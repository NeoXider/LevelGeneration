using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace RedoSchool.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// Prefab-level authoring contract for procedural rooms.
    /// Draws the room grid and validates that bounds and door sockets can be physically connected.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RoomPrefabGridValidator : MonoBehaviour
    {
        public const float DefaultCellSizeMeters = LevelGenerationGridMetrics.CellSizeMeters;
        public const float DefaultDoorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;

#if UNITY_EDITOR
        private const string DrawFootprintGizmosEditorPref =
            "RedoSchool.LevelGeneration.RoomAuthoring.DrawFootprintGizmos";

        public static bool DrawFootprintGizmos
        {
            get => EditorPrefs.GetBool(DrawFootprintGizmosEditorPref, false);
            set => EditorPrefs.SetBool(DrawFootprintGizmosEditorPref, value);
        }
#endif

#pragma warning disable 0414
        [SerializeField] [Tooltip("Настройка параметра \"cellSizeMeters\".")] [Min(0.5f)]
        private float cellSizeMeters = DefaultCellSizeMeters;

        [SerializeField] [Tooltip("Настройка параметра \"doorSnapMeters\".")] [Min(0.5f)]
        private float doorSnapMeters = DefaultDoorSnapMeters;
#pragma warning restore 0414
        [SerializeField] [Tooltip("Настройка параметра \"toleranceMeters\".")] [Min(0.01f)]
        private float toleranceMeters = 0.05f;

        [SerializeField] [Tooltip("Настройка параметра \"drawGizmos\".")]
        private bool drawGizmos = true;

        public float CellSizeMeters => LevelGenerationGridMetrics.CellSizeMeters;
        public float DoorSnapMeters => LevelGenerationGridMetrics.DoorSnapMeters;

        public IReadOnlyList<string> ValidatePrefab()
        {
            List<string> issues = new();
            RoomTemplate room = GetComponent<RoomTemplate>();
            if (room == null)
            {
                issues.Add("RoomTemplate component is missing on prefab root.");
                return issues;
            }

            RoomBounds bounds = room.Bounds != null ? room.Bounds : GetComponentInChildren<RoomBounds>(true);
            if (bounds == null || bounds.Box == null)
            {
                issues.Add("RoomBounds with BoxCollider is missing.");
                return issues;
            }

            RoomFootprint footprint = ResolveFootprint(room);
            float expectedWidth = footprint.WidthCells * CellSizeMeters;
            float expectedDepth = footprint.DepthCells * CellSizeMeters;
            Vector3 actualCenter =
                room.transform.InverseTransformPoint(bounds.Box.transform.TransformPoint(bounds.Box.center));
            Vector3 size = bounds.Box.size;
            float actualMinX = actualCenter.x - size.x * 0.5f;
            float actualMaxX = actualCenter.x + size.x * 0.5f;
            float actualMinZ = actualCenter.z - size.z * 0.5f;
            float actualMaxZ = actualCenter.z + size.z * 0.5f;
            Vector2 footprintMin = new(actualMinX, actualMinZ);
            Vector2 footprintMax = new(actualMaxX, actualMaxZ);
            IReadOnlyList<DoorSocket> doors = room.Doors != null && room.Doors.Count > 0
                ? room.Doors
                : GetComponentsInChildren<DoorSocket>(true);
            Vector2 gridOrigin = ResolveDoorGridOrigin(room.transform, doors);

            if (size.x <= toleranceMeters || size.z <= toleranceMeters)
            {
                issues.Add("RoomBounds X/Z size must be positive.");
            }

            if (!Approximately(size.x, expectedWidth) || !Approximately(size.z, expectedDepth))
            {
                issues.Add($"RoomBounds size must match footprint {expectedWidth:0.##}m x {expectedDepth:0.##}m.");
            }

            if (RequiresTwoDoors(room) && doors.Count < 2)
            {
                issues.Add("Main-path room must have at least 2 door sockets. Start/Exit/DeadEnd may have 1.");
            }

            for (int i = 0; i < doors.Count; i++)
            {
                ValidateDoor(room.transform, doors[i], footprintMin, footprintMax, gridOrigin, issues);
            }

            // Состояние двери адресуется по SocketId: две двери с одним id молча делят одно состояние. Проверяем
            // те же дескрипторы дверей, что видит генерация (RoomTemplate.DoorsView).
            if (DoorSocketIdRules.TryFindDuplicateConnectableSocketId(room.DoorsView, out string duplicateSocketId))
            {
                issues.Add(DoorSocketIdRules.DescribeDuplicate(((IRoomTemplateDescriptor)room).RoomId,
                    duplicateSocketId));
            }

            return issues;
        }

        [ContextMenu("Validate Room Prefab Contract")]
        private void ValidatePrefabFromInspector()
        {
            IReadOnlyList<string> issues = ValidatePrefab();
            if (issues.Count == 0)
            {
                Debug.Log($"[RoomPrefabGridValidator] {name}: OK.", this);
                return;
            }

            for (int i = 0; i < issues.Count; i++)
            {
                Debug.LogWarning($"[RoomPrefabGridValidator] {name}: {issues[i]}", this);
            }
        }

        private static RoomFootprint ResolveFootprint(RoomTemplate room)
        {
            if (room == null)
            {
                return new RoomFootprint(1, 1);
            }

            IRoomTemplateDescriptor descriptor = (IRoomTemplateDescriptor)room;
            return descriptor.Footprint;
        }

        private static bool RequiresTwoDoors(RoomTemplate room)
        {
            if (room == null)
            {
                return true;
            }

            IRoomTemplateDescriptor descriptor = (IRoomTemplateDescriptor)room;
            return descriptor.Kind != RoomKind.Start
                   && descriptor.Kind != RoomKind.Exit
                   && !(descriptor.Kind == RoomKind.Passage && CountConnectableDoors(room) <= 1 && room.CanBeDeadEnd);
        }

        private static int CountConnectableDoors(RoomTemplate room)
        {
            if (room?.Doors == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < room.Doors.Count; i++)
            {
                DoorSocket door = room.Doors[i];
                if (door != null
                    && door.enabled
                    && door.gameObject.activeSelf
                    && door.InitialState != DoorState.Disabled)
                {
                    count++;
                }
            }

            return count;
        }

        private void ValidateDoor(
            Transform roomRoot,
            DoorSocket door,
            Vector2 footprintMin,
            Vector2 footprintMax,
            Vector2 gridOrigin,
            List<string> issues)
        {
            if (door == null)
            {
                issues.Add("DoorSocket list contains null.");
                return;
            }

            if (TryInferDirectionFromNameOrId(door, out DoorDirection impliedDirection)
                && impliedDirection != door.Direction)
            {
                issues.Add(
                    $"Door '{door.SocketId}' name/id implies {impliedDirection}, but direction is {door.Direction}.");
            }

            Vector2 anchor = new(door.LocalXMeters, door.LocalZMeters);
            if (anchor.x < footprintMin.x - toleranceMeters
                || anchor.x > footprintMax.x + toleranceMeters
                || anchor.y < footprintMin.y - toleranceMeters
                || anchor.y > footprintMax.y + toleranceMeters)
            {
                issues.Add($"Door '{door.SocketId}' metadata anchor must stay inside the room footprint.");
            }

            float snapValue = door.Direction == DoorDirection.East || door.Direction == DoorDirection.West
                ? anchor.y
                : anchor.x;
            float originValue = door.Direction == DoorDirection.East || door.Direction == DoorDirection.West
                ? gridOrigin.y
                : gridOrigin.x;
            if (!IsMultiple(snapValue - originValue, DoorSnapMeters))
            {
                issues.Add(
                    $"Door '{door.SocketId}' must be snapped to {DoorSnapMeters:0.##}m grid from the first door.");
            }

            int expectedSteps = Mathf.RoundToInt((snapValue - originValue) / DoorSnapMeters);
            if (door.GridOffsetSteps != expectedSteps)
            {
                issues.Add(
                    $"Door '{door.SocketId}' gridOffsetSteps must be {expectedSteps} for offset {snapValue - originValue:0.##}m from the first door.");
            }

            int expectedLocalXSteps = Mathf.RoundToInt((anchor.x - gridOrigin.x) / DoorSnapMeters);
            int expectedLocalZSteps = Mathf.RoundToInt((anchor.y - gridOrigin.y) / DoorSnapMeters);
            if (door.LocalXSteps != expectedLocalXSteps || door.LocalZSteps != expectedLocalZSteps)
            {
                issues.Add(
                    $"Door '{door.SocketId}' local steps must be ({expectedLocalXSteps}, {expectedLocalZSteps}).");
            }

            if (!Approximately(door.GridOriginXMeters, gridOrigin.x) ||
                !Approximately(door.GridOriginZMeters, gridOrigin.y))
            {
                issues.Add(
                    $"Door '{door.SocketId}' grid origin must be ({gridOrigin.x:0.##}, {gridOrigin.y:0.##}) from the first door.");
            }

            if (float.IsNaN(door.LocalYawDegrees) || float.IsInfinity(door.LocalYawDegrees))
            {
                issues.Add($"Door '{door.SocketId}' localYawDegrees must be finite.");
            }
        }

        private static bool TryInferDirectionFromNameOrId(DoorSocket door, out DoorDirection direction)
        {
            string text = $"{door.SocketId} {door.name}".ToLowerInvariant();
            if (text.Contains("east"))
            {
                direction = DoorDirection.East;
                return true;
            }

            if (text.Contains("south"))
            {
                direction = DoorDirection.South;
                return true;
            }

            if (text.Contains("west"))
            {
                direction = DoorDirection.West;
                return true;
            }

            if (text.Contains("north"))
            {
                direction = DoorDirection.North;
                return true;
            }

            direction = default;
            return false;
        }

        private bool Approximately(float a, float b) => Mathf.Abs(a - b) <= toleranceMeters;

        private bool IsMultiple(float value, float step)
        {
            float nearest = Mathf.Round(value / step) * step;
            return Mathf.Abs(value - nearest) <= toleranceMeters;
        }

        private void OnValidate()
        {
            cellSizeMeters = DefaultCellSizeMeters;
            doorSnapMeters = DefaultDoorSnapMeters;
            if (toleranceMeters < 0.01f)
            {
                toleranceMeters = 0.05f;
            }
        }

        private void OnDrawGizmos()
        {
            if (!drawGizmos)
            {
                return;
            }

            RoomTemplate room = GetComponent<RoomTemplate>();
            RoomBounds bounds = room != null && room.Bounds != null
                ? room.Bounds
                : GetComponentInChildren<RoomBounds>(true);
            if (bounds == null || bounds.Box == null)
            {
                return;
            }

            BoxCollider box = bounds.Box;
            Matrix4x4 matrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            IReadOnlyList<DoorSocket> doors = room != null && room.Doors != null && room.Doors.Count > 0
                ? room.Doors
                : GetComponentsInChildren<DoorSocket>(true);
            Vector2 gridOrigin = ResolveDoorGridOrigin(transform, doors);
            RoomFootprint footprint = room != null ? ResolveFootprint(room) : new RoomFootprint(1, 1);
            Vector3 footprintSize = new(footprint.WidthCells * CellSizeMeters, box.size.y,
                footprint.DepthCells * CellSizeMeters);
            Vector3 actualCenter = transform.InverseTransformPoint(box.transform.TransformPoint(box.center));
            Vector2 actualMin = new(actualCenter.x - box.size.x * 0.5f, actualCenter.z - box.size.z * 0.5f);
            Vector3 footprintCenter = new(
                actualMin.x + footprintSize.x * 0.5f,
                box.center.y,
                actualMin.y + footprintSize.z * 0.5f);
            if (ShouldDrawFootprintGizmo())
            {
                Gizmos.color = new Color(1f, 0.78f, 0.05f, 1f);
                Gizmos.DrawWireCube(footprintCenter, footprintSize);
                Gizmos.DrawWireCube(footprintCenter, footprintSize * 0.995f);

                Gizmos.color = new Color(1f, 0.65f, 0.02f, 0.55f);
                DrawGrid(footprintCenter, footprintSize, gridOrigin);
            }

            Gizmos.color = new Color(0f, 0.95f, 1f, 0.8f);
            Gizmos.DrawWireCube(actualCenter, box.size);

            Gizmos.matrix = matrix;

            for (int i = 0; i < doors.Count; i++)
            {
                DrawDoorGizmo(doors[i]);
            }
        }

        private void DrawGrid(Vector3 center, Vector3 size, Vector2 gridOrigin)
        {
            float minX = center.x - size.x * 0.5f;
            float maxX = center.x + size.x * 0.5f;
            float minZ = center.z - size.z * 0.5f;
            float maxZ = center.z + size.z * 0.5f;
            float y = center.y;
            float startX = gridOrigin.x + Mathf.Ceil((minX - gridOrigin.x) / DoorSnapMeters) * DoorSnapMeters;
            float startZ = gridOrigin.y + Mathf.Ceil((minZ - gridOrigin.y) / DoorSnapMeters) * DoorSnapMeters;

            for (float x = startX; x <= maxX + 0.001f; x += DoorSnapMeters)
            {
                Gizmos.DrawLine(new Vector3(x, y, minZ), new Vector3(x, y, maxZ));
            }

            for (float z = startZ; z <= maxZ + 0.001f; z += DoorSnapMeters)
            {
                Gizmos.DrawLine(new Vector3(minX, y, z), new Vector3(maxX, y, z));
            }
        }

        private static Vector2 ResolveDoorGridOrigin(Transform roomRoot, IReadOnlyList<DoorSocket> doors)
        {
            if (roomRoot == null || doors == null || doors.Count == 0 || doors[0] == null)
            {
                return Vector2.zero;
            }

            Vector3 local = roomRoot.InverseTransformPoint(doors[0].transform.position);
            DoorSocket firstDoor = doors[0];
            bool hasAuthoredGridOrigin = !Mathf.Approximately(firstDoor.GridOriginXMeters, 0f)
                                         || !Mathf.Approximately(firstDoor.GridOriginZMeters, 0f)
                                         || firstDoor.LocalXSteps !=
                                         Mathf.RoundToInt(local.x / LevelGenerationGridMetrics.DoorSnapMeters)
                                         || firstDoor.LocalZSteps !=
                                         Mathf.RoundToInt(local.z / LevelGenerationGridMetrics.DoorSnapMeters);
            return hasAuthoredGridOrigin
                ? new Vector2(firstDoor.GridOriginXMeters, firstDoor.GridOriginZMeters)
                : new Vector2(local.x, local.z);
        }

        private static bool ShouldDrawFootprintGizmo()
        {
#if UNITY_EDITOR
            return DrawFootprintGizmos;
#else
            return true;
#endif
        }

        private static void DrawDoorGizmo(DoorSocket door)
        {
            if (door == null)
            {
                return;
            }

            Vector3 center = ResolveDoorGizmoCenter(door);
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(center, 0.18f);
        }

        private static Vector3 ResolveDoorGizmoCenter(DoorSocket door)
        {
            RoomTemplate room = door.GetComponentInParent<RoomTemplate>();
            IDoorSocketDescriptor descriptor = FindDoorDescriptor(room, door);
            return descriptor != null
                ? room.transform.TransformPoint(new Vector3(
                    descriptor.ConnectionLocalXMeters(),
                    0f,
                    descriptor.ConnectionLocalZMeters()))
                : door.transform.position;
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
                if (doors[i] != null && doors[i].SocketId == door.SocketId)
                {
                    return doors[i];
                }
            }

            return null;
        }
    }
}
