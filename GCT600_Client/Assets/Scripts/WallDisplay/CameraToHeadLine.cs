using UnityEngine;

// Draws a line from origin (webcam) to target (head). Reads positions only; never moves the target.
// Runs after MixamoRetargeter (0) and HybridAvatarRoot (100) in LateUpdate so the end point is this frame's head.
[DefaultExecutionOrder(200)]
[RequireComponent(typeof(LineRenderer))]
public class CameraToHeadLine : MonoBehaviour
{
    public Transform origin;
    public Transform target;
    public float width = 0.02f;
    public Color color = Color.green;
    public Material lineMaterial; // Optional. A runtime material is created if empty.

    private LineRenderer line;
    private Material runtimeMaterial;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.positionCount = 2;
        line.useWorldSpace = true;

        if (lineMaterial == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            runtimeMaterial = new Material(shader);
            line.sharedMaterial = runtimeMaterial;
        }
        else
        {
            line.sharedMaterial = lineMaterial;
        }
    }

    void LateUpdate()
    {
        if (origin == null || target == null ||
            !origin.gameObject.activeInHierarchy || !target.gameObject.activeInHierarchy)
        {
            line.enabled = false;
            return;
        }

        line.enabled = true;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;

        // URP Unlit ignores vertex colors, so tint the runtime material directly.
        if (runtimeMaterial != null && runtimeMaterial.HasProperty("_BaseColor"))
            runtimeMaterial.SetColor("_BaseColor", color);

        line.SetPosition(0, origin.position);
        line.SetPosition(1, target.position);
    }

    void OnDestroy()
    {
        if (runtimeMaterial != null) Destroy(runtimeMaterial);
    }
}
