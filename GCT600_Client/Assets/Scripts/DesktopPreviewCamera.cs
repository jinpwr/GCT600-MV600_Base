using Oculus.Avatar2;
using UnityEngine;
using UnityEngine.XR;

// Editor/desktop preview only: when no headset is active, the rig's eye camera sits at
// floor height, so render the scene from this camera instead. With a headset, stays off.
[RequireComponent(typeof(Camera))]
public sealed class DesktopPreviewCamera : MonoBehaviour
{
    [Tooltip("Drawn above the rig's eye camera in the Game view.")]
    public float depth = 10f;

    private Camera previewCamera;
    private bool registeredWithLod;

    private void Awake()
    {
        previewCamera = GetComponent<Camera>();
        previewCamera.stereoTargetEye = StereoTargetEyeMask.None;
        previewCamera.depth = depth;
    }

    private void Update()
    {
        // XR and the avatar LOD manager can start a few frames after scene load, so check every frame.
        bool preview = !XRSettings.isDeviceActive;
        if (previewCamera.enabled != preview) previewCamera.enabled = preview;
        // Meta Avatar LOD culls against the floor-level eye camera; let this camera count too.
        if (registeredWithLod != preview && AvatarLODManager.hasInstance)
        {
            if (preview) AvatarLODManager.Instance.AddExtraCamera(previewCamera, true, true);
            else AvatarLODManager.Instance.RemoveExtraCamera(previewCamera);
            registeredWithLod = preview;
        }
    }

    private void OnDestroy()
    {
        if (registeredWithLod && AvatarLODManager.hasInstance) AvatarLODManager.Instance.RemoveExtraCamera(previewCamera);
    }
}
