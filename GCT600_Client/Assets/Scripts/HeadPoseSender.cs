using System;
using GCT600.AvatarNetworking;
using UnityEngine;

// Sends only the headset pose (centerEyeAnchor position + rotation) on its own relay channel,
// using the same MAV1 packet layout as MetaAvatarSender.
[DisallowMultipleComponent]
public sealed class HeadPoseSender : MonoBehaviour
{
    // Encode rejects an empty avatar payload, so head packets carry this one marker byte.
    // A head packet is therefore always HeaderBytes + 1 = 45 bytes long.
    public const byte HeadPacketMarker = 0x48; // 'H'
    private static readonly byte[] HeadPayload = { HeadPacketMarker };

    [Header("Head")]
    [Tooltip("OVRCameraRig > TrackingSpace > CenterEyeAnchor. Found automatically when empty.")]
    public Transform centerEyeAnchor;
    [Tooltip("Optional shared-space origin, the same one MetaAvatarSender uses. Null means Unity world space.")]
    public Transform referenceSpace;

    [Header("Relay")]
    public string serverAddress = "127.0.0.1";
    public int serverPort = 5060;
    public string channel = "head-A";
    [Range(1, 60)] public int sendRate = 30;

    [Header("Runtime status")]
    [SerializeField] private string connectionStatus;
    [SerializeField] private int packetsQueued;
    [SerializeField] private int lastPacketBytes;

    private MetaAvatarRelayConnection connection;
    private float nextSend;
    private uint sequence;
    private bool errorReported;

    private void OnEnable()
    {
        if (centerEyeAnchor == null) centerEyeAnchor = FindCenterEyeAnchor();
        if (centerEyeAnchor == null)
        {
            Debug.LogError("[HeadPoseSender] Assign the OVRCameraRig CenterEyeAnchor.", this);
            enabled = false;
            return;
        }
        nextSend = 0;
        errorReported = false;
        try { connection = new MetaAvatarRelayConnection(serverAddress, serverPort, channel, true); }
        catch (Exception ex)
        {
            Debug.LogError("[HeadPoseSender] " + ex.Message, this);
            enabled = false;
        }
    }

    private static Transform FindCenterEyeAnchor()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig == null) return null;
        // The rig fills centerEyeAnchor in its own Awake, which may not have run yet.
        return rig.centerEyeAnchor != null ? rig.centerEyeAnchor : rig.transform.Find("TrackingSpace/CenterEyeAnchor");
    }

    private void LateUpdate()
    {
        connectionStatus = connection?.Status ?? "Stopped";
        if (connection == null || !connection.Connected) return;
        float now = Time.unscaledTime;
        if (now < nextSend) return;
        // Step from the previous target, not from now, so frame quantization does not lower the rate
        // (now + 1/30 at 72 fps sends every 3rd frame = 24 Hz). Resync after a stall.
        float interval = 1f / Mathf.Clamp(sendRate, 1, 60);
        nextSend = now - nextSend > interval ? now + interval : nextSend + interval;
        try
        {
            Vector3 position = centerEyeAnchor.position;
            Quaternion rotation = centerEyeAnchor.rotation;
            if (referenceSpace != null)
            {
                position = referenceSpace.InverseTransformPoint(position);
                rotation = Quaternion.Inverse(referenceSpace.rotation) * rotation;
            }
            float[] values = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
            byte[] packet = MetaAvatarNetworkProtocol.Encode(sequence++, Time.realtimeSinceStartupAsDouble, values, HeadPayload);
            connection.Publish(packet);
            packetsQueued++;
            lastPacketBytes = packet.Length;
            errorReported = false;
        }
        catch (Exception ex)
        {
            if (!errorReported) Debug.LogError("[HeadPoseSender] " + ex.Message, this);
            errorReported = true;
        }
    }

    private void OnDisable()
    {
        connection?.Dispose();
        connection = null;
        connectionStatus = "Stopped";
    }
}
