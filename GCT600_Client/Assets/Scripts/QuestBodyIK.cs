using Oculus.Avatar2;
using UnityEngine;
using UnityEngine.XR;

// First-person body for the local Quest player: drives a Humanoid character from the Meta XR camera rig.
// The headset places and turns the head (the eye camera stays where the headset puts it and the character
// is moved so its eyes sit on it). The hands follow Meta's hand skeletons (the Hand Tracking building
// blocks): wrist position and rotation and every finger joint, from hand tracking or, with OVR Manager >
// Controller Driven Hand Poses set to Natural, from the controllers. A hand without skeleton data follows
// its controller anchor with the fingers straight and open. The feet stay on the floor, so the knees bend
// when the player crouches. With a WebcamLegs component receiving fresh webcam data, the legs and body
// height come from the webcam instead.
// Needs an Animator Controller with IK Pass on. With no headset active (Editor without Link) the character
// stands under the rig in its default pose and nothing is hidden.
[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(50)]
[DisallowMultipleComponent]
public sealed class QuestBodyIK : MonoBehaviour
{
    [Header("Meta XR camera rig")]
    public Transform eyeAnchor;
    public Transform leftHandAnchor;
    public Transform rightHandAnchor;
    [Tooltip("The rig's tracking space. Its Y is the floor.")]
    public Transform trackingSpace;
    [Tooltip("Meta hand skeletons (the Hand Tracking building blocks). Found automatically when empty.")]
    public OVRSkeleton leftHandSkeleton;
    public OVRSkeleton rightHandSkeleton;

    [Header("Character")]
    [Tooltip("Eye height above the feet in the default pose, in metres.")]
    public float restEyeHeight = 1.622f;
    [Tooltip("How far the eyes sit in front of the Head bone, in metres.")]
    public float eyeForward = 0.09f;
    [Tooltip("Shown to everyone else but would block the player's own view; shadows-only while a headset is active.")]
    public Renderer[] hideInFirstPerson;
    [Tooltip("The local Meta Avatar. Its own body is hidden while a headset is active; it keeps tracking and streaming.")]
    public OvrAvatarEntity metaAvatar;
    [Tooltip("Hide Meta's own hand meshes while a headset is active, so only the character's hands show.")]
    public bool hideMetaHandMeshes = true;
    [Tooltip("Optional. While it has fresh webcam data, it drives the legs and body height instead of the planted feet.")]
    public WebcamLegs webcamLegs;

    [Header("Tuning")]
    [Tooltip("How quickly the body turns to follow the head.")]
    public float bodyTurnSpeed = 5f;
    [Tooltip("How far the body may sink below standing height when the player crouches, in metres.")]
    public float maxCrouch = 0.7f;
    [Tooltip("Wrist position relative to the controller anchor, used when a hand has no skeleton data.")]
    public Vector3 leftHandOffset = new Vector3(0f, 0f, -0.06f);
    public Vector3 rightHandOffset = new Vector3(0f, 0f, -0.06f);
    [Range(0f, 1f)] public float neckShare = 0.35f;
    [Tooltip("How far each arm may stretch along its length when the hand is out of reach, in metres. " +
             "The character's arms are shorter than most players'.")]
    public float maxArmStretch = 0.25f;
    [Tooltip("Editor testing: drive the body from the anchors even with no headset (move the anchors by hand).")]
    public bool driveWithoutHeadset;

    // Meta hand joint ids, per skeleton flavour: wrist, index/middle/little proximal, then for the 15 finger
    // bones (thumb, index, middle, ring, little x proximal, intermediate, distal) the joint pair whose line
    // gives that bone's direction.
    private static readonly int[] XrFrame = { 1, 7, 12, 22 };
    private static readonly int[] XrPairs = { 2, 3, 3, 4, 4, 5, 7, 8, 8, 9, 9, 10, 12, 13, 13, 14, 14, 15,
                                              17, 18, 18, 19, 19, 20, 22, 23, 23, 24, 24, 25 };
    private static readonly int[] LegacyFrame = { 0, 6, 9, 16 };
    private static readonly int[] LegacyPairs = { 3, 4, 4, 5, 5, 19, 6, 7, 7, 8, 8, 20, 9, 10, 10, 11, 11, 21,
                                                  12, 13, 13, 14, 14, 22, 16, 17, 17, 18, 18, 23 };

    private sealed class Hand
    {
        public Transform anchor;
        public Vector3 anchorOffset;
        public OVRSkeleton skeleton;
        public Transform bone;
        public Quaternion frameInv;   // the hand's (knuckles, palm-normal) frame, inverted, in the hand bone's space
        public readonly Transform[] fingers = new Transform[15];
        public readonly Transform[] fingerChild = new Transform[15];
        public readonly Quaternion[] fingerRest = new Quaternion[15];  // straight, open (rest pose)
        public Transform[] joints;    // Meta skeleton joints by id
        public int jointCount;
        public bool openXR;

        public bool testJoints;       // joints supplied by SetTestJoints (Editor testing without a headset)

        public bool Tracked => joints != null && (testJoints || (skeleton != null && skeleton.IsInitialized && skeleton.IsDataValid));
        public int[] Frame => openXR ? XrFrame : LegacyFrame;
        public int[] Pairs => openXR ? XrPairs : LegacyPairs;
        public Transform J(int id) => joints[id];

        public bool HasTarget => Tracked || anchor != null;
        public Vector3 Target => Tracked ? J(Frame[0]).position : anchor.TransformPoint(anchorOffset);
    }

    private Animator animator;
    private Transform head, neck;
    private Hand left, right;
    private Vector3 leftFootLocal, rightFootLocal;          // rest-pose ankles, in root space
    private Quaternion leftFootRot, rightFootRot;
    private Vector3 eyeInHead;    // eye point in the Head bone's space, measured from the first animated pose
    private bool calibrated;
    private bool driving;
    private bool hiddenApplied;

    private float FloorY => trackingSpace != null ? trackingSpace.position.y : 0f;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        neck = animator.GetBoneTransform(HumanBodyBones.Neck);

        // The bones still hold the imported rest pose here (T-pose: legs straight, fingers straight).
        Transform lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot), rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        leftFootLocal = transform.InverseTransformPoint(lf.position);
        rightFootLocal = transform.InverseTransformPoint(rf.position);
        leftFootRot = Quaternion.Inverse(transform.rotation) * lf.rotation;
        rightFootRot = Quaternion.Inverse(transform.rotation) * rf.rotation;

        if (leftHandSkeleton == null || rightHandSkeleton == null)
            foreach (var s in FindObjectsByType<OVRSkeleton>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var t = s.GetSkeletonType();
                if (leftHandSkeleton == null && (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft)) leftHandSkeleton = s;
                if (rightHandSkeleton == null && (t == OVRSkeleton.SkeletonType.HandRight || t == OVRSkeleton.SkeletonType.XRHandRight)) rightHandSkeleton = s;
            }
        left = MakeHand(true);
        right = MakeHand(false);
    }

    private Hand MakeHand(bool isLeft)
    {
        var h = new Hand
        {
            anchor = isLeft ? leftHandAnchor : rightHandAnchor,
            anchorOffset = isLeft ? leftHandOffset : rightHandOffset,
            skeleton = isLeft ? leftHandSkeleton : rightHandSkeleton,
            bone = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand),
        };
        int first = (int)(isLeft ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
        for (int i = 0; i < 15; i++)
        {
            Transform f = animator.GetBoneTransform((HumanBodyBones)(first + i));
            h.fingers[i] = f;
            if (f == null) continue;
            h.fingerRest[i] = f.localRotation;
            // Distal bones point at their end bone (e.g. ...Index4) when the rig has one.
            h.fingerChild[i] = i % 3 < 2 ? animator.GetBoneTransform((HumanBodyBones)(first + i + 1)) : (f.childCount > 0 ? f.GetChild(0) : null);
        }
        // Hand frame from the same landmarks as Meta's: wrist -> middle knuckle, and the normal of the
        // wrist / index knuckle / little knuckle triangle.
        Transform idx = h.fingers[3], mid = h.fingers[6], lit = h.fingers[12];
        if (h.bone != null && idx != null && mid != null && lit != null)
        {
            Vector3 w = h.bone.position;
            Vector3 fwd = h.bone.InverseTransformDirection(mid.position - w);
            Vector3 nrm = h.bone.InverseTransformDirection(Vector3.Cross(idx.position - w, lit.position - w));
            h.frameInv = Quaternion.Inverse(Quaternion.LookRotation(fwd, nrm));
        }
        return h;
    }

    private void Update()
    {
        driving = (XRSettings.isDeviceActive || driveWithoutHeadset) && eyeAnchor != null;
        SetFirstPersonHidden(driving);
        RefreshJoints(left);
        RefreshJoints(right);
        if (!driving) return;

        // Turn the body toward where the head faces (yaw only).
        Vector3 fwd = Vector3.ProjectOnPlane(eyeAnchor.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(eyeAnchor.up, Vector3.up);
        if (fwd.sqrMagnitude > 1e-4f)
        {
            Quaternion target = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-bodyTurnSpeed * Time.deltaTime));
        }

        // Stand on the floor; sink (and let foot IK bend the knees) when the eyes drop below standing height.
        float sink = Mathf.Clamp(eyeAnchor.position.y - FloorY - restEyeHeight, -maxCrouch, 0f);
        // With webcam legs, the height that keeps the lower foot on the floor takes over.
        float legs = LegsWeight;
        if (legs > 0f) sink = Mathf.Lerp(sink, Mathf.Clamp(webcamLegs.RootHeight, -maxCrouch, 0.5f), legs);
        Vector3 p = transform.position;
        p.y = FloorY + sink;
        transform.position = p;
    }

    /// <summary>Editor testing without a headset: drive a hand from joints laid out like Meta's OpenXR hand
    /// skeleton (index = OpenXR joint id). Pass null to go back to the real skeleton.</summary>
    public void SetTestJoints(bool isLeft, Transform[] openXRJoints)
    {
        Hand h = isLeft ? left : right;
        h.testJoints = openXRJoints != null;
        h.joints = openXRJoints;
        h.openXR = true;
    }

    private static void RefreshJoints(Hand h)
    {
        if (h.testJoints) return;
        var s = h.skeleton;
        if (s == null || !s.IsInitialized || s.Bones == null) { h.joints = null; return; }
        if (h.joints != null && h.jointCount == s.Bones.Count) return;
        var t = s.GetSkeletonType();
        h.openXR = t == OVRSkeleton.SkeletonType.XRHandLeft || t == OVRSkeleton.SkeletonType.XRHandRight;
        h.joints = new Transform[64];
        foreach (var b in s.Bones)
        {
            int id = (int)b.Id;
            if (id >= 0 && id < h.joints.Length) h.joints[id] = b.Transform;
        }
        h.jointCount = s.Bones.Count;
        foreach (int id in h.Pairs) if (h.joints[id] == null) { h.joints = null; return; }
        foreach (int id in h.Frame) if (h.joints[id] == null) { h.joints = null; return; }
    }

    private void OnAnimatorIK(int layerIndex)
    {
        float w = driving ? 1f : 0f;
        SetHandIK(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, left, -1f, w);
        SetHandIK(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, right, 1f, w);
        float feet = w * (1f - LegsWeight); // the webcam legs replace the planted feet
        PlantFoot(AvatarIKGoal.LeftFoot, AvatarIKHint.LeftKnee, leftFootLocal, feet);
        PlantFoot(AvatarIKGoal.RightFoot, AvatarIKHint.RightKnee, rightFootLocal, feet);
    }

    private float LegsWeight => webcamLegs != null && webcamLegs.isActiveAndEnabled ? webcamLegs.Weight : 0f;

    private void SetHandIK(AvatarIKGoal goal, AvatarIKHint hint, Hand hand, float side, float w)
    {
        if (!hand.HasTarget) w = 0f;
        animator.SetIKPositionWeight(goal, w);
        animator.SetIKRotationWeight(goal, 0f); // rotation is set on the bone in LateUpdate
        animator.SetIKHintPositionWeight(hint, w);
        if (w <= 0f) return;
        animator.SetIKPosition(goal, hand.Target);
        // Elbows out to the side and slightly back and down.
        Vector3 shoulder = animator.GetBoneTransform(side < 0 ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm).position;
        animator.SetIKHintPosition(hint, shoulder + transform.rotation * new Vector3(0.25f * side, -0.35f, -0.25f));
    }

    private void PlantFoot(AvatarIKGoal goal, AvatarIKHint knee, Vector3 restLocal, float w)
    {
        animator.SetIKPositionWeight(goal, w);
        animator.SetIKRotationWeight(goal, 0f); // flat-foot rotation is set on the bone in LateUpdate
        animator.SetIKHintPositionWeight(knee, w);
        if (w <= 0f) return;
        // Keep the standing stance under the body, but on the floor even while the body sinks.
        Vector3 pos = transform.TransformPoint(restLocal);
        pos.y = FloorY + restLocal.y;
        animator.SetIKPosition(goal, pos);
        animator.SetIKHintPosition(knee, pos + transform.forward * 0.6f + Vector3.up * 0.5f); // knees bend forward
    }

    private void LateUpdate()
    {
        if (head == null) return;
        if (!calibrated)
        {
            // First animated frame: the default pose, standing on the floor at the root.
            Vector3 eye = new Vector3(head.position.x, transform.position.y + restEyeHeight, head.position.z)
                          + transform.forward * eyeForward;
            eyeInHead = head.InverseTransformPoint(eye);
            calibrated = true;
        }
        if (!driving) return;

        // Head (and part of the neck) take the headset's rotation, relative to the animated pose.
        Quaternion headRel = Quaternion.Inverse(transform.rotation) * head.rotation;
        Quaternion desired = eyeAnchor.rotation * headRel;
        if (neck != null)
        {
            Quaternion delta = desired * Quaternion.Inverse(head.rotation);
            neck.rotation = Quaternion.Slerp(Quaternion.identity, delta, neckShare) * neck.rotation;
        }
        head.rotation = desired;

        // Slide the body sideways so the eyes land on the headset (height is handled by crouching).
        Vector3 shift = eyeAnchor.position - head.TransformPoint(eyeInHead);
        shift.y = 0f;
        transform.position += shift;

        // Feet: flat on the floor as in the standing pose, whatever the knees are doing.
        Transform lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot), rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        if (lf != null) lf.rotation = transform.rotation * leftFootRot;
        if (rf != null) rf.rotation = transform.rotation * rightFootRot;

        // Arms: when a hand is out of reach, lengthen the arm along its line so the hand still lands on target.
        StretchArm(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, left);
        StretchArm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, right);

        PoseHand(left);
        PoseHand(right);
    }

    private void StretchArm(HumanBodyBones upperBone, HumanBodyBones lowerBone, Hand h)
    {
        if (!h.HasTarget || h.bone == null || maxArmStretch <= 0f) return;
        Transform upper = animator.GetBoneTransform(upperBone), lower = animator.GetBoneTransform(lowerBone);
        if (upper == null || lower == null) return;
        Vector3 target = h.Target;
        Vector3 dir = (target - upper.position).normalized;
        float extra = Mathf.Clamp(Vector3.Dot(target - h.bone.position, dir), 0f, maxArmStretch);
        if (extra < 0.002f) return;
        // Half to the upper arm, half to the forearm (the hand is a child of the forearm, so it moves with it).
        lower.position += dir * (extra * 0.5f);
        h.bone.position += dir * (extra * 0.5f);
    }

    // Wrist rotation and fingers from Meta's hand skeleton; without it, follow the controller with open fingers.
    private static void PoseHand(Hand h)
    {
        if (h.bone == null) return;
        for (int i = 0; i < 15; i++)
            if (h.fingers[i] != null) h.fingers[i].localRotation = h.fingerRest[i];

        if (!h.Tracked)
        {
            // Holding a controller: knuckles along its forward; the hand-frame normal points along its right for both hands.
            if (h.anchor != null) h.bone.rotation = Quaternion.LookRotation(h.anchor.forward, h.anchor.right) * h.frameInv;
            return;
        }

        int[] f = h.Frame;
        Vector3 w = h.J(f[0]).position;
        Vector3 fwd = h.J(f[2]).position - w;
        Vector3 nrm = Vector3.Cross(h.J(f[1]).position - w, h.J(f[3]).position - w);
        if (fwd.sqrMagnitude > 1e-8f && nrm.sqrMagnitude > 1e-10f)
            h.bone.rotation = Quaternion.LookRotation(fwd, nrm) * h.frameInv;

        int[] pairs = h.Pairs;
        for (int i = 0; i < 15; i++)
        {
            Transform b = h.fingers[i], c = h.fingerChild[i];
            if (b == null || c == null) continue;
            Vector3 target = h.J(pairs[2 * i + 1]).position - h.J(pairs[2 * i]).position;
            Vector3 current = c.position - b.position;
            if (target.sqrMagnitude < 1e-8f || current.sqrMagnitude < 1e-8f) continue;
            b.rotation = Quaternion.FromToRotation(current, target) * b.rotation;
        }
    }

    private void SetFirstPersonHidden(bool hide)
    {
        if (hide == hiddenApplied) return;
        hiddenApplied = hide;
        if (hideInFirstPerson != null)
            foreach (var r in hideInFirstPerson)
                if (r != null) r.shadowCastingMode = hide ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
        if (metaAvatar != null) metaAvatar.Hidden = hide;
        if (hideMetaHandMeshes)
            foreach (var s in new[] { leftHandSkeleton, rightHandSkeleton })
            {
                if (s == null) continue;
                foreach (var m in s.GetComponents<OVRMeshRenderer>()) m.enabled = !hide;
                foreach (var m in s.GetComponents<OVRSkeletonRenderer>()) m.enabled = !hide;
                foreach (var r in s.GetComponentsInChildren<Renderer>(true)) r.enabled = !hide;
            }
    }

    private void OnDisable() => SetFirstPersonHidden(false);
}
