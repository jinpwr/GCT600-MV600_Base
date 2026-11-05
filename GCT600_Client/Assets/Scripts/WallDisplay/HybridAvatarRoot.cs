using UnityEngine;

// Moves a webcam-driven Mixamo avatar so its Head bone sits on the Quest head while HeadPoseReceiver is fresh.
// Position only: body pose and root rotation stay with MixamoRetargeter. When the head data is stale, does nothing.
// MixamoRetargeter writes bones and root in LateUpdate (order 0), so this runs after it; CameraToHeadLine runs later still.
[DefaultExecutionOrder(100)]
[DisallowMultipleComponent]
public class HybridAvatarRoot : MonoBehaviour
{
    private enum Source { Webcam, QuestHead }

    public Animator animator;
    public HeadPoseReceiver headReceiver;
    [Tooltip("Point on the head to align with the Quest head, in the Head bone's local space.")]
    public Vector3 headOffset = Vector3.zero;

    public Transform HeadBone
    {
        get
        {
            if (headBone == null && animator != null && animator.isHuman)
                headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            return headBone;
        }
    }

    private Transform headBone;
    private Source currentSource = (Source)(-1); // Forces the first log line.

    void Reset()
    {
        animator = GetComponent<Animator>();
    }

    void LateUpdate()
    {
        Transform head = HeadBone;
        bool questReady = head != null && headReceiver != null && headReceiver.target != null && headReceiver.IsFresh;

        if (questReady)
        {
            Vector3 target = headReceiver.target.position;
            Vector3 current = head.position + head.TransformVector(headOffset);
            transform.position += target - current;
        }

        Source source = questReady ? Source.QuestHead : Source.Webcam;
        if (source != currentSource)
        {
            Debug.Log($"[{Label}] root source: {source}", this);
            currentSource = source;
        }
    }

    // "MixamoAvatar_A" -> "A"
    private string Label => name.StartsWith("MixamoAvatar_") ? name.Substring("MixamoAvatar_".Length) : name;
}
