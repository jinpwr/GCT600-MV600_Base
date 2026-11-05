using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// Quest apps cannot block the system recenter (long-press Meta button; OVRManager.AllowRecenter
// only affects PC VR). This counts recenter events so a recording can be flagged as broken,
// and can optionally switch to the Stage origin, which a recenter does not move.
[DisallowMultipleComponent]
public sealed class RecenterWatcher : MonoBehaviour
{
    [Tooltip("Set OVRManager's tracking origin to Stage (fixed to the boundary) on start. " +
             "Changes the coordinate origin for every sender, so agree on it before calibration.")]
    public bool useStageOrigin = false;
    [Tooltip("Also count Unity XR trackingOriginUpdated events. Off by default: OpenXR also raises it when the " +
             "session first sets up its origin (e.g. putting the headset on), which looks like a recenter. " +
             "The Quest's own recenter counter (OVRDisplay.RecenteredPose) is always used.")]
    public bool countUnityXREvents = false;

    [Header("Warning")]
    [Tooltip("Optional object (e.g. a red text panel in front of the camera) shown after a recenter.")]
    public GameObject warningObject;
    [Tooltip("Seconds the warning stays visible. 0 keeps it on until ClearWarning() is called.")]
    public float warningSeconds = 5f;

    [Header("Runtime status")]
    [SerializeField] private int recenterCount;
    [SerializeField] private string trackingOrigin;
    [SerializeField] private float lastRecenterTime = -1;

    public int RecenterCount => recenterCount;

    private readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
    private OVRDisplay subscribedDisplay;
    private float hideWarningAt = -1;
    // Origin changes while XR starts up (and our own Stage switch) also raise the events.
    private float ignoreUntil;

    private void OnEnable()
    {
        if (warningObject != null) warningObject.SetActive(false);
        SubsystemManager.GetSubsystems(inputSubsystems);
        foreach (var subsystem in inputSubsystems) subsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
    }

    private void Start()
    {
        if (useStageOrigin && OVRManager.instance != null)
            OVRManager.instance.trackingOriginType = OVRManager.TrackingOrigin.Stage;
        ignoreUntil = Time.unscaledTime + 2f;
    }

    private void Update()
    {
        // OVRManager creates its display in Awake; subscribe once it exists.
        if (subscribedDisplay == null && OVRManager.display != null)
        {
            subscribedDisplay = OVRManager.display;
            subscribedDisplay.RecenteredPose += OnRecentered;
        }
        if (OVRManager.instance != null) trackingOrigin = OVRManager.instance.trackingOriginType.ToString();
        if (hideWarningAt >= 0 && Time.unscaledTime >= hideWarningAt) ClearWarning();
    }

    // Unity XR raises this alongside OVR's event for the same recenter; both only add to the count.
    private void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
    {
        if (countUnityXREvents) OnRecentered();
    }

    private void OnRecentered()
    {
        if (Time.unscaledTime < ignoreUntil) return;
        // Ignore the duplicate report of one recenter from the second event source.
        if (lastRecenterTime >= 0 && Time.unscaledTime - lastRecenterTime < 0.5f) return;
        recenterCount++;
        lastRecenterTime = Time.unscaledTime;
        Debug.LogWarning($"[RecenterWatcher] Recenter detected (#{recenterCount}, origin {trackingOrigin}). " +
                         "Data recorded before and after this point use different coordinates.", this);
        if (warningObject != null) warningObject.SetActive(true);
        hideWarningAt = warningSeconds > 0 ? Time.unscaledTime + warningSeconds : -1;
    }

    public void ClearWarning()
    {
        if (warningObject != null) warningObject.SetActive(false);
        hideWarningAt = -1;
    }

    private void OnDisable()
    {
        foreach (var subsystem in inputSubsystems) subsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
        inputSubsystems.Clear();
        if (subscribedDisplay != null) subscribedDisplay.RecenteredPose -= OnRecentered;
        subscribedDisplay = null;
    }
}
