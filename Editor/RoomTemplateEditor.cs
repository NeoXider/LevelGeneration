#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Infrastructure;
using Neoxider.LevelGeneration.Generation.Infrastructure.Data;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using UnityEditor;
using UnityEngine;

namespace Neoxider.LevelGeneration.Editor
{
    [CustomEditor(typeof(RoomTemplate))]
    internal sealed class RoomTemplateEditor : UnityEditor.Editor
    {
        internal const float DoorFlowSingleArrowSize = 5.1f;
        internal const float DoorFlowDualArrowSize = 4.5f;
        internal const float DoorFlowDualArrowYOffset = 0.2f;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawAuthoringTools();
            DrawRoomKindHelp();
            DrawDefaultInspectorExceptSpawn();
            DrawSpawnDefaults();
            RoomDoorSnapTool.DrawInspector((RoomTemplate)target);
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawAuthoringTools()
        {
            RoomTemplate room = (RoomTemplate)target;
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Room settings", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Open the room authoring toolkit for bounds, door references, validation, room pool registration, guides, and grid metrics.",
                    MessageType.None);

                if (room != null)
                {
                    EditorGUILayout.LabelField("Room Id",
                        string.IsNullOrWhiteSpace(room.RoomId) ? "<empty>" : room.RoomId);
                    DrawRoomPoolStatus(room);
                }

                using (new EditorGUI.DisabledScope(room == null))
                {
                    if (GUILayout.Button("Open Prefab Room Settings", GUILayout.Height(28f)))
                    {
                        RoomAuthoringToolkitWindow.OpenForRoom(room);
                    }
                }
            }
        }

        private static void DrawRoomPoolStatus(RoomTemplate room)
        {
            GameObject prefab = ResolveRoomPrefabAsset(room);
            if (prefab == null)
            {
                EditorGUILayout.HelpBox(
                    "Prefab asset not found. Save this room as a prefab before adding it to a room pool.",
                    MessageType.Warning);
                return;
            }

            RoomTemplate prefabRoom = prefab.GetComponent<RoomTemplate>();
            string roomId = prefabRoom != null ? prefabRoom.RoomId : room.RoomId;
            EditorGUILayout.LabelField("Prefab", AssetDatabase.GetAssetPath(prefab));

            string poolPath = LevelGenerationProjectPaths.DefaultRoomPoolPath;
            if (string.IsNullOrEmpty(poolPath))
            {
                EditorGUILayout.HelpBox(
                    "Room pool is not configured (LevelGenerationProjectPaths.DefaultRoomPoolPath).",
                    MessageType.Info);
                return;
            }

            RoomPoolSO pool = AssetDatabase.LoadAssetAtPath<RoomPoolSO>(poolPath);
            if (pool == null)
            {
                EditorGUILayout.HelpBox($"Room pool not found: {poolPath}", MessageType.Warning);
                return;
            }

            bool isAdded = ContainsRoom(pool, roomId);
            if (isAdded)
            {
                EditorGUILayout.HelpBox($"Added to room pool '{pool.name}' as RoomId '{roomId}'.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("X", "Remove this room prefab from the room pool"),
                            GUILayout.Width(28f), GUILayout.Height(22f)))
                    {
                        RemoveRoomFromPool(pool, roomId);
                    }
                }

                return;
            }

            EditorGUILayout.HelpBox(
                $"Not added to room pool '{pool.name}' as RoomId '{roomId}'. Open settings and add the prefab to the pool.",
                MessageType.Warning);
        }

        private static GameObject ResolveRoomPrefabAsset(RoomTemplate room)
        {
            if (room == null)
            {
                return null;
            }

            GameObject roomObject = room.gameObject;
            if (EditorUtility.IsPersistent(roomObject))
            {
                return roomObject;
            }

            // Room open in Prefab Edit Mode: the stage root isn't persistent and isn't an instance, so the
            // checks below miss it and the panel falsely warns "save as prefab first". Resolve via the stage.
            UnityEditor.SceneManagement.PrefabStage stage =
                UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(roomObject);
            if (stage != null && !string.IsNullOrWhiteSpace(stage.assetPath))
            {
                GameObject staged = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
                if (staged != null)
                {
                    return staged;
                }
            }

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(roomObject);
            if (!string.IsNullOrWhiteSpace(prefabPath))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab != null)
                {
                    return prefab;
                }
            }

            return PrefabUtility.GetCorrespondingObjectFromSource(roomObject);
        }

        private static bool ContainsRoom(RoomPoolSO pool, string roomId)
        {
            if (pool == null || string.IsNullOrWhiteSpace(roomId))
            {
                return false;
            }

            SerializedObject serializedPool = new(pool);
            SerializedProperty rooms = serializedPool.FindProperty("rooms");
            if (rooms == null || !rooms.isArray)
            {
                return false;
            }

            for (int i = 0; i < rooms.arraySize; i++)
            {
                SerializedProperty entry = rooms.GetArrayElementAtIndex(i);
                SerializedProperty roomPrefab = entry.FindPropertyRelative("roomPrefab");
                GameObject prefab = roomPrefab?.objectReferenceValue as GameObject;
                RoomTemplate template = prefab != null ? prefab.GetComponent<RoomTemplate>() : null;
                if (template != null && string.Equals(template.RoomId, roomId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveRoomFromPool(RoomPoolSO pool, string roomId)
        {
            if (pool == null || string.IsNullOrWhiteSpace(roomId))
            {
                return;
            }

            SerializedObject serializedPool = new(pool);
            SerializedProperty rooms = serializedPool.FindProperty("rooms");
            if (rooms == null || !rooms.isArray)
            {
                return;
            }

            for (int i = rooms.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty entry = rooms.GetArrayElementAtIndex(i);
                SerializedProperty roomPrefab = entry.FindPropertyRelative("roomPrefab");
                GameObject prefab = roomPrefab?.objectReferenceValue as GameObject;
                RoomTemplate template = prefab != null ? prefab.GetComponent<RoomTemplate>() : null;
                if (template == null || !string.Equals(template.RoomId, roomId, StringComparison.Ordinal))
                {
                    continue;
                }

                Undo.RecordObject(pool, $"Remove {roomId} from room pool");
                rooms.DeleteArrayElementAtIndex(i);
                serializedPool.ApplyModifiedProperties();
                EditorUtility.SetDirty(pool);
                AssetDatabase.SaveAssets();
                return;
            }
        }

        private void DrawRoomKindHelp()
        {
            SerializedProperty roomKind = serializedObject.FindProperty("roomKind");
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Room kind", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(roomKind, new GUIContent("Kind"));
                EditorGUILayout.HelpBox(
                    "Five generator kinds exist: Start, Exit, Passage, Interactive, DeadEnd. Passage can be tunnel or branch (the generator derives that from graph degree and connectable doors). DeadEnd is a blind one-door terminal used to cap free doors (e.g. MazeBranch wrong answers) and bonus rooms. Disabled doors are ignored.",
                    MessageType.Info);

                if ((RoomKind)roomKind.enumValueIndex == RoomKind.Interactive)
                {
                    EditorGUILayout.HelpBox(
                        "Interactive means the prefab has room logic: quiz branch, lever/crystal panel, password terminal, or future mechanics. Concrete question kind comes from JSON.",
                        MessageType.None);
                }
            }
        }

        private void DrawDefaultInspectorExceptSpawn()
        {
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.PropertyField(iterator, true);
                    }

                    continue;
                }

                if (IsSpawnProperty(iterator.name) || iterator.name == "roomKind")
                {
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        private void DrawSpawnDefaults()
        {
            SerializedProperty hasRule = serializedObject.FindProperty("hasDefaultSpawnRule");
            SerializedProperty mode = serializedObject.FindProperty("defaultSpawnMode");
            SerializedProperty overrideChance = serializedObject.FindProperty("defaultOverrideChance");
            SerializedProperty chance = serializedObject.FindProperty("defaultChance");
            SerializedProperty min = serializedObject.FindProperty("defaultMinCount");
            SerializedProperty max = serializedObject.FindProperty("defaultMaxCount");

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Default mission spawn rule", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Used when this prefab is added to a consumer mission room pool and the mission entry does not override spawn. Leave disabled for normal equal-chance selection.",
                    MessageType.None);
                EditorGUILayout.PropertyField(hasRule, new GUIContent("Use default spawn rule"));
                if (!hasRule.boolValue)
                {
                    return;
                }

                EditorGUILayout.PropertyField(mode, new GUIContent("Mode"));
                if ((RoomTypeSpawnMode)mode.enumValueIndex == RoomTypeSpawnMode.Chance)
                {
                    EditorGUILayout.PropertyField(overrideChance, new GUIContent("Override Chance"));
                    using (new EditorGUI.DisabledScope(!overrideChance.boolValue))
                    {
                        EditorGUILayout.Slider(chance, 0f, 1f, new GUIContent("Chance"));
                    }
                }
                else
                {
                    EditorGUILayout.PropertyField(min, new GUIContent("Min Count"));
                    EditorGUILayout.PropertyField(max, new GUIContent("Max Count"));
                }
            }
        }

        private static bool IsSpawnProperty(string propertyName)
        {
            return propertyName == "hasDefaultSpawnRule"
                   || propertyName == "defaultSpawnMode"
                   || propertyName == "defaultOverrideChance"
                   || propertyName == "defaultChance"
                   || propertyName == "defaultMinCount"
                   || propertyName == "defaultMaxCount";
        }
    }

    [InitializeOnLoad]
    internal static class RoomDoorSnapTool
    {
        private const string EnabledSessionKey = "Neoxider.LevelGeneration.RoomDoorSnapTool.Enabled";
        private const string MenuPath = "Neoxider/LevelGeneration/Room Door Snap Tool";
        private const float SnapRadiusMeters = 1f;
        private const float HandleBaseSize = 0.34f;
        private const float DoorLineHalfHeight = 0.18f;
        private const float LabelYOffset = 0.45f;

        private static readonly Color DragDoorColor = new(0.15f, 1f, 0.35f, 0.95f);
        private static readonly Color TargetDoorColor = new(0.2f, 0.75f, 1f, 0.9f);
        private static readonly Color SnappedDoorColor = new(0.05f, 0.45f, 1f, 1f);
        private static readonly Color DisabledDoorColor = new(0.55f, 0.55f, 0.55f, 0.38f);

        private static bool _enabled;
        private static EntityId _dragDoorEntityId;
        private static EntityId _snappedSourceDoorEntityId;
        private static EntityId _snappedTargetDoorEntityId;

        static RoomDoorSnapTool()
        {
            _enabled = SessionState.GetBool(EnabledSessionKey, false);
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                SessionState.SetBool(EnabledSessionKey, value);
                SceneView.RepaintAll();
            }
        }

        [MenuItem(MenuPath)]
        private static void ToggleFromMenu() => Enabled = !Enabled;

        [MenuItem(MenuPath, true)]
        private static bool ToggleFromMenuValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        public static void DrawInspector(RoomTemplate room)
        {
            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Door snap gizmo", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Enable this tool, then drag a green door handle on the selected room. The whole room moves with that door. If the dragged resolved door center gets within 1 meter of a compatible door on another room, the room snaps and rotates so the door frames match.",
                    MessageType.None);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool nextEnabled = GUILayout.Toggle(Enabled, "Enable Door Snap Gizmo", "Button");
                    if (nextEnabled != Enabled)
                    {
                        Enabled = nextEnabled;
                    }

                    using (new EditorGUI.DisabledScope(_dragDoorEntityId == EntityId.None))
                    {
                        if (GUILayout.Button("Clear Drag State"))
                        {
                            _dragDoorEntityId = EntityId.None;
                            _snappedSourceDoorEntityId = EntityId.None;
                            _snappedTargetDoorEntityId = EntityId.None;
                            SceneView.RepaintAll();
                        }
                    }
                }

                if (room != null)
                {
                    EditorGUILayout.LabelField("Selected room", room.RoomId);
                }
            }
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!Enabled)
            {
                return;
            }

            RoomTemplate selectedRoom = ResolveSelectedRoom();
            DrawSceneHelp(selectedRoom);
            DrawTargetDoorHints(selectedRoom);

            if (selectedRoom != null)
            {
                DrawDraggableDoorHandles(selectedRoom);
            }
        }

        private static void DrawSceneHelp(RoomTemplate selectedRoom)
        {
            Handles.BeginGUI();
            using (new GUILayout.AreaScope(new Rect(12f, 12f, 430f, 94f), GUIContent.none, EditorStyles.helpBox))
            {
                GUILayout.Label("Room Door Snap Tool", EditorStyles.boldLabel);
                GUILayout.Label(selectedRoom == null
                    ? "Select a RoomTemplate in the scene."
                    : "Drag a green door handle. The selected room follows the door on the XZ plane.");
                GUILayout.Label(
                    $"Snap radius: {SnapRadiusMeters:0.#}m. Only connectable doors with matching DoorSize snap.");
            }

            Handles.EndGUI();
        }

        private static void DrawDraggableDoorHandles(RoomTemplate room)
        {
            IReadOnlyList<DoorSocket> doors = room.Doors;
            if (doors == null)
            {
                return;
            }

            for (int i = 0; i < doors.Count; i++)
            {
                DoorSocket door = doors[i];
                if (door == null || !TryGetDoorPose(door, out DoorPose pose))
                {
                    continue;
                }

                bool connectable = IsConnectable(door);
                bool snapped = door.GetEntityId() == _snappedSourceDoorEntityId;
                Color doorColor = snapped ? SnappedDoorColor : connectable ? DragDoorColor : DisabledDoorColor;
                DrawDoorFrame(pose, doorColor, door.FlowDirection);
                DrawDoorLabel(pose,
                    snapped ? $"Snapped {door.SocketId}" :
                    connectable ? $"Drag {door.SocketId}" : $"{door.SocketId} disabled",
                    doorColor);

                if (!connectable)
                {
                    continue;
                }

                using (new Handles.DrawingScope(doorColor))
                {
                    float handleSize = HandleUtility.GetHandleSize(pose.WorldCenter) * HandleBaseSize;
                    EditorGUI.BeginChangeCheck();
                    Vector3 draggedCenter = Handles.Slider2D(
                        pose.WorldCenter,
                        Vector3.up,
                        Vector3.right,
                        Vector3.forward,
                        handleSize,
                        Handles.SphereHandleCap,
                        Vector2.zero);

                    if (EditorGUI.EndChangeCheck())
                    {
                        _dragDoorEntityId = door.GetEntityId();
                        DragRoomByDoor(room, door, pose, draggedCenter);
                    }
                }
            }
        }

        private static void DrawTargetDoorHints(RoomTemplate selectedRoom)
        {
            List<RoomTemplate> rooms = FindSceneRooms();
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomTemplate room = rooms[i];
                if (room == null || room == selectedRoom)
                {
                    continue;
                }

                IReadOnlyList<DoorSocket> doors = room.Doors;
                if (doors == null)
                {
                    continue;
                }

                for (int d = 0; d < doors.Count; d++)
                {
                    DoorSocket door = doors[d];
                    if (door == null || !TryGetDoorPose(door, out DoorPose pose))
                    {
                        continue;
                    }

                    bool snappedTarget = door.GetEntityId() == _snappedTargetDoorEntityId;
                    Color color = snappedTarget ? SnappedDoorColor :
                        IsConnectable(door) ? TargetDoorColor : DisabledDoorColor;
                    DrawDoorFrame(pose, color, door.FlowDirection);
                    DrawDoorLabel(pose, snappedTarget ? $"Snap target {door.SocketId}" : door.SocketId, color);
                }
            }
        }

        private static void DragRoomByDoor(RoomTemplate room, DoorSocket door, DoorPose originalPose,
            Vector3 draggedCenter)
        {
            draggedCenter.y = originalPose.WorldCenter.y;
            Vector3 delta = draggedCenter - originalPose.WorldCenter;
            delta.y = 0f;
            if (delta.sqrMagnitude <= 0.000001f)
            {
                return;
            }

            Undo.RecordObject(room.transform, "Drag room by door");
            room.transform.position += delta;

            if (TryGetDoorPose(door, out DoorPose movedPose)
                && TryFindNearestSnapTarget(room, door, movedPose, out DoorSocket targetDoor))
            {
                SnapRoomToTargetDoor(room, door, targetDoor);
                _snappedSourceDoorEntityId = door.GetEntityId();
                _snappedTargetDoorEntityId = targetDoor.GetEntityId();
            }
            else
            {
                _snappedSourceDoorEntityId = EntityId.None;
                _snappedTargetDoorEntityId = EntityId.None;
            }

            EditorUtility.SetDirty(room);
            SceneView.RepaintAll();
        }

        private static bool TryFindNearestSnapTarget(
            RoomTemplate sourceRoom,
            DoorSocket sourceDoor,
            DoorPose sourcePose,
            out DoorSocket targetDoor)
        {
            targetDoor = null;
            float bestDistance = SnapRadiusMeters;
            List<RoomTemplate> rooms = FindSceneRooms();
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomTemplate room = rooms[i];
                if (room == null || room == sourceRoom)
                {
                    continue;
                }

                IReadOnlyList<DoorSocket> doors = room.Doors;
                if (doors == null)
                {
                    continue;
                }

                for (int d = 0; d < doors.Count; d++)
                {
                    DoorSocket candidate = doors[d];
                    if (candidate == null || !IsConnectable(candidate))
                    {
                        continue;
                    }

                    if (candidate.Size != sourceDoor.Size)
                    {
                        continue;
                    }

                    if (!TryGetDoorPose(candidate, out DoorPose candidatePose))
                    {
                        continue;
                    }

                    float distance = HorizontalDistance(sourcePose.WorldCenter, candidatePose.WorldCenter);
                    if (distance > bestDistance)
                    {
                        continue;
                    }

                    bestDistance = distance;
                    targetDoor = candidate;
                }
            }

            return targetDoor != null;
        }

        private static void SnapRoomToTargetDoor(RoomTemplate sourceRoom, DoorSocket sourceDoor, DoorSocket targetDoor)
        {
            if (sourceRoom == null || sourceDoor == null || targetDoor == null)
            {
                return;
            }

            if (!TryGetDoorPose(sourceDoor, out DoorPose sourcePose)
                || !TryGetDoorPose(targetDoor, out DoorPose targetPose))
            {
                return;
            }

            float desiredSourceWorldYaw = NormalizeYaw(targetPose.WorldYawDegrees + 180f);
            float desiredRoomWorldYaw = NormalizeYaw(desiredSourceWorldYaw - sourcePose.LocalYawDegrees);
            sourceRoom.transform.rotation = Quaternion.Euler(0f, desiredRoomWorldYaw, 0f);

            if (!TryGetDoorPose(sourceDoor, out DoorPose rotatedPose))
            {
                return;
            }

            sourceRoom.transform.position += targetPose.WorldCenter - rotatedPose.WorldCenter;

            if (TryGetDoorPose(sourceDoor, out DoorPose finalSourcePose)
                && TryGetDoorPose(targetDoor, out DoorPose finalTargetPose))
            {
                float centerDelta = HorizontalDistance(finalSourcePose.WorldCenter, finalTargetPose.WorldCenter);
                float yawDelta = OppositeYawDelta(finalSourcePose.WorldYawDegrees, finalTargetPose.WorldYawDegrees);
                float frameDelta = Mathf.Abs(finalSourcePose.FrameWidthMeters - finalTargetPose.FrameWidthMeters);
                if (centerDelta > 0.05f || yawDelta > 0.1f || frameDelta > 0.35f)
                {
                    Debug.LogWarning(
                        $"[RoomDoorSnapTool] Snap result is outside tolerance: center={centerDelta:0.###}m, yaw={yawDelta:0.#}deg, frame={frameDelta:0.###}m. Source={sourceRoom.RoomId}/{sourceDoor.SocketId}, Target={targetDoor.GetComponentInParent<RoomTemplate>(true)?.RoomId}/{targetDoor.SocketId}.",
                        sourceRoom);
                }
            }

            if (Mathf.Abs(sourcePose.FrameWidthMeters - targetPose.FrameWidthMeters) > 0.35f)
            {
                Debug.LogWarning(
                    $"[RoomDoorSnapTool] Snapped doors with different resolved frame widths: {sourceRoom.RoomId}/{sourceDoor.SocketId}={sourcePose.FrameWidthMeters:0.##}m, {targetDoor.GetComponentInParent<RoomTemplate>(true)?.RoomId}/{targetDoor.SocketId}={targetPose.FrameWidthMeters:0.##}m.",
                    sourceRoom);
            }
        }

        private static bool TryGetDoorPose(DoorSocket door, out DoorPose pose)
        {
            pose = default;
            if (door == null)
            {
                return false;
            }

            RoomTemplate room = door.GetComponentInParent<RoomTemplate>(true);
            if (room == null)
            {
                return false;
            }

            Vector3 local = room.transform.InverseTransformPoint(door.ResolveAuthoringWorldCenter());
            float localYaw = door.LocalYawDegrees;
            float frameWidth = DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(door.Size);

            IDoorSocketDescriptor descriptor = FindDoorDescriptor(room, door);
            if (descriptor != null)
            {
                local = new Vector3(
                    descriptor.ConnectionLocalXMeters(),
                    descriptor.ConnectionLocalYMeters(),
                    descriptor.ConnectionLocalZMeters());
                frameWidth = descriptor.FrameWidthMeters();
            }

            Vector3 localOutward = Quaternion.Euler(0f, localYaw, 0f) * Vector3.right;
            Vector3 worldOutward = room.transform.TransformDirection(localOutward);
            worldOutward.y = 0f;
            if (worldOutward.sqrMagnitude <= 0.0001f)
            {
                return false;
            }

            worldOutward.Normalize();
            Vector3 worldCenter = room.transform.TransformPoint(local);
            pose = new DoorPose(worldCenter, worldOutward, localYaw, VectorYawDegrees(worldOutward), frameWidth);
            return true;
        }

        private static IDoorSocketDescriptor FindDoorDescriptor(RoomTemplate room, DoorSocket door)
        {
            if (room == null || door == null || room.DoorsView == null)
            {
                return null;
            }

            IReadOnlyList<IDoorSocketDescriptor> doors = room.DoorsView;
            for (int i = 0; i < doors.Count; i++)
            {
                if (doors[i] != null && string.Equals(doors[i].SocketId, door.SocketId, StringComparison.Ordinal))
                {
                    return doors[i];
                }
            }

            return null;
        }

        private static RoomTemplate ResolveSelectedRoom()
        {
            GameObject active = Selection.activeGameObject;
            if (active == null)
            {
                return null;
            }

            return active.TryGetComponent<RoomTemplate>(out RoomTemplate room)
                ? room
                : active.GetComponentInParent<RoomTemplate>(true);
        }

        private static List<RoomTemplate> FindSceneRooms()
        {
            RoomTemplate[] found = Resources.FindObjectsOfTypeAll<RoomTemplate>();
            List<RoomTemplate> result = new(found.Length);
            for (int i = 0; i < found.Length; i++)
            {
                RoomTemplate room = found[i];
                if (room == null || EditorUtility.IsPersistent(room))
                {
                    continue;
                }

                if (!room.gameObject.scene.IsValid() || !room.gameObject.scene.isLoaded)
                {
                    continue;
                }

                result.Add(room);
            }

            return result;
        }

        private static bool IsConnectable(DoorSocket door)
        {
            return door != null
                   && door.enabled
                   && IsEffectivelyActive(door.transform)
                   && door.FlowDirection != DoorSocketFlowDirection.None
                   && door.InitialState != DoorState.Disabled;
        }

        private static bool IsEffectivelyActive(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                if (!current.gameObject.activeSelf)
                {
                    return false;
                }

                current = current.parent;
            }

            return true;
        }

        private static void DrawDoorFrame(DoorPose pose, Color color, DoorSocketFlowDirection flow)
        {
            using (new Handles.DrawingScope(color))
            {
                Vector3 center = pose.WorldCenter;
                Vector3 outward = pose.WorldOutward;
                Vector3 side = Vector3.Cross(Vector3.up, outward).normalized;
                float halfWidth = Mathf.Max(0.3f, pose.FrameWidthMeters * 0.5f);

                Handles.DrawAAPolyLine(4f, center - side * halfWidth, center + side * halfWidth);
                Handles.DrawLine(center - Vector3.up * DoorLineHalfHeight, center + Vector3.up * DoorLineHalfHeight);
                DrawDoorFlowArrowHandles(center, outward, flow);
            }
        }

        private static void DrawDoorFlowArrowHandles(Vector3 center, Vector3 outward, DoorSocketFlowDirection flow)
        {
            if (flow == DoorSocketFlowDirection.None)
            {
                Handles.color = DoorFlowColor(flow);
                Handles.DrawAAPolyLine(5f, center + Vector3.left * 0.5f, center + Vector3.right * 0.5f);
                Handles.DrawAAPolyLine(5f, center + Vector3.forward * 0.5f, center + Vector3.back * 0.5f);
                return;
            }

            if (flow == DoorSocketFlowDirection.Both)
            {
                Handles.color = DoorFlowColor(DoorSocketFlowDirection.ExitOnly);
                Handles.ArrowHandleCap(0, center + Vector3.up * RoomTemplateEditor.DoorFlowDualArrowYOffset,
                    Quaternion.LookRotation(outward, Vector3.up),
                    RoomTemplateEditor.DoorFlowDualArrowSize, EventType.Repaint);
                Handles.color = DoorFlowColor(DoorSocketFlowDirection.EntryOnly);
                Handles.ArrowHandleCap(0, center - Vector3.up * RoomTemplateEditor.DoorFlowDualArrowYOffset,
                    Quaternion.LookRotation(-outward, Vector3.up),
                    RoomTemplateEditor.DoorFlowDualArrowSize, EventType.Repaint);
                return;
            }

            Vector3 arrowDirection = flow == DoorSocketFlowDirection.EntryOnly ? -outward : outward;
            Handles.color = DoorFlowColor(flow);
            Handles.ArrowHandleCap(0, center, Quaternion.LookRotation(arrowDirection, Vector3.up),
                RoomTemplateEditor.DoorFlowSingleArrowSize,
                EventType.Repaint);
        }

        private static Color DoorFlowColor(DoorSocketFlowDirection flow)
        {
            return flow switch
            {
                DoorSocketFlowDirection.EntryOnly => new Color(0.35f, 0.55f, 1f, 1f),
                DoorSocketFlowDirection.ExitOnly => new Color(1f, 0.12f, 0.08f, 1f),
                DoorSocketFlowDirection.Both => new Color(0.2f, 1f, 0.45f, 1f),
                _ => new Color(0.55f, 0.55f, 0.55f, 1f)
            };
        }

        private static void DrawDoorLabel(DoorPose pose, string text, Color color)
        {
            GUIStyle style = new(EditorStyles.miniBoldLabel) { normal = { textColor = color }, alignment = TextAnchor.MiddleCenter };
            Handles.Label(pose.WorldCenter + Vector3.up * LabelYOffset, text, style);
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static float OppositeYawDelta(float aYaw, float bYaw)
        {
            float diff = Mathf.Abs(NormalizeYaw(aYaw - bYaw));
            if (diff > 180f)
            {
                diff = 360f - diff;
            }

            return Mathf.Abs(diff - 180f);
        }

        private static float VectorYawDegrees(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                return 0f;
            }

            direction.Normalize();
            return NormalizeYaw(Mathf.Atan2(-direction.z, direction.x) * Mathf.Rad2Deg);
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private readonly struct DoorPose
        {
            public readonly Vector3 WorldCenter;
            public readonly Vector3 WorldOutward;
            public readonly float LocalYawDegrees;
            public readonly float WorldYawDegrees;
            public readonly float FrameWidthMeters;

            public DoorPose(
                Vector3 worldCenter,
                Vector3 worldOutward,
                float localYawDegrees,
                float worldYawDegrees,
                float frameWidthMeters)
            {
                WorldCenter = worldCenter;
                WorldOutward = worldOutward;
                LocalYawDegrees = localYawDegrees;
                WorldYawDegrees = worldYawDegrees;
                FrameWidthMeters = frameWidthMeters;
            }
        }
    }
}
#endif
