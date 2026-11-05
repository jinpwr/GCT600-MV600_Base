using Oculus.Avatar2;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// Builds the webcam-to-head occlusion test rig and links it to RemoteAvatar_A / RemoteAvatar_B.
public static class OcclusionVizMenu
{
    // Plane1 (the floor in front of Wall1) sits at y = 0 in AvatarWallDisplay.
    const float FloorY = 0f;

    const string RootName = "OcclusionViz";
    const string WallName = "Wall1";

    const float WebcamOffsetFromWall = 0.1f;
    const float HeadHeight = 1.6f;
    const float FakeHeadSize = 0.15f;
    const float ReceivedHeadSize = 0.12f;
    const float HeadADistance = 1.5f;
    const float HeadBDistance = 3.0f;
    const float HeadBSideOffset = 0.1f;
    const float BodyDiameter = 0.4f;
    const float BodyHeight = 1.7f;

    static readonly Color ReceivedHeadColor = new Color(0.1f, 0.8f, 1f); // cyan; fake heads keep the default grey

    [MenuItem("GCT600/Occlusion Viz/Create")]
    static void Create()
    {
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            if (go.name == RootName)
            {
                Debug.LogWarning($"[OcclusionViz] '{RootName}' already exists in scene '{scene.name}'. Nothing was created.", go);
                Selection.activeGameObject = go;
                return;
            }
        }

        GameObject wall = GameObject.Find(WallName);
        Renderer wallRenderer = wall != null ? wall.GetComponent<Renderer>() : null;
        if (wallRenderer == null)
        {
            Debug.LogError($"[OcclusionViz] '{WallName}' with a Renderer not found in scene '{scene.name}'. Nothing was created.");
            return;
        }

        Undo.SetCurrentGroupName("Create Occlusion Viz");
        int undoGroup = Undo.GetCurrentGroup();

        // Unity's Quad shows its front face toward local -Z, so the room side of the wall is -forward.
        Vector3 inward = Vector3.ProjectOnPlane(-wall.transform.forward, Vector3.up).normalized;
        Bounds b = wallRenderer.bounds;
        Vector3 wallBottomCenter = new Vector3(b.center.x, b.min.y, b.center.z);

        GameObject root = new GameObject(RootName);

        // Webcam
        Transform webcamOrigin = new GameObject("WebcamOrigin").transform;
        webcamOrigin.SetParent(root.transform, false);
        webcamOrigin.position = wallBottomCenter + inward * WebcamOffsetFromWall;
        webcamOrigin.rotation = Quaternion.LookRotation(inward, Vector3.up);

        Transform marker = CreatePrimitive(PrimitiveType.Cube, "WebcamMarker", webcamOrigin);
        marker.localPosition = Vector3.zero;
        marker.localScale = new Vector3(0.15f, 0.10f, 0.10f);

        Transform forwardIndicator = CreatePrimitive(PrimitiveType.Cube, "ForwardIndicator", webcamOrigin);
        forwardIndicator.localPosition = new Vector3(0f, 0f, 0.05f + 0.15f); // starts at the marker's front face
        forwardIndicator.localScale = new Vector3(0.02f, 0.02f, 0.3f);

        // Head positions
        Vector3 headAPos = webcamOrigin.position + inward * HeadADistance;
        headAPos.y = FloorY + HeadHeight;
        Vector3 headBPos = webcamOrigin.position + inward * HeadBDistance + webcamOrigin.right * HeadBSideOffset;
        headBPos.y = FloorY + HeadHeight;

        // Unity's cylinder is 2 m tall and 1 m wide at scale 1.
        Transform bodyA = CreatePrimitive(PrimitiveType.Cylinder, "FakeBody_A", root.transform);
        bodyA.position = new Vector3(headAPos.x, FloorY + BodyHeight * 0.5f, headAPos.z);
        bodyA.localScale = new Vector3(BodyDiameter, BodyHeight * 0.5f, BodyDiameter);

        HeadTargetSelector selectorA = CreatePerson("A", "quest-A-head", root.transform, webcamOrigin, headAPos, Color.green);
        HeadTargetSelector selectorB = CreatePerson("B", "quest-B-head", root.transform, webcamOrigin, headBPos, Color.yellow);

        // Avatars are optional: link what exists and report the rest.
        LinkAvatar("RemoteAvatar_A", selectorA, webcamOrigin);
        LinkAvatar("RemoteAvatar_B", selectorB, webcamOrigin);

        Undo.RegisterCreatedObjectUndo(root, "Create Occlusion Viz");
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = root;
        Debug.Log($"[OcclusionViz] Created '{RootName}' in scene '{scene.name}'.", root);
    }

    static HeadTargetSelector CreatePerson(string id, string channel, Transform parent, Transform webcamOrigin,
        Vector3 headPos, Color lineColor)
    {
        GameObject person = new GameObject("Person_" + id);
        person.transform.SetParent(parent, false);

        Transform receivedHead = CreatePrimitive(PrimitiveType.Sphere, "ReceivedHead_" + id, person.transform);
        receivedHead.position = headPos;
        receivedHead.localScale = Vector3.one * ReceivedHeadSize;
        Renderer receivedRenderer = receivedHead.GetComponent<Renderer>();
        receivedRenderer.sharedMaterial = new Material(receivedRenderer.sharedMaterial)
        {
            name = "ReceivedHead (OcclusionViz)",
            color = ReceivedHeadColor
        };

        Transform fakeHead = CreatePrimitive(PrimitiveType.Cube, "FakeHead_" + id, person.transform);
        fakeHead.position = headPos;
        fakeHead.localScale = Vector3.one * FakeHeadSize;

        GameObject lineObject = new GameObject("Line_" + id);
        lineObject.transform.SetParent(person.transform, false);
        CameraToHeadLine line = lineObject.AddComponent<CameraToHeadLine>();
        line.origin = webcamOrigin;
        line.target = fakeHead;
        line.color = lineColor;
        // Has no material until Play, which would render magenta in edit mode. CameraToHeadLine enables it at runtime.
        LineRenderer lineRenderer = lineObject.GetComponent<LineRenderer>();
        lineRenderer.positionCount = 2;
        lineRenderer.useWorldSpace = true;
        lineRenderer.enabled = false;

        HeadPoseReceiver receiver = person.AddComponent<HeadPoseReceiver>();
        receiver.channel = channel;
        receiver.displayOrigin = webcamOrigin;
        receiver.target = receivedHead;

        HeadTargetSelector selector = person.AddComponent<HeadTargetSelector>();
        selector.line = line;
        selector.headReceiver = receiver;
        selector.fallback = fakeHead;
        return selector;
    }

    static void LinkAvatar(string avatarName, HeadTargetSelector selector, Transform webcamOrigin)
    {
        GameObject go = GameObject.Find(avatarName);
        if (go == null)
        {
            Debug.LogWarning($"[OcclusionViz] '{avatarName}' not found. {selector.name} will use the head channel or fake head only.", selector);
            return;
        }

        OvrAvatarEntity avatar = go.GetComponent<OvrAvatarEntity>();
        if (avatar == null)
            Debug.LogWarning($"[OcclusionViz] '{avatarName}' has no OvrAvatarEntity. {selector.name}.avatar left empty.", go);
        else
            selector.avatar = avatar;

        MetaAvatarReceiver receiver = go.GetComponent<MetaAvatarReceiver>();
        if (receiver == null)
        {
            Debug.LogWarning($"[OcclusionViz] '{avatarName}' has no MetaAvatarReceiver. Display Origin was not set.", go);
            return;
        }
        Undo.RecordObject(receiver, "Set Display Origin");
        receiver.displayOrigin = webcamOrigin;
        PrefabUtility.RecordPrefabInstancePropertyModifications(receiver);
        Debug.Log($"[OcclusionViz] Linked '{avatarName}' to {selector.name}; Display Origin = WebcamOrigin.", go);
    }

    static Transform CreatePrimitive(PrimitiveType type, string name, Transform parent)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        Collider collider = go.GetComponent<Collider>();
        if (collider != null) Object.DestroyImmediate(collider);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}
