#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using RedoSchool.LevelGeneration.Generation.Infrastructure;
using RedoSchool.LevelGeneration.Generation.Infrastructure.Data;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;
using UnityEditor;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Editor
{
    public sealed class RoomAuthoringToolkitWindow : EditorWindow
    {
        private const string GridMetricsFileName = "LevelGenerationGridMetrics.cs";

        private const string BoundsObjectName = "bounds";
        private const string RoofObjectName = "Roof";
        private const float DefaultBoundsHeight = 3f;
        private const float FootprintSnapToleranceMeters = 0.5f;

        private UnityEngine.Object _targetRoomObject;
        private RoomPoolSO _targetRoomPool;
        /// <summary>
        /// Папка и пул по умолчанию берутся при первом <see cref="OnGUI"/>, а не в инициализаторе и не в
        /// <c>OnEnable</c>: окно, восстановленное после перекомпиляции, включается раньше, чем проект назначит
        /// <see cref="LevelGenerationProjectPaths"/> из своего <c>[InitializeOnLoad]</c>.
        /// </summary>
        private string _prefabRoot;
        [NonSerialized] private bool _defaultPoolResolved;
        private bool _drawRoomGuides = true;
        private bool _drawFootprintGuide = true;
        private bool _drawBoundsGuide = true;
        private bool _drawDoorLabels = true;
        private bool _drawTriggerGuides;
        private float _gridDoorSnapMeters;
        private Vector2 _scroll;
        private readonly List<ReportLine> _report = new();

        [MenuItem("Neoxider/LevelGeneration/Room Authoring Toolkit")]
        public static void Open() => GetWindow<RoomAuthoringToolkitWindow>("Room Authoring");

        public static void OpenForRoom(RoomTemplate room)
        {
            RoomAuthoringToolkitWindow window = GetWindow<RoomAuthoringToolkitWindow>("Room Authoring");
            if (room != null)
            {
                window._targetRoomObject = ResolveToolkitTargetObject(room);
                window.Repaint();
                SceneView.RepaintAll();
            }

            window.Focus();
        }

        private static UnityEngine.Object ResolveToolkitTargetObject(RoomTemplate room)
        {
            GameObject prefab = ResolveRoomPrefabAssetForToolkit(room);
            return prefab != null ? prefab : room;
        }

        private static GameObject ResolveRoomPrefabAssetForToolkit(RoomTemplate room)
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

        private void OnEnable()
        {
            _drawFootprintGuide = RoomPrefabGridValidator.DrawFootprintGizmos;
            _gridDoorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;
            SceneView.duringSceneGui += OnSceneGui;
        }

        private void OnDisable() => SceneView.duringSceneGui -= OnSceneGui;

        private void OnGUI()
        {
            ResolveProjectDefaults();
            RoomTemplate selectedRoom = FindTargetRoom() ?? FindSelectedRoom();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawSection("Room Target", () =>
            {
                DrawTargetControls();
                DrawActiveRoomSummary(selectedRoom);
                DrawRoomPoolControls(selectedRoom);
            });

            DrawSection("Guides", () => DrawGuideControls());
            DrawSection("Repair And Validation", () => DrawRepairControls(selectedRoom));
            DrawSection("Roof", () => DrawRoofControls(selectedRoom));

            DrawSection("Grid Metrics", () =>
            {
                DrawGridMetricsControls();

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Grid config", GridMetricsFileName);
                    if (GUILayout.Button("Open", GUILayout.Width(70f)))
                    {
                        UnityEngine.Object asset =
                            AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(FindGridMetricsAssetPath() ?? string.Empty);
                        if (asset != null)
                        {
                            AssetDatabase.OpenAsset(asset);
                        }
                    }
                }
            });

            DrawSection("Diagnostics", () =>
            {
                DrawDoorDistanceInfo(selectedRoom);
                EditorGUILayout.Space(6f);
                _prefabRoot = EditorGUILayout.TextField("Prefab root", _prefabRoot);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Validate All Room Prefabs"))
                    {
                        ValidateAllPrefabs();
                    }

                    if (GUILayout.Button("Clear Report", GUILayout.Width(110f)))
                    {
                        _report.Clear();
                    }
                }

                DrawReport();
            });

            EditorGUILayout.EndScrollView();
        }

        private static void DrawSection(string title, Action draw)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            draw();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(6f);
        }

        private static void DrawActiveRoomSummary(RoomTemplate selectedRoom)
        {
            if (selectedRoom == null)
            {
                EditorGUILayout.HelpBox("Assign a room prefab/object or select a room in Hierarchy/Project.",
                    MessageType.Info);
                return;
            }

            IRoomTemplateDescriptor descriptor = (IRoomTemplateDescriptor)selectedRoom;
            RoomFootprint footprint = descriptor.Footprint;
            EditorGUILayout.HelpBox(
                $"Active: {selectedRoom.name}\nFootprint: {footprint.WidthCells} x {footprint.DepthCells} cells ({footprint.WidthCells * LevelGenerationGridMetrics.CellSizeMeters:0.##}m x {footprint.DepthCells * LevelGenerationGridMetrics.CellSizeMeters:0.##}m)\nDoors: {selectedRoom.Doors?.Count ?? 0}",
                MessageType.None);
        }

        private void DrawRepairControls(RoomTemplate selectedRoom)
        {
            using (new EditorGUI.DisabledScope(selectedRoom == null))
            {
                if (GUILayout.Button("Repair Bounds + Validate (Keep Doors)", GUILayout.Height(30f)))
                {
                    RepairRoomContract(selectedRoom);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Refresh References"))
                    {
                        RefreshRoomReferences(selectedRoom);
                        AddInfo($"{selectedRoom.name}: references refreshed.");
                    }

                    if (GUILayout.Button("Ensure Bounds"))
                    {
                        EnsureBounds(selectedRoom);
                        RefreshRoomReferences(selectedRoom);
                        AddInfo($"{selectedRoom.name}: bounds ensured.");
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Use Current Door Positions"))
                    {
                        SnapRoomDoors(selectedRoom);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reset Anchors To Footprint Sides"))
                    {
                        RebuildDoorAnchorsFromFootprint(selectedRoom);
                    }

                    if (GUILayout.Button("Validate Selected"))
                    {
                        ValidateRoom(selectedRoom, true);
                    }
                }
            }
        }

        private void DrawReport()
        {
            if (_report.Count == 0)
            {
                EditorGUILayout.HelpBox("No report messages yet.", MessageType.None);
                return;
            }

            for (int i = 0; i < _report.Count; i++)
            {
                EditorGUILayout.HelpBox(_report[i].Message, _report[i].Type);
            }
        }

        private void DrawGridMetricsControls()
        {
            EditorGUILayout.LabelField("Global grid", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _gridDoorSnapMeters = Mathf.Max(0.25f, EditorGUILayout.FloatField("Door snap meters", _gridDoorSnapMeters));
            EditorGUILayout.LabelField("Footprint cell meters", $"{_gridDoorSnapMeters:0.##}m (same as door snap)");
            if (EditorGUI.EndChangeCheck())
            {
                Repaint();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "Current compiled",
                    $"door snap {LevelGenerationGridMetrics.DoorSnapMeters:0.##}m, footprint cell {LevelGenerationGridMetrics.CellSizeMeters:0.##}m");

                if (GUILayout.Button("Apply", GUILayout.Width(70f)))
                {
                    ApplyGridMetrics(_gridDoorSnapMeters);
                }

                if (GUILayout.Button("Reset", GUILayout.Width(70f)))
                {
                    _gridDoorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;
                }
            }
        }

        private void ApplyGridMetrics(float doorSnapMeters)
        {
            string assetPath = FindGridMetricsAssetPath();
            if (assetPath == null)
            {
                AddWarning($"Grid config not found: {GridMetricsFileName}");
                return;
            }

            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (package != null
                && package.source != UnityEditor.PackageManager.PackageSource.Embedded
                && package.source != UnityEditor.PackageManager.PackageSource.Local)
            {
                AddWarning($"Grid config is read-only in the installed package {package.name}; change it in the package repository.");
                return;
            }

            string fullPath = package == null
                ? Path.GetFullPath(assetPath)
                : Path.Combine(package.resolvedPath, assetPath.Substring(package.assetPath.Length).TrimStart('/'));
            float normalizedSnap = Mathf.Max(0.25f, doorSnapMeters);
            string text = File.ReadAllText(fullPath);
            text = Regex.Replace(
                text,
                @"public const float DoorSnapMeters = [^;]+;",
                $"public const float DoorSnapMeters = {FormatFloatLiteral(normalizedSnap)};");
            text = Regex.Replace(
                text,
                @"public const float CellSizeMeters = [^;]+;",
                "public const float CellSizeMeters = DoorSnapMeters;");
            File.WriteAllText(fullPath, text);
            AssetDatabase.ImportAsset(assetPath);
            AssetDatabase.Refresh();
            AddInfo(
                $"Global grid updated to door snap {normalizedSnap:0.##}m, footprint cell {normalizedSnap:0.##}m. Unity will recompile scripts.");
        }

        /// <summary>Файл констант сетки — по скрипту, а не по пути: пакет лежит и в Assets, и в Packages.</summary>
        private static string FindGridMetricsAssetPath()
        {
            foreach (string guid in AssetDatabase.FindAssets("LevelGenerationGridMetrics t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("/" + GridMetricsFileName, StringComparison.Ordinal))
                {
                    return path;
                }
            }

            return null;
        }

        private void ResolveProjectDefaults()
        {
            if (string.IsNullOrEmpty(_prefabRoot))
            {
                _prefabRoot = LevelGenerationProjectPaths.RoomPrefabFolder;
            }

            if (!_defaultPoolResolved)
            {
                _defaultPoolResolved = true;
                if (_targetRoomPool == null)
                {
                    _targetRoomPool = LoadDefaultRoomPool();
                }
            }
        }

        /// <summary>Пул из <see cref="LevelGenerationProjectPaths.DefaultRoomPoolPath"/>; пустой путь — пула нет.</summary>
        private static RoomPoolSO LoadDefaultRoomPool()
        {
            string path = LevelGenerationProjectPaths.DefaultRoomPoolPath;
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<RoomPoolSO>(path);
        }

        private static string FormatFloatLiteral(float value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "f";

        private void DrawTargetControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _targetRoomObject = EditorGUILayout.ObjectField(
                    "Room prefab/object",
                    _targetRoomObject,
                    typeof(UnityEngine.Object),
                    true);

                if (GUILayout.Button("Use Selection", GUILayout.Width(110f)))
                {
                    _targetRoomObject = Selection.activeObject;
                    Repaint();
                    SceneView.RepaintAll();
                }
            }
        }

        private void DrawRoomPoolControls(RoomTemplate selectedRoom)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Reusable room pool", EditorStyles.boldLabel);
                _targetRoomPool = (RoomPoolSO)EditorGUILayout.ObjectField(
                    "Target pool",
                    _targetRoomPool,
                    typeof(RoomPoolSO),
                    false);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Use Default Pool"))
                    {
                        _targetRoomPool = LoadDefaultRoomPool();
                        if (_targetRoomPool == null)
                        {
                            string path = LevelGenerationProjectPaths.DefaultRoomPoolPath;
                            AddWarning(string.IsNullOrEmpty(path)
                                ? "Default room pool is not configured (LevelGenerationProjectPaths.DefaultRoomPoolPath)."
                                : $"Default room pool not found: {path}");
                        }
                    }

                    using (new EditorGUI.DisabledScope(selectedRoom == null || _targetRoomPool == null))
                    {
                        if (GUILayout.Button("Add selected room to pool"))
                        {
                            AddSelectedRoomToPool(selectedRoom, _targetRoomPool);
                        }
                    }
                }

                if (selectedRoom == null)
                {
                    EditorGUILayout.HelpBox("Select a room prefab first.", MessageType.None);
                    return;
                }

                if (_targetRoomPool == null)
                {
                    EditorGUILayout.HelpBox("Assign a RoomPoolSO asset.", MessageType.Warning);
                    return;
                }

                GameObject prefab = ResolvePrefabAsset(selectedRoom);
                RoomTemplate prefabRoom = prefab != null ? prefab.GetComponent<RoomTemplate>() : null;
                string roomId = prefabRoom != null ? prefabRoom.RoomId : selectedRoom.RoomId;
                bool exists = ContainsRoom(_targetRoomPool, roomId);
                EditorGUILayout.HelpBox(
                    exists
                        ? $"Pool already contains RoomId '{roomId}'."
                        : $"Ready to add RoomId '{roomId}'.",
                    exists ? MessageType.Info : MessageType.None);
                if (exists)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent("X", "Remove this room prefab from the selected room pool"),
                                GUILayout.Width(28f), GUILayout.Height(22f)))
                        {
                            RemoveSelectedRoomFromPool(prefabRoom, _targetRoomPool);
                        }
                    }
                }
            }
        }

        private void DrawGuideControls()
        {
            EditorGUI.BeginChangeCheck();
            _drawRoomGuides = EditorGUILayout.Toggle("Draw room guides", _drawRoomGuides);
            using (new EditorGUI.DisabledScope(!_drawRoomGuides))
            {
                _drawFootprintGuide = EditorGUILayout.Toggle("Draw footprint grid (debug)", _drawFootprintGuide);
                _drawBoundsGuide = EditorGUILayout.Toggle("Draw bounds", _drawBoundsGuide);
                _drawDoorLabels = EditorGUILayout.Toggle("Draw door labels", _drawDoorLabels);
                _drawTriggerGuides = EditorGUILayout.Toggle("Draw trigger boxes", _drawTriggerGuides);
            }

            if (EditorGUI.EndChangeCheck())
            {
                RoomPrefabGridValidator.DrawFootprintGizmos = _drawFootprintGuide;
                SceneView.RepaintAll();
            }
        }

        private static void DrawDoorDistanceInfo(RoomTemplate room)
        {
            if (room == null)
            {
                return;
            }

            DoorSocket[] doors = room.GetComponentsInChildren<DoorSocket>(true);
            if (doors.Length < 2)
            {
                EditorGUILayout.LabelField("Door distances", "Need at least 2 DoorSocket components");
                return;
            }

            EditorGUILayout.LabelField("Door distances", EditorStyles.boldLabel);
            for (int i = 0; i < doors.Length; i++)
            {
                DoorSocket from = doors[i];
                if (from == null)
                {
                    continue;
                }

                for (int j = i + 1; j < doors.Length; j++)
                {
                    DoorSocket to = doors[j];
                    if (to == null)
                    {
                        continue;
                    }

                    Vector3 fromLocal = room.transform.InverseTransformPoint(from.transform.position);
                    Vector3 toLocal = room.transform.InverseTransformPoint(to.transform.position);
                    float dx = toLocal.x - fromLocal.x;
                    float dz = toLocal.z - fromLocal.z;
                    float axisMeters = IsEastWestPair(from, to) ? Mathf.Abs(dx) :
                        IsNorthSouthPair(from, to) ? Mathf.Abs(dz) : Mathf.Sqrt(dx * dx + dz * dz);
                    float sideOffsetMeters = IsEastWestPair(from, to) ? dz : IsNorthSouthPair(from, to) ? dx : 0f;
                    int snapX = Mathf.RoundToInt(dx / LevelGenerationGridMetrics.DoorSnapMeters);
                    int snapZ = Mathf.RoundToInt(dz / LevelGenerationGridMetrics.DoorSnapMeters);
                    EditorGUILayout.LabelField(
                        $"{from.SocketId} -> {to.SocketId}",
                        $"{axisMeters:0.##}m axis, side {sideOffsetMeters:0.##}m, delta ({dx:0.##}m, {dz:0.##}m), snap ({snapX}, {snapZ})");
                }
            }
        }

        private static bool IsEastWestPair(DoorSocket a, DoorSocket b)
        {
            return (a.Direction == DoorDirection.East || a.Direction == DoorDirection.West)
                   && (b.Direction == DoorDirection.East || b.Direction == DoorDirection.West);
        }

        private static bool IsNorthSouthPair(DoorSocket a, DoorSocket b)
        {
            return (a.Direction == DoorDirection.North || a.Direction == DoorDirection.South)
                   && (b.Direction == DoorDirection.North || b.Direction == DoorDirection.South);
        }

        private RoomTemplate FindTargetRoom()
        {
            if (_targetRoomObject == null)
            {
                return null;
            }

            if (_targetRoomObject is GameObject go)
            {
                return go.GetComponent<RoomTemplate>() ?? go.GetComponentInParent<RoomTemplate>(true);
            }

            if (_targetRoomObject is Component component)
            {
                return component.GetComponent<RoomTemplate>() ?? component.GetComponentInParent<RoomTemplate>(true);
            }

            return null;
        }

        private static RoomTemplate FindSelectedRoom()
        {
            if (Selection.activeGameObject != null)
            {
                GameObject selected = Selection.activeGameObject;
                RoomTemplate room = selected.GetComponent<RoomTemplate>() ??
                                    selected.GetComponentInParent<RoomTemplate>(true);
                if (room != null)
                {
                    return room;
                }
            }

            if (Selection.activeObject is GameObject prefabAsset)
            {
                return prefabAsset.GetComponent<RoomTemplate>();
            }

            return null;
        }

        private void AddSelectedRoomToPool(RoomTemplate selectedRoom, RoomPoolSO pool)
        {
            if (selectedRoom == null)
            {
                AddWarning("Select a room prefab before adding it to a pool.");
                return;
            }

            if (pool == null)
            {
                AddWarning("Assign a RoomPoolSO before adding a room.");
                return;
            }

            GameObject prefab = ResolvePrefabAsset(selectedRoom);
            if (prefab == null)
            {
                AddWarning($"{selectedRoom.name}: cannot resolve source prefab asset. Save it as a prefab first.");
                return;
            }

            RoomTemplate room = prefab.GetComponent<RoomTemplate>();
            if (room == null)
            {
                AddWarning($"{prefab.name}: prefab root has no RoomTemplate component.");
                return;
            }

            string roomId = room.RoomId;
            if (ContainsRoom(pool, roomId))
            {
                AddInfo($"{pool.name}: RoomId '{roomId}' is already in the pool.");
                EditorGUIUtility.PingObject(pool);
                return;
            }

            Undo.RecordObject(pool, $"Add {roomId} to room pool");
            SerializedObject serializedPool = new(pool);
            SerializedProperty rooms = serializedPool.FindProperty("rooms");
            if (rooms == null || !rooms.isArray)
            {
                AddWarning($"{pool.name}: serialized 'rooms' list was not found.");
                return;
            }

            int index = rooms.arraySize;
            rooms.InsertArrayElementAtIndex(index);
            SerializedProperty entry = rooms.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative("roomPrefab").objectReferenceValue = prefab;
            entry.FindPropertyRelative("overrideSpawnRule").boolValue = false;
            entry.FindPropertyRelative("mode").enumValueIndex = (int)RoomTypeSpawnMode.Chance;
            entry.FindPropertyRelative("overrideChance").boolValue = false;
            entry.FindPropertyRelative("chance").floatValue = 0f;
            entry.FindPropertyRelative("minCount").intValue = 0;
            entry.FindPropertyRelative("maxCount").intValue = 0;
            entry.FindPropertyRelative("overrideWeight").boolValue = false;
            entry.FindPropertyRelative("weight").intValue = 100;

            serializedPool.ApplyModifiedProperties();
            EditorUtility.SetDirty(pool);
            AssetDatabase.SaveAssets();
            AddInfo($"{pool.name}: added '{roomId}' to reusable room pool.");
            EditorGUIUtility.PingObject(pool);
        }

        private void RemoveSelectedRoomFromPool(RoomTemplate selectedRoom, RoomPoolSO pool)
        {
            if (selectedRoom == null)
            {
                AddWarning("Select a room prefab before removing it from a pool.");
                return;
            }

            if (pool == null)
            {
                AddWarning("Assign a RoomPoolSO before removing a room.");
                return;
            }

            string roomId = selectedRoom.RoomId;
            SerializedObject serializedPool = new(pool);
            SerializedProperty rooms = serializedPool.FindProperty("rooms");
            if (rooms == null || !rooms.isArray)
            {
                AddWarning($"{pool.name}: serialized 'rooms' list was not found.");
                return;
            }

            bool removed = false;
            Undo.RecordObject(pool, $"Remove {roomId} from room pool");
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

                rooms.DeleteArrayElementAtIndex(i);
                removed = true;
            }

            if (!removed)
            {
                AddWarning($"{pool.name}: RoomId '{roomId}' was not found in the pool.");
                return;
            }

            serializedPool.ApplyModifiedProperties();
            EditorUtility.SetDirty(pool);
            AssetDatabase.SaveAssets();
            AddInfo($"{pool.name}: removed '{roomId}' from reusable room pool.");
            EditorGUIUtility.PingObject(pool);
        }

        private static bool ContainsRoom(RoomPoolSO pool, string roomId)
        {
            if (pool == null || pool.Rooms == null || string.IsNullOrWhiteSpace(roomId))
            {
                return false;
            }

            for (int i = 0; i < pool.Rooms.Count; i++)
            {
                GameObject prefab = pool.Rooms[i].roomPrefab;
                if (prefab == null)
                {
                    continue;
                }

                RoomTemplate room = prefab.GetComponent<RoomTemplate>();
                if (room == null)
                {
                    continue;
                }

                if (string.Equals(room.RoomId, roomId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static GameObject ResolvePrefabAsset(RoomTemplate room)
        {
            if (room == null)
            {
                return null;
            }

            GameObject gameObject = room.gameObject;

            if (PrefabUtility.IsPartOfPrefabAsset(gameObject))
            {
                return gameObject;
            }

            // Room opened in Prefab Edit Mode: the stage root is a preview-scene object that is neither a
            // prefab asset nor an instance, so the instance-based checks below miss it. Resolve the real
            // asset from the stage's asset path so the room can still be added to a pool while being edited.
            UnityEditor.SceneManagement.PrefabStage stage =
                UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject);
            if (stage != null && !string.IsNullOrWhiteSpace(stage.assetPath))
            {
                GameObject staged = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
                if (staged != null)
                {
                    return staged;
                }
            }

            GameObject source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(gameObject);
            if (source != null)
            {
                return source;
            }

            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            return string.IsNullOrWhiteSpace(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private void OnSceneGui(SceneView sceneView)
        {
            if (!_drawRoomGuides)
            {
                return;
            }

            RoomTemplate room = FindTargetRoom() ?? FindSelectedRoom();
            if (room == null)
            {
                return;
            }

            DrawRoomGuides(room, _drawBoundsGuide, _drawDoorLabels, _drawTriggerGuides);
        }

        private static void DrawRoomGuides(
            RoomTemplate room,
            bool drawBounds,
            bool drawDoorLabels,
            bool drawTriggerGuides)
        {
            if (room == null)
            {
                return;
            }

            if (drawBounds)
            {
                DrawBoundsGuide(room);
            }

            if (drawDoorLabels || drawTriggerGuides)
            {
                DrawDoorGuides(room, drawDoorLabels, drawTriggerGuides);
            }

            DrawRoofGuide(room);
        }

        private static void DrawBoundsGuide(RoomTemplate room)
        {
            RoomBounds bounds = room.Bounds != null ? room.Bounds : room.GetComponentInChildren<RoomBounds>(true);
            if (bounds == null || bounds.Box == null)
            {
                return;
            }

            BoxCollider box = bounds.Box;
            Matrix4x4 matrix = Handles.matrix;
            Handles.matrix = box.transform.localToWorldMatrix;
            Handles.color = new Color(0f, 0.9f, 1f, 0.85f);
            Handles.DrawWireCube(box.center, box.size);
            Handles.matrix = matrix;
            Handles.Label(box.transform.TransformPoint(box.center) + Vector3.up * 0.4f, "RoomBounds");
        }

        private static void DrawDoorGuides(RoomTemplate room, bool drawDoorLabels, bool drawTriggerGuides)
        {
            DoorSocket[] doors = room.GetComponentsInChildren<DoorSocket>(true);
            for (int i = 0; i < doors.Length; i++)
            {
                DoorSocket door = doors[i];
                if (door == null)
                {
                    continue;
                }

                if (drawDoorLabels)
                {
                    Handles.color = door.AutoOpen ? new Color(0.25f, 1f, 0.45f, 1f) : new Color(1f, 0.85f, 0.15f, 1f);
                    Handles.Label(
                        door.transform.position + Vector3.up * 0.9f,
                        $"Dir: {door.Direction}\nId: {door.SocketId}\nOff: {door.GridOffsetSteps}, Auto: {(door.AutoOpen ? "on" : "off")}");
                }

                if (drawTriggerGuides)
                {
                    DoorAutoOpenTrigger[] triggers = door.GetComponentsInChildren<DoorAutoOpenTrigger>(true);
                    for (int t = 0; t < triggers.Length; t++)
                    {
                        DrawTriggerGuide(triggers[t]);
                    }
                }
            }
        }

        private static void DrawTriggerGuide(DoorAutoOpenTrigger trigger)
        {
            if (trigger == null)
            {
                return;
            }

            BoxCollider box = trigger.GetComponent<BoxCollider>();
            if (box == null)
            {
                return;
            }

            Matrix4x4 matrix = Handles.matrix;
            Handles.matrix = box.transform.localToWorldMatrix;
            Handles.color = new Color(0.2f, 1f, 0.35f, 0.6f);
            Handles.DrawWireCube(box.center, box.size);
            Handles.matrix = matrix;
            Handles.Label(box.transform.TransformPoint(box.center) + Vector3.up * 0.25f, "TrigerBoxOpen");
        }

        private static void DrawRoofGuide(RoomTemplate room)
        {
            GameObject roof = FindRoof(room);
            if (roof == null)
            {
                return;
            }

            Handles.color = roof.activeSelf ? new Color(0.8f, 0.7f, 1f, 1f) : new Color(0.45f, 0.45f, 0.55f, 1f);
            Handles.Label(roof.transform.position + Vector3.up * 0.8f, roof.activeSelf ? "Roof shown" : "Roof hidden");
        }

        private void DrawRoofControls(RoomTemplate room)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Roof", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(room == null))
            {
                GameObject roof = room != null ? FindRoof(room) : null;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(roof == null))
                    {
                        bool current = roof != null && roof.activeSelf;
                        EditorGUI.BeginChangeCheck();
                        bool next = EditorGUILayout.Toggle("Show roof", current);
                        if (EditorGUI.EndChangeCheck())
                        {
                            SetRoofActive(room, roof, next);
                        }
                    }

                    if (GUILayout.Button("Ensure Roof UnityEngine.Object", GUILayout.Width(150f)))
                    {
                        EnsureRoofObject(room);
                    }
                }

                if (room != null && roof == null)
                {
                    EditorGUILayout.HelpBox(
                        $"No direct child named '{RoofObjectName}'. Press Ensure Roof UnityEngine.Object, then put roof meshes under it.",
                        MessageType.Info);
                }
            }
        }

        private static GameObject FindRoof(RoomTemplate room)
        {
            if (room == null)
            {
                return null;
            }

            for (int i = 0; i < room.transform.childCount; i++)
            {
                Transform child = room.transform.GetChild(i);
                if (child != null && child.name == RoofObjectName)
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private void EnsureRoofObject(RoomTemplate room)
        {
            if (room == null)
            {
                return;
            }

            GameObject roof = FindRoof(room);
            if (roof != null)
            {
                AddInfo($"{room.name}: roof object already exists.");
                return;
            }

            roof = new GameObject(RoofObjectName);
            Undo.RegisterCreatedObjectUndo(roof, "Create room roof object");
            roof.transform.SetParent(room.transform, false);
            roof.transform.localPosition = Vector3.zero;
            roof.transform.localRotation = Quaternion.identity;
            roof.transform.localScale = Vector3.one;
            SaveRoomChanges(room, roof);
            AddInfo($"{room.name}: created empty '{RoofObjectName}' child. Put roof meshes under it.");
        }

        private void SetRoofActive(RoomTemplate room, GameObject roof, bool isActive)
        {
            if (room == null || roof == null)
            {
                return;
            }

            Undo.RecordObject(roof, "Toggle room roof");
            roof.SetActive(isActive);
            SaveRoomChanges(room, roof);
            AddInfo($"{room.name}: roof {(isActive ? "shown" : "hidden")}.");
        }

        private static void SaveRoomChanges(RoomTemplate room, UnityEngine.Object changedObject)
        {
            if (changedObject != null)
            {
                EditorUtility.SetDirty(changedObject);
                PrefabUtility.RecordPrefabInstancePropertyModifications(changedObject);
            }

            if (room != null)
            {
                EditorUtility.SetDirty(room.gameObject);
                PrefabUtility.RecordPrefabInstancePropertyModifications(room.gameObject);
                if (PrefabUtility.IsPartOfPrefabAsset(room.gameObject))
                {
                    PrefabUtility.SavePrefabAsset(room.gameObject);
                }
            }

            AssetDatabase.SaveAssets();
        }

        private static void RefreshRoomReferences(RoomTemplate room)
        {
            if (room == null)
            {
                return;
            }

            Undo.RecordObject(room, "Refresh room references");
            int removedTemplateComponents = RemoveNestedRoomTemplateComponents(room);
            int removedDoorSockets = RemoveDuplicateDoorSockets(room);
            room.RefreshChildReferences();
            EditorUtility.SetDirty(room);
            PrefabUtility.RecordPrefabInstancePropertyModifications(room);
            AssetDatabase.SaveAssets();
            if (removedTemplateComponents > 0 || removedDoorSockets > 0)
            {
                Debug.Log(
                    $"[RoomAuthoringToolkit] {room.name}: removed {removedTemplateComponents} nested RoomTemplate component(s), {removedDoorSockets} duplicate DoorSocket component(s).",
                    room);
            }
        }

        private static RoomBounds EnsureBounds(RoomTemplate room)
        {
            if (room == null)
            {
                return null;
            }

            GameObject directBoundsObject = FindDirectChild(room.transform, BoundsObjectName);
            RoomBounds bounds = directBoundsObject != null
                ? directBoundsObject.GetComponent<RoomBounds>()
                : room.Bounds != null
                    ? room.Bounds
                    : room.GetComponentInChildren<RoomBounds>(true);
            GameObject boundsObject;
            bool hadBoxBeforeEnsure;
            if (bounds != null)
            {
                boundsObject = bounds.gameObject;
                hadBoxBeforeEnsure = boundsObject.GetComponent<BoxCollider>() != null;
            }
            else
            {
                boundsObject = FindDirectChild(room.transform, BoundsObjectName);
                if (boundsObject == null)
                {
                    boundsObject = new GameObject(BoundsObjectName);
                    Undo.RegisterCreatedObjectUndo(boundsObject, "Create room bounds");
                    boundsObject.transform.SetParent(room.transform, false);
                    boundsObject.transform.localPosition = Vector3.zero;
                    boundsObject.transform.localRotation = Quaternion.identity;
                    boundsObject.transform.localScale = Vector3.one;
                }

                hadBoxBeforeEnsure = boundsObject.GetComponent<BoxCollider>() != null;
                bounds = boundsObject.GetComponent<RoomBounds>();
                if (bounds == null)
                {
                    bounds = Undo.AddComponent<RoomBounds>(boundsObject);
                }
            }

            NormalizeBoundsTransform(room, boundsObject);

            BoxCollider box = boundsObject.GetComponent<BoxCollider>();
            if (box == null)
            {
                box = Undo.AddComponent<BoxCollider>(boundsObject);
            }

            Undo.RecordObject(box, "Configure room bounds");
            box.isTrigger = true;
            ConfigureBoundsBox(room, boundsObject, box, !hadBoxBeforeEnsure);
            LinkBoundsCollider(bounds, box);

            EditorUtility.SetDirty(bounds);
            EditorUtility.SetDirty(box);
            PrefabUtility.RecordPrefabInstancePropertyModifications(bounds);
            PrefabUtility.RecordPrefabInstancePropertyModifications(box);
            SaveRoomChanges(room, boundsObject);
            room.RefreshChildReferences();
            return bounds;
        }

        private static void NormalizeBoundsTransform(RoomTemplate room, GameObject boundsObject)
        {
            if (room == null || boundsObject == null)
            {
                return;
            }

            if (boundsObject.transform.parent != room.transform)
            {
                Undo.SetTransformParent(boundsObject.transform, room.transform, "Move room bounds to root");
            }

            Undo.RecordObject(boundsObject.transform, "Normalize room bounds transform");
            boundsObject.name = BoundsObjectName;
            boundsObject.transform.localPosition = Vector3.zero;
            boundsObject.transform.localRotation = Quaternion.identity;
            boundsObject.transform.localScale = Vector3.one;
            EditorUtility.SetDirty(boundsObject.transform);
            PrefabUtility.RecordPrefabInstancePropertyModifications(boundsObject.transform);
        }

        private static GameObject FindDirectChild(Transform parent, string childName)
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child != null && child.name == childName)
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private static void ConfigureBoundsBox(RoomTemplate room, GameObject boundsObject, BoxCollider box,
            bool useDefaultHeight)
        {
            bool hasContentBounds = TryCalculateContentBounds(room, boundsObject, out Bounds contentBounds);
            Vector2 footprintMin = Vector2.zero;
            Vector3 roomSize;

            if (hasContentBounds)
            {
                roomSize = ResolveSnappedContentFootprint(contentBounds, out footprintMin, out int widthCells,
                    out int depthCells);
                ApplyRoomFootprint(
                    room,
                    widthCells,
                    depthCells,
                    CalculateOccupiedCells(room, boundsObject, footprintMin, widthCells, depthCells));
            }
            else
            {
                roomSize = ResolveRoomSizeFromTemplate(room);
                if (roomSize.x <= 0f || roomSize.z <= 0f)
                {
                    roomSize = new Vector3(
                        LevelGenerationGridMetrics.CellSizeMeters,
                        0f,
                        LevelGenerationGridMetrics.CellSizeMeters);
                }
            }

            Vector3 size = box.size;
            size.x = roomSize.x;
            size.z = roomSize.z;
            if (hasContentBounds)
            {
                size.y = Mathf.Max(DefaultBoundsHeight, contentBounds.size.y);
            }
            else if (useDefaultHeight || size.y <= 0.01f)
            {
                size.y = DefaultBoundsHeight;
            }

            Vector3 center = box.center;
            center.x = footprintMin.x + roomSize.x * 0.5f;
            center.z = footprintMin.y + roomSize.z * 0.5f;

            if (hasContentBounds)
            {
                center.y = contentBounds.center.y;
            }
            else if (Mathf.Approximately(center.y, 0f))
            {
                center.y = size.y * 0.5f;
            }

            box.size = size;
            box.center = center;
        }

        private static Vector3 ResolveSnappedContentFootprint(
            Bounds contentBounds,
            out Vector2 footprintMin,
            out int widthCells,
            out int depthCells)
        {
            float snap = Mathf.Max(0.01f, LevelGenerationGridMetrics.CellSizeMeters);
            float minX = ResolveSnappedAxisMin(contentBounds.min.x, contentBounds.max.x, snap, out widthCells);
            float minZ = ResolveSnappedAxisMin(contentBounds.min.z, contentBounds.max.z, snap, out depthCells);
            footprintMin = new Vector2(minX, minZ);

            return new Vector3(widthCells * snap, 0f, depthCells * snap);
        }

        private static float ResolveSnappedAxisMin(float contentMin, float contentMax, float snap, out int cells)
        {
            float rawSize = Mathf.Max(0.001f, contentMax - contentMin);
            int nearestCells = Mathf.Max(1, Mathf.RoundToInt(rawSize / snap));
            float nearestSize = nearestCells * snap;
            if (Mathf.Abs(rawSize - nearestSize) <= FootprintSnapToleranceMeters)
            {
                cells = nearestCells;
                return contentMin;
            }
            else
            {
                cells = Mathf.Max(1, Mathf.CeilToInt(rawSize / snap));
            }

            float snappedSize = cells * snap;
            return (contentMin + contentMax - snappedSize) * 0.5f;
        }

        private static void ApplyRoomFootprint(
            RoomTemplate room,
            int widthCells,
            int depthCells,
            IReadOnlyList<RoomFootprintCell> occupiedCells = null)
        {
            if (room == null)
            {
                return;
            }

            SerializedObject serialized = new(room);
            SerializedProperty widthProperty = serialized.FindProperty("widthCells");
            SerializedProperty depthProperty = serialized.FindProperty("depthCells");
            if (widthProperty == null || depthProperty == null)
            {
                return;
            }

            widthProperty.intValue = Mathf.Max(1, widthCells);
            depthProperty.intValue = Mathf.Max(1, depthCells);

            SerializedProperty cellsProperty = serialized.FindProperty("occupiedCells");
            if (cellsProperty != null)
            {
                IReadOnlyList<RoomFootprintCell> cells = occupiedCells == null || occupiedCells.Count == 0
                    ? BuildRectangleCells(widthCells, depthCells)
                    : occupiedCells;
                cellsProperty.ClearArray();
                for (int i = 0; i < cells.Count; i++)
                {
                    cellsProperty.InsertArrayElementAtIndex(i);
                    SerializedProperty element = cellsProperty.GetArrayElementAtIndex(i);
                    element.FindPropertyRelative("X").intValue = cells[i].X;
                    element.FindPropertyRelative("Z").intValue = cells[i].Z;
                }
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(room);
            PrefabUtility.RecordPrefabInstancePropertyModifications(room);
        }

        private static IReadOnlyList<RoomFootprintCell> CalculateOccupiedCells(
            RoomTemplate room,
            GameObject boundsObject,
            Vector2 footprintMin,
            int widthCells,
            int depthCells)
        {
            List<Bounds> localBounds = new();
            CollectLocalContentBounds(room, boundsObject, localBounds);
            if (localBounds.Count == 0)
            {
                return BuildRectangleCells(widthCells, depthCells);
            }

            List<RoomFootprintCell> result = new(Mathf.Max(1, widthCells * depthCells));
            float snap = Mathf.Max(0.01f, LevelGenerationGridMetrics.CellSizeMeters);
            const float tolerance = 0.05f;
            for (int z = 0; z < depthCells; z++)
            {
                float cellMinZ = footprintMin.y + z * snap;
                float cellMaxZ = cellMinZ + snap;
                for (int x = 0; x < widthCells; x++)
                {
                    float cellMinX = footprintMin.x + x * snap;
                    float cellMaxX = cellMinX + snap;
                    if (IntersectsAny(localBounds, cellMinX, cellMaxX, cellMinZ, cellMaxZ, tolerance))
                    {
                        result.Add(new RoomFootprintCell(x, z));
                    }
                }
            }

            return result.Count > 0 ? result : BuildRectangleCells(widthCells, depthCells);
        }

        private static IReadOnlyList<RoomFootprintCell> BuildRectangleCells(int widthCells, int depthCells)
        {
            List<RoomFootprintCell> result = new(Mathf.Max(1, widthCells * depthCells));
            for (int z = 0; z < Mathf.Max(1, depthCells); z++)
            {
                for (int x = 0; x < Mathf.Max(1, widthCells); x++)
                {
                    result.Add(new RoomFootprintCell(x, z));
                }
            }

            return result;
        }

        private static bool IntersectsAny(
            IReadOnlyList<Bounds> bounds,
            float cellMinX,
            float cellMaxX,
            float cellMinZ,
            float cellMaxZ,
            float tolerance)
        {
            for (int i = 0; i < bounds.Count; i++)
            {
                Bounds b = bounds[i];
                if (b.max.x <= cellMinX + tolerance || b.min.x >= cellMaxX - tolerance)
                {
                    continue;
                }

                if (b.max.z <= cellMinZ + tolerance || b.min.z >= cellMaxZ - tolerance)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static void CollectLocalContentBounds(RoomTemplate room, GameObject boundsObject,
            List<Bounds> localBounds)
        {
            if (room == null || localBounds == null)
            {
                return;
            }

            Renderer[] renderers = room.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                if (ShouldIgnoreForRoomBounds(renderer.transform, boundsObject))
                {
                    continue;
                }

                localBounds.Add(ToLocalBounds(room.transform, renderer.bounds));
            }

            Collider[] colliders = room.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger)
                {
                    continue;
                }

                if (ShouldIgnoreForRoomBounds(collider.transform, boundsObject))
                {
                    continue;
                }

                localBounds.Add(ToLocalBounds(room.transform, collider.bounds));
            }
        }

        private static Bounds ToLocalBounds(Transform roomRoot, Bounds worldBounds)
        {
            bool hasBounds = false;
            Bounds localBounds = default;
            EncapsulateWorldBounds(roomRoot, worldBounds, ref localBounds, ref hasBounds);
            return localBounds;
        }

        private static bool TryCalculateContentBounds(RoomTemplate room, GameObject boundsObject, out Bounds bounds)
        {
            bounds = default;
            if (room == null)
            {
                return false;
            }

            bool hasBounds = false;
            Renderer[] renderers = room.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                if (ShouldIgnoreForRoomBounds(renderer.transform, boundsObject))
                {
                    continue;
                }

                EncapsulateWorldBounds(room.transform, renderer.bounds, ref bounds, ref hasBounds);
            }

            Collider[] colliders = room.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger)
                {
                    continue;
                }

                if (ShouldIgnoreForRoomBounds(collider.transform, boundsObject))
                {
                    continue;
                }

                EncapsulateWorldBounds(room.transform, collider.bounds, ref bounds, ref hasBounds);
            }

            DoorSocket[] doors = room.GetComponentsInChildren<DoorSocket>(true);
            for (int i = 0; i < doors.Length; i++)
            {
                DoorSocket door = doors[i];
                if (door == null)
                {
                    continue;
                }

                Vector3 local = room.transform.InverseTransformPoint(door.transform.position);
                EncapsulateLocalPoint(local, ref bounds, ref hasBounds);
                EncapsulateLocalPoint(new Vector3(door.LocalXMeters, local.y, door.LocalZMeters), ref bounds,
                    ref hasBounds);
            }

            return hasBounds;
        }

        private static bool ShouldIgnoreForRoomBounds(Transform target, GameObject boundsObject)
        {
            if (target == null)
            {
                return true;
            }

            if (boundsObject != null && target.IsChildOf(boundsObject.transform))
            {
                return true;
            }

            if (HasIgnoredBoundsName(target))
            {
                return true;
            }

            if (target.GetComponentInParent<DoorSocket>(true) != null)
            {
                return true;
            }

            if (target.GetComponentInParent<DoorAutoOpenTrigger>(true) != null)
            {
                return true;
            }

            if (target.GetComponentInParent<RoomBounds>(true) != null)
            {
                return true;
            }

            return false;
        }

        private static bool HasIgnoredBoundsName(Transform target)
        {
            while (target != null)
            {
                if (target.name.StartsWith("FX_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                target = target.parent;
            }

            return false;
        }

        private static void EncapsulateWorldBounds(Transform roomRoot, Bounds worldBounds, ref Bounds localBounds,
            ref bool hasBounds)
        {
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            Vector3[] corners = new[] { new Vector3(min.x, min.y, min.z), new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, min.z), new Vector3(min.x, max.y, max.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z) };

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 local = roomRoot.InverseTransformPoint(corners[i]);
                if (!hasBounds)
                {
                    localBounds = new Bounds(local, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    localBounds.Encapsulate(local);
                }
            }
        }

        private static void EncapsulateLocalPoint(Vector3 local, ref Bounds localBounds, ref bool hasBounds)
        {
            if (!hasBounds)
            {
                localBounds = new Bounds(local, Vector3.zero);
                hasBounds = true;
                return;
            }

            localBounds.Encapsulate(local);
        }

        private static Vector3 ResolveRoomSizeFromTemplate(RoomTemplate room)
        {
            if (room == null)
            {
                return Vector3.zero;
            }

            IRoomTemplateDescriptor descriptor = (IRoomTemplateDescriptor)room;
            RoomFootprint footprint = descriptor.Footprint;
            return new Vector3(
                footprint.WidthCells * LevelGenerationGridMetrics.CellSizeMeters,
                0f,
                footprint.DepthCells * LevelGenerationGridMetrics.CellSizeMeters);
        }

        private static void LinkBoundsCollider(RoomBounds bounds, BoxCollider box)
        {
            if (bounds == null || box == null)
            {
                return;
            }

            SerializedObject serialized = new(bounds);
            serialized.FindProperty("boxCollider").objectReferenceValue = box;
            serialized.ApplyModifiedProperties();
        }

        private static int RemoveNestedRoomTemplateComponents(RoomTemplate root)
        {
            int count = 0;
            RoomTemplate[] templates = root.GetComponentsInChildren<RoomTemplate>(true);
            for (int i = templates.Length - 1; i >= 0; i--)
            {
                RoomTemplate template = templates[i];
                if (template == null || template == root)
                {
                    continue;
                }

                Undo.DestroyObjectImmediate(template);
                count++;
            }

            return count;
        }

        private static int RemoveDuplicateDoorSockets(RoomTemplate root)
        {
            int count = 0;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                DoorSocket[] sockets = transforms[i].GetComponents<DoorSocket>();
                for (int s = sockets.Length - 1; s >= 1; s--)
                {
                    Undo.DestroyObjectImmediate(sockets[s]);
                    count++;
                }
            }

            return count;
        }

        private void RepairRoomContract(RoomTemplate room)
        {
            _report.Clear();
            if (room == null)
            {
                return;
            }

            RefreshRoomReferences(room);
            EnsureValidator(room);
            EnsureBounds(room);
            RefreshRoomReferences(room);
            SnapRoomDoors(room, false);
            EnsureBounds(room);
            RefreshRoomReferences(room);
            ValidateRoom(room, false);
            SceneView.RepaintAll();
            AddInfo($"{room.name}: repair pass finished. Door transforms and trigger boxes were kept.");
        }

        private void RebuildDoorAnchorsFromFootprint(RoomTemplate room)
        {
            if (room == null)
            {
                return;
            }

            Vector3 size = ResolveRoomSize(room);
            if (size.x <= 0f || size.z <= 0f)
            {
                AddWarning($"{room.name}: room size is invalid; set RoomTemplate footprint or RoomBounds first.");
                return;
            }

            DoorSocket[] doors = room.GetComponentsInChildren<DoorSocket>(true);
            if (doors.Length == 0)
            {
                AddWarning($"{room.name}: no DoorSocket components found.");
                return;
            }

            DoorDirection[] directions = new DoorDirection[doors.Length];
            bool allEastWest = true;
            bool allNorthSouth = true;
            for (int i = 0; i < doors.Length; i++)
            {
                directions[i] =
                    ResolveAuthoredDirection(doors[i], ResolveNearestDirection(room.transform, doors[i], size));
                allEastWest &= directions[i] == DoorDirection.East || directions[i] == DoorDirection.West;
                allNorthSouth &= directions[i] == DoorDirection.North || directions[i] == DoorDirection.South;
            }

            Vector2[] anchors = new Vector2[doors.Length];
            for (int i = 0; i < doors.Length; i++)
            {
                anchors[i] = ResolveAnchorFromFootprint(room.transform, doors[i], directions[i], size, allEastWest,
                    allNorthSouth);
            }

            Vector2 gridOrigin = anchors[0];
            for (int i = 0; i < doors.Length; i++)
            {
                ApplyDoorData(doors[i], directions[i], anchors[i], gridOrigin,
                    LevelGenerationGridMetrics.DoorSnapMeters, true);
                EditorUtility.SetDirty(doors[i]);
                PrefabUtility.RecordPrefabInstancePropertyModifications(doors[i]);
            }

            AssetDatabase.SaveAssets();
            AddInfo(
                $"{room.name}: rebuilt {doors.Length} door anchor(s) from footprint. Door transforms and trigger boxes were not moved.");
        }

        private static DoorDirection ResolveNearestDirection(Transform roomRoot, DoorSocket door, Vector3 roomSize)
        {
            if (roomRoot == null || door == null)
            {
                return DoorDirection.East;
            }

            Vector3 local = roomRoot.InverseTransformPoint(door.transform.position);
            float west = Mathf.Abs(local.x);
            float east = Mathf.Abs(local.x - roomSize.x);
            float south = Mathf.Abs(local.z);
            float north = Mathf.Abs(local.z - roomSize.z);

            DoorDirection direction = DoorDirection.West;
            float min = west;
            if (east < min)
            {
                min = east;
                direction = DoorDirection.East;
            }

            if (south < min)
            {
                min = south;
                direction = DoorDirection.South;
            }

            if (north < min)
            {
                direction = DoorDirection.North;
            }

            return direction;
        }

        private static Vector2 ResolveAnchorFromFootprint(
            Transform roomRoot,
            DoorSocket door,
            DoorDirection direction,
            Vector3 roomSize,
            bool allEastWest,
            bool allNorthSouth)
        {
            Vector2 currentAnchor = new(door.LocalXMeters, door.LocalZMeters);
            Vector3 local = roomRoot != null && door != null
                ? roomRoot.InverseTransformPoint(door.transform.position)
                : Vector3.zero;
            Vector2 center = new(roomSize.x * 0.5f, roomSize.z * 0.5f);
            float snap = LevelGenerationGridMetrics.DoorSnapMeters;

            float ResolveAlong(float current, float fallback, float max, bool forceCenter)
            {
                if (forceCenter)
                {
                    return max * 0.5f;
                }

                if (current >= -0.001f && current <= max + 0.001f)
                {
                    return Mathf.Clamp(current, 0f, max);
                }

                float snapped = Mathf.Round(fallback / snap) * snap;
                return Mathf.Clamp(snapped, 0f, max);
            }

            return direction switch
            {
                DoorDirection.West => new Vector2(0f, ResolveAlong(currentAnchor.y, local.z, roomSize.z, allEastWest)),
                DoorDirection.East => new Vector2(roomSize.x,
                    ResolveAlong(currentAnchor.y, local.z, roomSize.z, allEastWest)),
                DoorDirection.South => new Vector2(ResolveAlong(currentAnchor.x, local.x, roomSize.x, allNorthSouth),
                    0f),
                DoorDirection.North => new Vector2(ResolveAlong(currentAnchor.x, local.x, roomSize.x, allNorthSouth),
                    roomSize.z),
                _ => center
            };
        }

        private void SnapRoomDoors(RoomTemplate room) => SnapRoomDoors(room, true);

        private void SnapRoomDoors(RoomTemplate room, bool report)
        {
            if (room == null)
            {
                return;
            }

            Vector3 size = ResolveRoomSize(room);
            if (size.x <= 0f || size.z <= 0f)
            {
                AddWarning($"{room.name}: room size is invalid; set RoomTemplate footprint or RoomBounds first.");
                return;
            }

            DoorSocket[] doors = room.GetComponentsInChildren<DoorSocket>(true);
            if (doors.Length == 0)
            {
                AddWarning($"{room.name}: no DoorSocket components found.");
                return;
            }

            Vector2 gridOrigin = ResolveDoorGridOrigin(room.transform, doors);
            Vector2 footprintMin = ResolveDoorAlignedFootprintMin(room, doors, size, gridOrigin);
            Vector2 footprintMax = footprintMin + new Vector2(size.x, size.z);
            for (int i = 0; i < doors.Length; i++)
            {
                SnapDoor(room.transform, doors[i], footprintMin, footprintMax, gridOrigin);
            }

            AssetDatabase.SaveAssets();
            if (report)
            {
                AddInfo(
                    $"{room.name}: updated {doors.Length} door socket data from current manual positions. Grid origin is first door at ({gridOrigin.x:0.##}, {gridOrigin.y:0.##}).");
            }
        }

        private void SnapDoor(
            Transform roomRoot,
            DoorSocket door,
            Vector2 footprintMin,
            Vector2 footprintMax,
            Vector2 gridOrigin)
        {
            if (door == null)
            {
                return;
            }

            Undo.RecordObject(door, "Snap room door data");

            Vector3 local = roomRoot.InverseTransformPoint(door.transform.position);
            float west = Mathf.Abs(local.x - footprintMin.x);
            float east = Mathf.Abs(local.x - footprintMax.x);
            float south = Mathf.Abs(local.z - footprintMin.y);
            float north = Mathf.Abs(local.z - footprintMax.y);

            DoorDirection direction = DoorDirection.West;
            float min = west;
            if (east < min)
            {
                min = east;
                direction = DoorDirection.East;
            }

            if (south < min)
            {
                min = south;
                direction = DoorDirection.South;
            }

            if (north < min)
            {
                direction = DoorDirection.North;
            }

            direction = ResolveAuthoredDirection(door, direction);

            float offsetMeters = direction == DoorDirection.East || direction == DoorDirection.West
                ? local.z - gridOrigin.y
                : local.x - gridOrigin.x;
            ApplyDoorData(
                door,
                direction,
                local,
                offsetMeters,
                gridOrigin,
                LevelGenerationGridMetrics.DoorSnapMeters,
                true);
            EditorUtility.SetDirty(door);
            PrefabUtility.RecordPrefabInstancePropertyModifications(door);
        }

        private static void ApplyDoorData(
            DoorSocket door,
            DoorDirection direction,
            Vector2 anchor,
            Vector2 gridOrigin,
            float doorSnapMeters,
            bool preserveDirectionAndYaw = false)
        {
            float offsetMeters = direction == DoorDirection.East || direction == DoorDirection.West
                ? anchor.y - gridOrigin.y
                : anchor.x - gridOrigin.x;
            float yaw = LocalYaw(direction);
            SerializedObject serialized = new(door);
            if (!preserveDirectionAndYaw)
            {
                serialized.FindProperty("direction").enumValueIndex = (int)direction;
            }

            serialized.FindProperty("gridOffsetSteps").intValue = Mathf.RoundToInt(offsetMeters / doorSnapMeters);
            serialized.FindProperty("localXSteps").intValue =
                Mathf.RoundToInt((anchor.x - gridOrigin.x) / doorSnapMeters);
            serialized.FindProperty("localZSteps").intValue =
                Mathf.RoundToInt((anchor.y - gridOrigin.y) / doorSnapMeters);
            serialized.FindProperty("gridOriginXMeters").floatValue = gridOrigin.x;
            serialized.FindProperty("gridOriginZMeters").floatValue = gridOrigin.y;
            if (!preserveDirectionAndYaw)
            {
                serialized.FindProperty("localYawDegrees").floatValue = yaw;
            }

            serialized.ApplyModifiedProperties();
        }

        private static Vector2 ResolveDoorGridOrigin(Transform roomRoot, IReadOnlyList<DoorSocket> doors)
        {
            if (roomRoot == null || doors == null || doors.Count == 0 || doors[0] == null)
            {
                return Vector2.zero;
            }

            Vector3 local = roomRoot.InverseTransformPoint(doors[0].transform.position);
            return new Vector2(local.x, local.z);
        }

        private static Vector2 ResolveDoorAlignedFootprintMin(
            RoomTemplate room,
            IReadOnlyList<DoorSocket> doors,
            Vector3 footprintSize,
            Vector2 gridOrigin)
        {
            RoomBounds bounds = room != null && room.Bounds != null ? room.Bounds :
                room != null ? room.GetComponentInChildren<RoomBounds>(true) : null;
            Vector2 actualMin = Vector2.zero;
            Vector2 actualMax = new(footprintSize.x, footprintSize.z);
            if (room != null && bounds != null && bounds.Box != null)
            {
                BoxCollider box = bounds.Box;
                Vector3 center = room.transform.InverseTransformPoint(box.transform.TransformPoint(box.center));
                actualMin = new Vector2(center.x - box.size.x * 0.5f, center.z - box.size.z * 0.5f);
                actualMax = new Vector2(center.x + box.size.x * 0.5f, center.z + box.size.z * 0.5f);
            }

            float minX = SnapMinAroundBounds(gridOrigin.x, actualMin.x, actualMax.x, footprintSize.x);
            float minZ = SnapMinAroundBounds(gridOrigin.y, actualMin.y, actualMax.y, footprintSize.z);
            return new Vector2(minX, minZ);
        }

        private static float SnapMinAroundBounds(float origin, float actualMin, float actualMax, float size)
        {
            float candidate = origin + Mathf.Floor((actualMin - origin) / LevelGenerationGridMetrics.DoorSnapMeters)
                * LevelGenerationGridMetrics.DoorSnapMeters;
            if (candidate + size < actualMax - 0.001f)
            {
                candidate = origin + Mathf.Ceil((actualMax - size - origin) / LevelGenerationGridMetrics.DoorSnapMeters)
                    * LevelGenerationGridMetrics.DoorSnapMeters;
            }

            return candidate;
        }

        private static void ApplyDoorData(
            DoorSocket door,
            DoorDirection direction,
            Vector3 local,
            float offsetMeters,
            Vector2 gridOrigin,
            float doorSnapMeters,
            bool preserveDirectionAndYaw = false)
        {
            float yaw = LocalYaw(direction);
            SerializedObject serialized = new(door);
            if (!preserveDirectionAndYaw)
            {
                serialized.FindProperty("direction").enumValueIndex = (int)direction;
            }

            serialized.FindProperty("gridOffsetSteps").intValue = Mathf.RoundToInt(offsetMeters / doorSnapMeters);
            serialized.FindProperty("localXSteps").intValue =
                Mathf.RoundToInt((local.x - gridOrigin.x) / doorSnapMeters);
            serialized.FindProperty("localZSteps").intValue =
                Mathf.RoundToInt((local.z - gridOrigin.y) / doorSnapMeters);
            serialized.FindProperty("gridOriginXMeters").floatValue = gridOrigin.x;
            serialized.FindProperty("gridOriginZMeters").floatValue = gridOrigin.y;
            if (!preserveDirectionAndYaw)
            {
                serialized.FindProperty("localYawDegrees").floatValue = yaw;
            }

            serialized.ApplyModifiedProperties();
        }

        private static float LocalYaw(DoorDirection direction)
        {
            return direction switch
            {
                DoorDirection.East => 0f,
                DoorDirection.South => 90f,
                DoorDirection.West => 180f,
                DoorDirection.North => 270f,
                _ => 0f
            };
        }

        private static DoorDirection ResolveAuthoredDirection(DoorSocket door, DoorDirection fallback)
        {
            if (door == null)
            {
                return fallback;
            }

            DoorDirection authored = door.Direction;
            return Enum.IsDefined(typeof(DoorDirection), authored) ? authored : fallback;
        }

        private void ValidateAllPrefabs()
        {
            _report.Clear();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { _prefabRoot });
            if (guids == null || guids.Length == 0)
            {
                AddWarning($"No prefabs found under {_prefabRoot}.");
                return;
            }

            int checkedRooms = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    continue;
                }

                RoomTemplate room = prefab.GetComponent<RoomTemplate>();
                if (room == null)
                {
                    continue;
                }

                checkedRooms++;
                ValidateRoom(room, false, path);
            }

            _report.Insert(0,
                new ReportLine($"Validated {checkedRooms} room prefab(s) under {_prefabRoot}.", MessageType.Info));
        }

        private void ValidateRoom(RoomTemplate room, bool clearReport, string assetPath = null)
        {
            if (clearReport)
            {
                _report.Clear();
            }

            if (room == null)
            {
                return;
            }

            RoomPrefabGridValidator validator = EnsureValidator(room);
            IReadOnlyList<string> issues = validator.ValidatePrefab();
            string prefix = string.IsNullOrWhiteSpace(assetPath) ? room.name : assetPath;
            if (issues.Count == 0)
            {
                AddInfo($"{prefix}: OK.");
                return;
            }

            for (int i = 0; i < issues.Count; i++)
            {
                AddWarning($"{prefix}: {issues[i]}");
            }
        }

        private static RoomPrefabGridValidator EnsureValidator(RoomTemplate room)
        {
            RoomPrefabGridValidator validator = room.GetComponent<RoomPrefabGridValidator>();
            if (validator != null)
            {
                return validator;
            }

            validator = Undo.AddComponent<RoomPrefabGridValidator>(room.gameObject);
            EditorUtility.SetDirty(room.gameObject);
            PrefabUtility.RecordPrefabInstancePropertyModifications(room.gameObject);
            return validator;
        }

        private static Vector3 ResolveRoomSize(RoomTemplate room)
        {
            Vector3 templateSize = ResolveRoomSizeFromTemplate(room);
            if (templateSize.x > 0f && templateSize.z > 0f)
            {
                return templateSize;
            }

            RoomBounds bounds = room.Bounds != null ? room.Bounds : room.GetComponentInChildren<RoomBounds>(true);
            return bounds != null && bounds.Box != null ? bounds.Box.size : Vector3.zero;
        }

        private void AddInfo(string message)
        {
            _report.Add(new ReportLine(message, MessageType.Info));
            Debug.Log($"[RoomAuthoringToolkit] {message}");
        }

        private void AddWarning(string message)
        {
            _report.Add(new ReportLine(message, MessageType.Warning));
            Debug.LogWarning($"[RoomAuthoringToolkit] {message}");
        }

        private readonly struct ReportLine
        {
            public readonly string Message;
            public readonly MessageType Type;

            public ReportLine(string message, MessageType type)
            {
                Message = message;
                Type = type;
            }
        }
    }
}
#endif
