using Oculus.Avatar2;
using UnityEngine;
using UnityEngine.XR;

// First-person body for the local Quest player: drives a Humanoid character from the Meta XR camera rig.
// The headset places and turns the head (the eye camera stays where the headset puts it and the character
// is moved so its eyes sit on it), the hand anchors drive the hands through Humanoid IK, and the feet stay
// on the floor, so the knees bend when the player crouches.
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

    [Header("Character")]
    [Tooltip("Eye height above the feet in the default pose, in metres.")]
    public float restEyeHeight = 1.622f;
    [Tooltip("How far the eyes sit in front of the Head bone, in metres.")]
    public float eyeForward = 0.09f;
    [Tooltip("Shown to everyone else but would block the player's own view; shadows-only while a headset is active.")]
    public Renderer[] hideInFirstPerson;
    [Tooltip("The local Meta Avatar. Its own body is hidden while a headset is active; it keeps tracking and streaming.")]
    public OvrAvatarEntity metaAvatar;

    [Header("Tuning")]
    [Tooltip("How quickly the body turns to follow the head.")]
    public float bodyTurnSpeed = 5f;
    [Tooltip("How far the body may sink below standing height when the player crouches, in metres.")]
    public float maxCrouch = 0.7f;
    [Tooltip("Wrist position relative to the hand anchor, in the anchor's space.")]
    public Vector3 leftHandOffset = new Vector3(0f, 0f, -0.06f);
    public Vector3 rightHandOffset = new Vector3(0f, 0f, -0.06f);
    [Range(0f, 1f)] public float neckShare = 0.35f;
    [Tooltip("Editor testing: drive the body from the anchors even with no headset (move the anchors by hand).")]
    public bool driveWithoutHeadset;

    private struct Hand
    {
        public Transform bone;
        public Quaternion frameInv;   // inverse of the hand's (finger, palm) frame in its own bone space
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

        // The bones still hold the imported standing pose here (arms down, palms facing the legs).
        Transform lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot), rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        leftFootLocal = transform.InverseTransformPoint(lf.position);
        rightFootLocal = transform.InverseTransformPoint(rf.position);
        leftFootRot = Quaternion.Inverse(transform.rotation) * lf.rotation;
        rightFootRot = Quaternion.Inverse(transform.rotation) * rf.rotation;
        left = MakeHand(HumanBodyBones.LeftHand, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, transform.right);
        right = MakeHand(HumanBodyBones.RightHand, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, -transform.right);
    }

    private Hand MakeHand(HumanBodyBones hand, HumanBodyBones finger, HumanBodyBones fallbackFinger, Vector3 palmWorld)
    {
        Transform bone = animator.GetBoneTransform(hand);
        Transform f = animator.GetBoneTransform(fallbackFinger) ?? animator.GetBoneTransform(finger);
        Vector3 fingerLocal = bone.InverseTransformDirection(f.position - bone.position).normalized;
        Vector3 palmLocal = bone.InverseTransformDirection(palmWorld).normalized;
        return new Hand { bone = bone, frameInv = Quaternion.Inverse(Quaternion.LookRotation(fingerLocal, palmLocal)) };
    }

    private void Update()
    {
        driving = (XRSettings.isDeviceActive || driveWithoutHeadset) && eyeAnchor != null;
        SetFirstPersonHidden(driving);
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
        Vector3 p = transform.position;
        p.y = FloorY + sink;
        transform.position = p;
    }

    private void OnAnimatorIK(int layerIndex)
    {
        float w = driving ? 1f : 0f;
        SetHand(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, leftHandAnchor, leftHandOffset, -1f, w);
        SetHand(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, rightHandAnchor, rightHandOffset, 1f, w);
        PlantFoot(AvatarIKGoal.LeftFoot, AvatarIKHint.LeftKnee, leftFootLocal, w);
        PlantFoot(AvatarIKGoal.RightFoot, AvatarIKHint.RightKnee, rightFootLocal, w);
    }

    private void SetHand(AvatarIKGoal goal, AvatarIKHint hint, Transform anchor, Vector3 offset, float side, float w)
    {
        if (anchor == null) w = 0f;
        animator.SetIKPositionWeight(goal, w);
        animator.SetIKRotationWeight(goal, 0f); // rotation is set on the bone in LateUpdate
        animator.SetIKHintPositionWeight(hint, w);
        if (w <= 0f) return;
        animator.SetIKPosition(goal, anchor.TransformPoint(offset));
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

        // Hands: fingers along the anchor's forward, palms facing inward (as when holding a controller).
        AimHand(left, leftHandAnchor, leftHandAnchor != null ? leftHandAnchor.right : Vector3.zero);
        AimHand(right, rightHandAnchor, rightHandAnchor != null ? -rightHandAnchor.right : Vector3.zero);
    }

    private static void AimHand(Hand hand, Transform anchor, Vector3 palm)
    {
        if (anchor == null || hand.bone == null) return;
        hand.bone.rotation = Quaternion.LookRotation(anchor.forward, palm) * hand.frameInv;
    }

    private void SetFirstPersonHidden(bool hide)
    {
        if (hide == hiddenApplied) return;
        hiddenApplied = hide;
        if (hideInFirstPerson != null)
            foreach (var r in hideInFirstPerson)
                if (r != null) r.shadowCastingMode = hide ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
        if (metaAvatar != null) metaAvatar.Hidden = hide;
    }

    private void OnDisable() => SetFirstPersonHidden(false);
}
