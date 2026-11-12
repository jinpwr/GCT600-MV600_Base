using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// One-click lighting setup for a scene that contains the DojangRoom prefab.
// Baked lightmaps belong to a scene, not a prefab, so every scene that uses the
// room needs its own lighting settings and bake.
public static class DojangSceneSetup
{
    const string LightingSettingsPath = "Assets/Dojang/DojangLighting.lighting";
    const uint RoomRenderingLayer = 1u << 1; // the room's renderers use rendering layer 1 only

    [MenuItem("Tools/Dojang/Set Up Lighting And Bake")]
    static void SetUpAndBake()
    {
        if (!SetUp()) return;
        Lightmapping.BakeAsync();
        Debug.Log("[Dojang] Lighting bake started. Watch the progress bar at the bottom right of the editor.");
    }

    [MenuItem("Tools/Dojang/Set Up Lighting (No Bake)")]
    static bool SetUp()
    {
        if (Object.FindFirstObjectByType<LightProbeGroup>() == null || GameObject.Find("DojangRoom") == null)
        {
            EditorUtility.DisplayDialog("Dojang", "Drag Assets/Dojang/DojangRoom.prefab into the scene first.", "OK");
            return false;
        }

        var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingSettingsPath);
        if (settings == null)
        {
            Debug.LogError("[Dojang] Missing " + LightingSettingsPath);
            return false;
        }
        Lightmapping.lightingSettings = settings;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.12f, 0.11f, 0.10f);

        // Directional lights stay for avatars but must not light the room or be shadowed by its ceiling.
        int changed = 0;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (light.type != LightType.Directional) continue;
            Undo.RecordObject(light, "Dojang directional light");
            light.renderingLayerMask = ~(int)RoomRenderingLayer;
            var data = light.GetComponent<UniversalAdditionalLightData>() ?? light.gameObject.AddComponent<UniversalAdditionalLightData>();
            Undo.RecordObject(data, "Dojang directional light");
            data.renderingLayers = ~RoomRenderingLayer;
            data.customShadowLayers = true;
            data.shadowRenderingLayers = ~RoomRenderingLayer;
            changed++;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[Dojang] Lighting settings assigned; {changed} directional light(s) set to skip the room.");
        return true;
    }
}
