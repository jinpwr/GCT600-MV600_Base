using System;
using System.Collections;
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
    [Tooltip("Start once the headset is worn and tracking (immediately in the Editor without a headset).")]
    public bool startAutomatically = true;
    [Tooltip("Seconds between putting the headset on and the automatic start, to get into the pose.")]
    public float autoStartDelay = 3f;
    [Tooltip("Controller button that starts a new recording. None disables it.")]
    public OVRInput.Button restartButton = OVRInput.Button.One;
    [Tooltip("Written into the file name and header; TaekwonQuestSlot sets it.")]
    public string slotLabel = "A";
    [Tooltip("Optional; adds the recenter count to every row.")]
    public RecenterWatcher recenterWatcher;
    [Tooltip("Vibrate both controllers: one pulse when recording starts, two when it is saved.")]
    public bool hapticFeedback = true;

    [Header("Runtime status")]
    [SerializeField] private bool recording;
    [SerializeField] private int rowsRecorded;
    [SerializeField] private string lastFilePath;

    public string LastFilePath => lastFilePath;

    private readonly StringBuilder rows = new StringBuilder();
    private double startTime;
    private DateTime startUtc;
    private bool autoStarted;
    private float wornSince = -1;

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
            // The headset tracks while it sits on a desk, so also wait for the proximity sensor.
            bool worn = !XRSettings.isDeviceActive ||
                        (OVRPlugin.userPresent && OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head));
            if (!worn) wornSince = -1;
            else if (wornSince < 0) wornSince = Time.unscaledTime;
            bool headReady = worn && (!XRSettings.isDeviceActive || Time.unscaledTime - wornSince >= autoStartDelay);
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
        if (now - startTime >= durationSeconds)
        {
            FinishRecording(false);
            Buzz(2);
        }
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
        Buzz(1);
    }

    // The wearer cannot see the Console, so signal start and end through the controllers.
    private void Buzz(int pulses)
    {
        if (hapticFeedback && isActiveAndEnabled) StartCoroutine(BuzzRoutine(pulses));
    }

    private static IEnumerator BuzzRoutine(int pulses)
    {
        for (int i = 0; i < pulses; i++)
        {
            OVRInput.SetControllerVibration(1f, 0.8f, OVRInput.Controller.Touch);
            yield return new WaitForSecondsRealtime(0.25f);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.Touch);
            yield return new WaitForSecondsRealtime(0.15f);
        }
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
