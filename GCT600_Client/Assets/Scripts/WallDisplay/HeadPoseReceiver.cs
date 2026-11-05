using System;
using GCT600.AvatarNetworking;
using UnityEngine;

// Receives a head pose (MAV1 header only) from the relay and moves target. Avatar data after the header is ignored.
// The relay connection runs on a worker thread; the Transform is changed only in Update, like MetaAvatarReceiver.
[DisallowMultipleComponent]
public sealed class HeadPoseReceiver : MonoBehaviour
{
    [Header("Relay")]
    public string serverAddress = "127.0.0.1";
    public int serverPort = 5060;
    public string channel = "head-A";

    [Header("Pose")]
    [Tooltip("Maps the sender's reference space into this scene, same as MetaAvatarReceiver.displayOrigin.")]
    public Transform displayOrigin;
    public Transform target;
    public float staleSeconds = 0.5f;

    [Header("Runtime status")]
    [SerializeField] private string connectionStatus;
    [SerializeField] private uint lastSequence;
    [SerializeField] private int droppedCount;

    public bool IsFresh => lastPoseTime >= 0 && Time.unscaledTime - lastPoseTime <= staleSeconds;
    public uint LastSequence => lastSequence;
    public int DroppedCount => droppedCount;

    private MetaAvatarRelayConnection connection;
    private Renderer targetRenderer;
    private float lastPoseTime = -1;
    private bool hasSequence;
    private bool wasConnected;
    private bool failureReported;
    private bool errorReported;

    private void OnEnable()
    {
        lastPoseTime = -1;
        hasSequence = false;
        wasConnected = false;
        failureReported = false;
        errorReported = false;
        try { connection = new MetaAvatarRelayConnection(serverAddress, serverPort, channel, false); }
        catch (Exception ex)
        {
            Debug.LogError($"[HeadPoseReceiver {channel}] " + ex.Message, this);
            enabled = false;
        }
    }

    private void Update()
    {
        connectionStatus = connection?.Status ?? "Stopped";
        ReportConnectionChange();

        byte[] latest = null;
        if (connection != null && connection.Connected) connection.TryTake(out latest);
        if (latest != null) ApplyPacket(latest);

        if (target != null)
        {
            if (targetRenderer == null || targetRenderer.transform != target) targetRenderer = target.GetComponent<Renderer>();
            if (targetRenderer != null) targetRenderer.enabled = IsFresh;
        }
    }

    private void ApplyPacket(byte[] bytes)
    {
        try
        {
            var packet = MetaAvatarNetworkProtocol.Decode(bytes);

            // A smaller sequence means the sender restarted; only forward gaps count as drops.
            if (hasSequence && packet.Sequence > lastSequence + 1)
                droppedCount += (int)(packet.Sequence - lastSequence - 1);
            lastSequence = packet.Sequence;
            hasSequence = true;

            // Same conversion as MetaAvatarReceiver.Update (root pose + displayOrigin).
            float[] root = packet.Root;
            Vector3 position = new Vector3(root[0], root[1], root[2]);
            Quaternion rotation = new Quaternion(root[3], root[4], root[5], root[6]).normalized;
            if (displayOrigin != null)
            {
                position = displayOrigin.TransformPoint(position);
                rotation = displayOrigin.rotation * rotation;
            }
            if (target != null) target.SetPositionAndRotation(position, rotation);

            lastPoseTime = Time.unscaledTime;
            errorReported = false;
        }
        catch (Exception ex)
        {
            if (!errorReported) Debug.LogWarning($"[HeadPoseReceiver {channel}] Bad packet: " + ex.Message, this);
            errorReported = true;
        }
    }

    // One line per state change: connected, disconnected, or first failed attempt.
    private void ReportConnectionChange()
    {
        bool connected = connection != null && connection.Connected;
        if (connected != wasConnected)
        {
            if (connected) Debug.Log($"[HeadPoseReceiver {channel}] Connected to {serverAddress}:{serverPort}", this);
            else Debug.LogWarning($"[HeadPoseReceiver {channel}] Disconnected. Retrying in the background.", this);
            wasConnected = connected;
            failureReported = !connected;
            return;
        }
        if (!connected && !failureReported && connectionStatus.StartsWith("Retrying"))
        {
            Debug.LogWarning($"[HeadPoseReceiver {channel}] Connection failed ({connectionStatus}). Retrying in the background.", this);
            failureReported = true;
        }
    }

    private void OnDisable()
    {
        connection?.Dispose();
        connection = null;
        connectionStatus = "Stopped";
    }
}
