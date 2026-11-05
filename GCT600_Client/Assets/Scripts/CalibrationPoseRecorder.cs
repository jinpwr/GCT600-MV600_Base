using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

// Records head and hand anchor positions during the registration pose (first seconds of a
// session) to a CSV in Application.persistentDataPath/calibration on the device.
// quest_time uses the same clock as the MAV1 packets (Time.realtimeSinceStartupAsDouble).
[DisallowMultipleComponent]
public sealed class CalibrationPoseRecorder : MonoBehaviour
{
    [Header("Anchors (OVRCameraRig). Found automatically when empty.")]
    public Transform centerEyeAnchor;
    public Transform leftHandAnchor;
    public Transform rightHandAnchor;
    [Tooltip("Same reference space as the senders. Null means Unity world space.")]
    public Transform referenceSpace;

    [Header("Recording")]
    public float durationSeconds = 10f;
    [Tooltip("Start once the headset is tracking (immediately in the Editor without a headset).")]
    public bool startAutomatically = true;
    [Tooltip("Controller button that starts a new recording. None disables it.")]
    public OVRInput.Button restartButton = OVRInput.Button.One;
    [Tooltip("Written into the file name and header; TaekwonQuestSlot sets it.")]
    public string slotLabel = "A";
    [Tooltip("Optional; adds the recenter count to every row.")]
    public RecenterWatcher recenterWatcher;

    [Header("Runtime status")]
    [SerializeField] private bool recording;
    [SerializeField] private int rowsRecorded;
    [SerializeField] private string lastFilePath;

    public string LastFilePath => lastFilePath;

    private readonly StringBuilder rows = new StringBuilder();
    private double startTime;
    private DateTime startUtc;
    private bool autoStarted;

    private void Start()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            if (centerEyeAnchor == null) centerEyeAnchor = rig.centerEyeAnchor;
            if (leftHandAnchor == null) leftHandAnchor = rig.leftHandAnchor;
            if (rightHandAnchor == null) rightHandAnchor = rig.rightHandAnchor;
        }
        if (centerEyeAnchor == null || leftHandAnchor == null || rightHandAnchor == null)
        {
            Debug.LogError("[CalibrationPoseRecorder] Assign the OVRCameraRig center eye and hand anchors.", this);
            enabled = false;
        }
    }

    private void LateUpdate()
    {
        if (!recording)
        {
            bool headReady = !XRSettings.isDeviceActive || OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head);
            if (startAutomatically && !autoStarted && headReady)
            {
                autoStarted = true;
                StartRecording();
            }
            else if (restartButton != OVRInput.Button.None && OVRInput.GetDown(restartButton))
            {
                StartRecording();
            }
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        AppendRow(now);
        if (now - startTime >= durationSeconds) FinishRecording(false);
    }

    public void StartRecording()
    {
        if (recording) return;
        rows.Clear();
        rowsRecorded = 0;
        startTime = Time.realtimeSinceStartupAsDouble;
        startUtc = DateTime.UtcNow;
        recording = true;
        Debug.Log($"[CalibrationPoseRecorder] Recording registration pose for {durationSeconds:0.#} s.", this);
    }

    private void AppendRow(double questTime)
    {
        var c = CultureInfo.InvariantCulture;
        rows.Append(questTime.ToString("F6", c)).Append(',')
            .Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(c)).Append(',')
            .Append(Time.frameCount.ToString(c));
        AppendPosition(centerEyeAnchor, OVRPlugin.Node.Head);
        AppendPosition(leftHandAnchor, OVRPlugin.Node.HandLeft);
        AppendPosition(rightHandAnchor, OVRPlugin.Node.HandRight);
        rows.Append(',').Append(recenterWatcher != null ? recenterWatcher.RecenterCount.ToString(c) : "")
            .Append('\n');
        rowsRecorded++;
    }

    private void AppendPosition(Transform anchor, OVRPlugin.Node node)
    {
        Vector3 p = referenceSpace != null ? referenceSpace.InverseTransformPoint(anchor.position) : anchor.position;
        var c = CultureInfo.InvariantCulture;
        rows.Append(',').Append(p.x.ToString("F5", c))
            .Append(',').Append(p.y.ToString("F5", c))
            .Append(',').Append(p.z.ToString("F5", c))
            .Append(',').Append(OVRPlugin.GetNodePositionTracked(node) ? '1' : '0');
    }

    private void FinishRecording(bool partial)
    {
        recording = false;
        try
        {
            string folder = Path.Combine(Application.persistentDataPath, "calibration");
            Directory.CreateDirectory(folder);
            string name = $"calib_{slotLabel}_{startUtc.ToLocalTime():yyyyMMdd_HHmmss}{(partial ? "_partial" : "")}.csv";
            lastFilePath = Path.Combine(folder, name);
            var file = new StringBuilder();
            file.Append("# slot=").Append(slotLabel)
                .Append(" device=").Append(SystemInfo.deviceModel)
                .Append(" started_utc=").Append(startUtc.ToString("o", CultureInfo.InvariantCulture))
                .Append(" reference_space=").Append(referenceSpace != null ? referenceSpace.name : "world")
                .Append('\n');
            file.Append("quest_time,unix_ms,frame," +
                        "head_x,head_y,head_z,head_tracked," +
                        "lhand_x,lhand_y,lhand_z,lhand_tracked," +
                        "rhand_x,rhand_y,rhand_z,rhand_tracked,recenter_count\n");
            file.Append(rows);
            File.WriteAllText(lastFilePath, file.ToString());
            Debug.Log($"[CalibrationPoseRecorder] Saved {rowsRecorded} rows to {lastFilePath}", this);
        }
        catch (Exception ex)
        {
            Debug.LogError("[CalibrationPoseRecorder] Could not save CSV: " + ex.Message, this);
        }
    }

    // The headset going to sleep or the app quitting mid-recording still leaves a file.
    private void OnApplicationPause(bool paused)
    {
        if (paused && recording) FinishRecording(true);
    }

    private void OnDisable()
    {
        if (recording) FinishRecording(true);
    }
}
