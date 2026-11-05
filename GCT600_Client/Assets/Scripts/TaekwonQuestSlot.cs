using System;
using System.IO;
using UnityEngine;

// Picks which Quest this build is running as (A or B) and gives the avatar and head senders
// their channel names and one shared reference space. Runs before the senders connect.
// One build serves both headsets: push slot.txt containing "A" or "B" to
// Application.persistentDataPath on each Quest; without the file the Inspector value is used.
[DefaultExecutionOrder(-100)]
[DisallowMultipleComponent]
public sealed class TaekwonQuestSlot : MonoBehaviour
{
    public enum Slot { A, B }
    public const string SlotFileName = "slot.txt";

    [Tooltip("Used when slot.txt is missing or unreadable.")]
    public Slot slot = Slot.A;

    [Header("Relay")]
    [Tooltip("IP of the PC running avatar_relay.py, given to both senders. Empty keeps each sender's own value.")]
    public string serverAddress = "127.0.0.1";

    [Header("Relay channels (1-64 ASCII letters, digits, _ or -)")]
    public string avatarChannelA = "avatar-A";
    public string avatarChannelB = "avatar-B";
    public string headChannelA = "head-A";
    public string headChannelB = "head-B";

    [Header("Shared reference space")]
    [Tooltip("Given to every sender and the recorder. Leave empty (Unity world space) until calibration provides it.")]
    public Transform referenceSpace;

    [Header("Targets")]
    public MetaAvatarSender avatarSender;
    public HeadPoseSender headSender;
    public CalibrationPoseRecorder calibrationRecorder;

    [Header("Runtime status")]
    [SerializeField] private Slot activeSlot;
    [SerializeField] private string slotSource;

    public Slot ActiveSlot => activeSlot;

    private void Awake()
    {
        activeSlot = ReadSlotFile(out slotSource);
        bool isA = activeSlot == Slot.A;
        bool setAddress = !string.IsNullOrWhiteSpace(serverAddress);
        if (avatarSender != null)
        {
            avatarSender.channel = isA ? avatarChannelA : avatarChannelB;
            avatarSender.referenceSpace = referenceSpace;
            if (setAddress) avatarSender.serverAddress = serverAddress.Trim();
        }
        if (headSender != null)
        {
            headSender.channel = isA ? headChannelA : headChannelB;
            headSender.referenceSpace = referenceSpace;
            if (setAddress) headSender.serverAddress = serverAddress.Trim();
        }
        if (calibrationRecorder != null)
        {
            calibrationRecorder.slotLabel = activeSlot.ToString();
            calibrationRecorder.referenceSpace = referenceSpace;
        }
        Debug.Log($"[TaekwonQuestSlot] Slot {activeSlot} ({slotSource}): avatar={avatarSender?.channel} " +
                  $"head={headSender?.channel} relay={avatarSender?.serverAddress}", this);
    }

    private Slot ReadSlotFile(out string source)
    {
        string path = Path.Combine(Application.persistentDataPath, SlotFileName);
        try
        {
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path).Trim().ToUpperInvariant();
                if (text == "A" || text == "B")
                {
                    source = path;
                    return text == "A" ? Slot.A : Slot.B;
                }
                Debug.LogWarning($"[TaekwonQuestSlot] {path} must contain A or B, found '{text}'.", this);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TaekwonQuestSlot] Could not read slot file: " + ex.Message, this);
        }
        source = "Inspector";
        return slot;
    }
}
