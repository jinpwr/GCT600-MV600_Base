using UnityEngine;

// Drives a Humanoid character's legs from the webcam (MediaPipe Pose via WebcamPoseReceiver), so raising
// a knee or kicking shows on the character. Only the legs: the head and hands stay with QuestBodyIK.
// Each leg segment's direction is measured in the player's own hip/torso frame, so where the webcam stands
// does not matter. The body is then raised or lowered so the lower foot stays on the floor; QuestBodyIK
// reads Weight and RootHeight and stops planting the feet while the webcam legs are active.
// When frames go stale or the legs leave the camera's view, the legs blend back to QuestBodyIK's feet.
[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(60)] // after QuestBodyIK (50)
[DisallowMultipleComponent]
public sealed class WebcamLegs : MonoBehaviour
{
    public WebcamPoseReceiver pose;
    [Tooltip("The rig's tracking space. Its Y is the floor.")]
    public Transform trackingSpace;
    [Tooltip("Hip, knee, ankle and toe landmarks must all be at least this visible.")]
    [Range(0f, 1f)] public float minVisibility = 0.5f;
    [Tooltip("How quickly the legs follow new webcam frames (higher = snappier, more jitter).")]
    public float followSpeed = 14f;
    [Tooltip("How quickly the webcam legs fade in or out when tracking starts or stops.")]
    public float blendSpeed = 4f;

    /// <summary>0 = feet planted by QuestBodyIK, 1 = legs fully from the webcam.</summary>
    public float Weight { get; private set; }
    /// <summary>Root height above the floor that keeps the lower foot on the floor.</summary>
    public float RootHeight { get; private set; }

    // MediaPipe Pose landmark indices.
    private const int LShoulder = 11, RShoulder = 12, LHip = 23, RHip = 24, LKnee = 25, RKnee = 26,
                      LAnkle = 27, RAnkle = 28, LFootIndex = 31, RFootIndex = 32;

    private sealed class Segment
    {
        public HumanBodyBones bone, child;
        public int from, to;
        public Transform t;
        public Vector3 restDir;      // root space
        public Quaternion restRot;   // root space
        public Vector3 dir;          // smoothed target direction, player's body frame
        public bool hasDir;
    }

    private Animator animator;
    private Segment[] segments;
    private Transform leftFoot, rightFoot;
    private float ankleHeight;
    private float FloorY => trackingSpace != null ? trackingSpace.position.y : 0f;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        segments = new[]
        {
            Make(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, LHip, LKnee),
            Make(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, LKnee, LAnkle),
            Make(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, LAnkle, LFootIndex),
            Make(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, RHip, RKnee),
            Make(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, RKnee, RAnkle),
            Make(HumanBodyBones.RightFoot, HumanBodyBones.RightToes, RAnkle, RFootIndex),
        };
        leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        ankleHeight = leftFoot != null ? transform.InverseTransformPoint(leftFoot.position).y : 0.08f;
        RootHeight = 0f;
    }

    private Segment Make(HumanBodyBones bone, HumanBodyBones child, int from, int to)
    {
        // The bones still hold the imported standing pose here.
        Transform t = animator.GetBoneTransform(bone), c = animator.GetBoneTransform(child);
        if (t == null || c == null) return null;
        return new Segment
        {
            bone = bone, child = child, from = from, to = to, t = t,
            restDir = transform.InverseTransformDirection(c.position - t.position).normalized,
            restRot = Quaternion.Inverse(transform.rotation) * t.rotation,
        };
    }

    private void LateUpdate()
    {
        bool tracking = ReadPose(out Quaternion playerFrameInv, out var wl);
        Weight = Mathf.MoveTowards(Weight, tracking ? 1f : 0f, blendSpeed * Time.deltaTime);
        if (tracking)
        {
            float k = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            foreach (var s in segments)
            {
                if (s == null) continue;
                Vector3 d = playerFrameInv * (ToUnity(wl[s.to]) - ToUnity(wl[s.from]));
                if (d.sqrMagnitude < 1e-6f) continue;
                d.Normalize();
                s.dir = s.hasDir ? Vector3.Slerp(s.dir, d, k) : d;
                s.hasDir = true;
            }
        }
        if (Weight <= 0f) return;

        // Pose the legs top-down (parents first) in the character's upright body frame.
        Quaternion body = transform.rotation;
        foreach (var s in segments)
        {
            if (s == null || !s.hasDir) continue;
            Quaternion target = Quaternion.FromToRotation(body * s.restDir, body * s.dir) * (body * s.restRot);
            s.t.rotation = Quaternion.Slerp(s.t.rotation, target, Weight);
        }

        // Keep the lower foot on the floor: the height the root needs, used by QuestBodyIK next frame.
        if (leftFoot != null && rightFoot != null)
        {
            float lowest = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
            float rootAboveFloor = transform.position.y - FloorY;
            RootHeight = rootAboveFloor - (lowest - (FloorY + ankleHeight));
        }
    }

    private bool ReadPose(out Quaternion playerFrameInv, out System.Collections.Generic.List<Landmark> wl)
    {
        playerFrameInv = Quaternion.identity;
        wl = null;
        if (pose == null || !pose.IsFresh) return false;
        wl = pose.Latest.world_landmarks;
        if (wl == null || wl.Count < 33) return false;
        for (int i = LHip; i <= RFootIndex; i++)
            if (i != 29 && i != 30 && wl[i].visibility < minVisibility) return false; // heels (29, 30) unused

        // The player's own frame: right along the hips, up from hips to shoulders.
        Vector3 hipL = ToUnity(wl[LHip]), hipR = ToUnity(wl[RHip]);
        Vector3 up = (ToUnity(wl[LShoulder]) + ToUnity(wl[RShoulder])) * 0.5f - (hipL + hipR) * 0.5f;
        Vector3 right = hipR - hipL;
        Vector3 fwd = Vector3.Cross(right, up);
        if (fwd.sqrMagnitude < 1e-6f || up.sqrMagnitude < 1e-6f) return false;
        playerFrameInv = Quaternion.Inverse(Quaternion.LookRotation(fwd, up));
        return true;
    }

    // MediaPipe world landmarks: x = image right, y = down, z = away from the camera, in metres (right-handed).
    // Flipping y gives a left-handed frame with the same physical layout, like Unity's.
    private static Vector3 ToUnity(Landmark l) => new Vector3(l.x, -l.y, l.z);
}
