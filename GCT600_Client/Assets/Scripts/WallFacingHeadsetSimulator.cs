using Oculus.Avatar2;
using UnityEngine;

// Editor-only (no headset) head pose for the local Meta Avatar. The sample simulator turns
// the avatar 180 degrees to face a camera; this keeps its keyboard controls but faces the
// rig's forward (toward the wall). Has no effect when a headset is connected.
// Assign to SampleInputManager > Headset Input Simulator.
public sealed class WallFacingHeadsetSimulator : BaseHeadsetInputSimulator
{
    [Tooltip("Extra yaw applied to the sample simulator. 180 cancels its built-in turn.")]
    public float yawOffset = 180f;

    private void Awake()
    {
        _inputTrackingDelegate = new YawOffsetDelegate(this);
    }

    private sealed class YawOffsetDelegate : OvrAvatarInputTrackingDelegate
    {
        private readonly SampleAvatarHeadsetInputSimulator inner = new SampleAvatarHeadsetInputSimulator();
        private readonly WallFacingHeadsetSimulator owner;

        public YawOffsetDelegate(WallFacingHeadsetSimulator owner) { this.owner = owner; }

        public override bool GetRawInputTrackingState(out OvrAvatarInputTrackingState inputTrackingState)
        {
            if (!inner.GetRawInputTrackingState(out inputTrackingState)) return false;
            Quaternion orientation = inputTrackingState.headset.orientation;
            inputTrackingState.headset.orientation = Quaternion.Euler(0f, owner.yawOffset, 0f) * orientation;
            return true;
        }
    }
}
