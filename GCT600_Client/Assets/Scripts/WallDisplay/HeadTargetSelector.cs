using Oculus.Avatar2;
using UnityEngine;

// Picks the line's end point. Auto: fresh head channel > streamed avatar Head joint > fallback.
// Runs after HeadPoseReceiver.Update so IsFresh is current; CameraToHeadLine reads the target in LateUpdate.
[DefaultExecutionOrder(100)]
public class HeadTargetSelector : MonoBehaviour
{
    public enum Mode { Auto, HeadChannel, AvatarJoint, Fake }
    private enum Source { None, HeadChannel, AvatarJoint, Fake }

    public CameraToHeadLine line;
    public HeadPoseReceiver headReceiver;
    public OvrAvatarEntity avatar;
    public Transform fallback;
    public Mode mode = Mode.Auto;

    private Transform avatarHead;
    private Source currentSource = (Source)(-1); // Forces the first log line.
    private bool missingCriticalJointReported;

    void Update()
    {
        if (line == null) return;

        Transform target;
        Source source = Select(out target);
        line.target = target;

        if (source != currentSource)
        {
            Debug.Log($"[{Label}] head source: {source}", this);
            currentSource = source;
        }
    }

    private Source Select(out Transform target)
    {
        bool channelReady = headReceiver != null && headReceiver.target != null && headReceiver.IsFresh;
        Transform joint = mode == Mode.Auto || mode == Mode.AvatarJoint ? FindStreamedAvatarHead() : null;

        switch (mode)
        {
            case Mode.HeadChannel:
                target = channelReady ? headReceiver.target : null;
                return channelReady ? Source.HeadChannel : Source.None;
            case Mode.AvatarJoint:
                target = joint;
                return joint != null ? Source.AvatarJoint : Source.None;
            case Mode.Fake:
                target = fallback;
                return fallback != null ? Source.Fake : Source.None;
        }

        if (channelReady) { target = headReceiver.target; return Source.HeadChannel; }
        if (joint != null) { target = joint; return Source.AvatarJoint; }
        target = fallback;
        return fallback != null ? Source.Fake : Source.None;
    }

    // Returns the Head joint only when the skeleton is loaded and a streamed pose is valid.
    private Transform FindStreamedAvatarHead()
    {
        if (avatar == null) return null;

        if (!avatar.HasCriticalJoint(CAPI.ovrAvatar2JointType.Head))
        {
            if (!missingCriticalJointReported)
            {
                Debug.LogWarning($"[{Label}] '{avatar.name}' has no Head in Critical Joint Types " +
                    "(OvrAvatarEntity > Skeleton Output). Its Head joint cannot be used; falling back.", this);
                missingCriticalJointReported = true;
            }
            return null;
        }

        if (!avatar.IsCreated || avatar.IsLocal || !avatar.HasJoints ||
            avatar.CurrentState < OvrAvatarEntity.AvatarState.Skeleton)
            return null;

        CAPI.ovrAvatar2StreamingPlaybackState? playback = avatar.GetStreamingPlaybackState();
        if (!playback.HasValue || !playback.Value.poseValid) return null;

        // Teardown destroys joint Transforms, and NaN poses are reported by the SDK; look up again when invalid.
        if (!IsValid(avatarHead)) avatarHead = avatar.GetSkeletonTransform(CAPI.ovrAvatar2JointType.Head);
        return IsValid(avatarHead) ? avatarHead : null;
    }

    private static bool IsValid(Transform t)
    {
        if (t == null) return false;
        Vector3 p = t.position;
        return !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                 float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
    }

    // "Person_A" -> "A"
    private string Label => name.StartsWith("Person_") ? name.Substring("Person_".Length) : name;
}
