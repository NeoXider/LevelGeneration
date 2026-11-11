using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using RedoSchool.LevelGeneration.Generation.Infrastructure.Data;
using UnityEngine;
using UnityEngine.Serialization;

namespace RedoSchool.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// Root MonoBehaviour on a room prefab. This is the room template source of truth for generation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoomTemplate : MonoBehaviour, IRoomTemplateDescriptor, IRoomFootprintShapeDescriptor,
        IRoomPlacementBoundsDescriptor, IRoomWallCollisionDescriptor, IInteractiveRoomTemplateDescriptor
    {
        [Header("Identity")] [SerializeField] [Tooltip("Stable room id used by generation.")]
        private string roomId;

        [SerializeField] [Tooltip("Tileset this room belongs to.")]
        private TilesetSO tileset;

        [Header("Classification")] [SerializeField] [Tooltip("Generator-facing room kind. Tunnel/branch shape is derived from connectable doors.")]
        private RoomKind roomKind = RoomKind.Passage;

        [SerializeField]
        [InteractiveRoomKind]
        [Tooltip("Interactive room type (content kind, e.g. MazeBranch) this room hosts; mission JSON is matched by it. " +
                 "Must be a registered interactive room type. Empty: not an interactive-type room (or inferred from the room module type/name).")]
        private string interactiveRoomKind;

        [FormerlySerializedAs("interactiveKind")] [SerializeField] [HideInInspector]
        private string legacyInteractiveKind;

        [Header("Selection")] [SerializeField] [Tooltip("Mission difficulty tier for room selection.")] [Min(1)]
        private int difficulty = 1;

        [SerializeField] [Tooltip("Default selection weight for this room.")] [Range(0, 100)]
        private int weight = 10;

        [SerializeField] [Tooltip("Allows this room to be selected for the required main path.")]
        private bool canBeMainPath = true;

        [SerializeField] [Tooltip("Allows this room to be selected for optional side branches.")]
        private bool canBeSideBranch = true;

        [SerializeField] [Tooltip("Marks this room as a valid mission start room.")]
        private bool canBeStart;

        [SerializeField] [Tooltip("Marks this room as a valid mission exit room.")]
        private bool canBeExit;

        [SerializeField] [Tooltip("Allows this room to terminate an optional branch.")]
        private bool canBeDeadEnd;

        [Header("Metadata")] [SerializeField] [Tooltip("Optional tags used by selection rules and designer filters.")]
        private List<string> tags = new();

        [SerializeField] [Tooltip("Estimated time budget for this room in seconds.")] [Min(0)]
        private int estimatedDurationSeconds = 45;

        [Header("Default mission spawn rule")] [SerializeField] [Tooltip("Enables this room default mission spawn rule.")]
        private bool hasDefaultSpawnRule;

        [SerializeField] [Tooltip("Default spawn mode used when this room contributes a mission rule.")]
        private RoomTypeSpawnMode defaultSpawnMode;

        [SerializeField] [Tooltip("Overrides the default spawn chance when enabled.")]
        private bool defaultOverrideChance;

        [SerializeField] [Tooltip("Default spawn chance for chance-based room rules.")] [Range(0f, 1f)]
        private float defaultChance;

        [SerializeField] [Tooltip("Default minimum count for count-based room rules.")] [Min(0)]
        private int defaultMinCount;

        [SerializeField] [Tooltip("Default maximum count for count-based room rules.")] [Min(0)]
        private int defaultMaxCount;

        [Header("Grid footprint")] [SerializeField] [Tooltip("Room footprint width in grid cells.")] [Min(1)]
        private int widthCells = 1;

        [SerializeField] [Tooltip("Room footprint depth in grid cells.")] [Min(1)]
        private int depthCells = 1;

        [SerializeField] [Tooltip("Optional custom occupied footprint cells.")]
        private List<RoomFootprintCell> occupiedCells = new();

        [Header("Gameplay module")] [SerializeField] [Tooltip("Gameplay module ScriptableObject used by this room.")]
        private ScriptableObject module;

        [Header("Minimap bake")]
        [SerializeField]
        [Tooltip(
            "Parent GameObject for runtime minimap wall texture bake. If assigned, this has priority over Minimap Wall Roots.")]
        private GameObject minimapWallParent;

        [SerializeField]
        [Tooltip(
            "Wall roots used by the runtime minimap texture bake. Assign wall objects or parent objects that contain wall MeshFilters.")]
        private List<Transform> minimapWallRoots = new();

        [SerializeField] [Tooltip("When enabled, runtime minimap draws a top-down raster wall mask instead of footprint cells.")]
        private bool useMinimapWallGeometry = true;

        [Header("Prefab references")] [SerializeField] [Tooltip("Door sockets collected from child DoorSocket components.")]
        private List<DoorSocket> doors = new();

        [SerializeField] [Tooltip("Placement bounds collected from child RoomBounds component.")]
        private RoomBounds bounds;

        public IReadOnlyList<DoorSocket> Doors => GetResolvedDoors();
        public RoomBounds Bounds => bounds;
        public GameObject Prefab => gameObject;
        public ScriptableObject Module => module;
        public GameObject MinimapWallParent => minimapWallParent;
        public IReadOnlyList<Transform> MinimapWallRoots => minimapWallRoots;
        public bool UseMinimapWallGeometry => useMinimapWallGeometry;
        public bool HasDefaultSpawnRule => hasDefaultSpawnRule;
        public RoomTypeSpawnMode DefaultSpawnMode => defaultSpawnMode;
        public bool DefaultOverrideChance => defaultOverrideChance;
        public float DefaultChance => defaultChance;
        public int DefaultMinCount => defaultMinCount;
        public int DefaultMaxCount => defaultMaxCount;

        public string RoomId => !string.IsNullOrWhiteSpace(roomId) ? roomId : name;
        public RoomKind Kind => ResolveKind();
        public string TilesetId => tileset != null ? tileset.TilesetId : string.Empty;
        public int Difficulty => difficulty;
        public int Weight => weight;
        public bool CanBeMainPath => canBeMainPath;
        public bool CanBeSideBranch => canBeSideBranch;
        public bool CanBeStart => canBeStart;
        public bool CanBeExit => canBeExit;
        public bool CanBeDeadEnd => canBeDeadEnd;
        public IReadOnlyList<string> Tags => tags;
        public int EstimatedDurationSeconds => estimatedDurationSeconds;
        public RoomFootprint Footprint => new(widthCells, depthCells);
        public RoomFootprintShape FootprintShape => new(Footprint, occupiedCells);

        public IReadOnlyList<RoomWallSegment2D> WallCollisionSegments =>
            _wallCollisionSegmentsCache ??= RoomWallSegmentExtractor.Extract(this);

        public bool HasPlacementBounds => bounds != null && bounds.Box != null;
        public float PlacementBoundsMinXMeters => ResolvePlacementBoundsMin().x;
        public float PlacementBoundsMinZMeters => ResolvePlacementBoundsMin().z;
        public IReadOnlyList<IDoorSocketDescriptor> DoorsView => GetDoorViews();
        public string ModuleAssetId => module != null ? module.name : null;
        /// <summary>
        /// Declared interactive kind. Which room type that is (and the fallback by room module name) is resolved
        /// by the host game's room type registry, see <see cref="IInteractiveRoomKindRules"/>.
        /// </summary>
        public string InteractiveKind => ResolveInteractiveKind();

        /// <summary>Room module type and asset name: legacy inference of the room type for rooms without a kind.</summary>
        public string InteractiveModuleHint => module != null ? $"{module.GetType().Name} {module.name}" : null;

        public int InteractiveOptionCapacity => CountAnswerDoors();

        IReadOnlyList<IDoorSocketDescriptor> IRoomTemplateDescriptor.Doors => DoorsView;

        public DoorSocket FindSocket(string socketId)
        {
            IReadOnlyList<DoorSocket> resolvedDoors = GetResolvedDoors();
            for (int i = 0; i < resolvedDoors.Count; i++)
            {
                if (resolvedDoors[i] != null && resolvedDoors[i].SocketId == socketId)
                {
                    return resolvedDoors[i];
                }
            }

            return null;
        }

        private void Reset()
        {
            RefreshChildReferences();
            EnsureGridValidator();
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(interactiveRoomKind) && !string.IsNullOrWhiteSpace(legacyInteractiveKind))
            {
                interactiveRoomKind = legacyInteractiveKind.Trim();
            }

            if (_isRefreshingReferences || !NeedsReferenceRefresh())
            {
                return;
            }

            _isRefreshingReferences = true;
            try
            {
                RefreshChildReferences();
            }
            finally
            {
                _isRefreshingReferences = false;
            }
        }

        private RoomKind ResolveKind()
        {
            if (roomKind != RoomKind.Passage)
            {
                return roomKind;
            }

            if (canBeStart)
            {
                return RoomKind.Start;
            }

            if (canBeExit)
            {
                return RoomKind.Exit;
            }

            if (!string.IsNullOrWhiteSpace(InteractiveKind))
            {
                return RoomKind.Interactive;
            }

            return RoomKind.Passage;
        }

        private string ResolveInteractiveKind()
        {
            if (!string.IsNullOrWhiteSpace(interactiveRoomKind))
            {
                return interactiveRoomKind.Trim();
            }

            return string.IsNullOrWhiteSpace(legacyInteractiveKind) ? string.Empty : legacyInteractiveKind.Trim();
        }

#if UNITY_EDITOR
        public void ValidateForEditorTests() => OnValidate();
#endif

        [ContextMenu("Refresh Child References")]
        public void RefreshChildReferences()
        {
            doors = CollectUniqueDoors();
            bounds = GetComponentInChildren<RoomBounds>(true);
            _resolvedDoorCache = null;
            _doorViewCache = null;
            _wallCollisionSegmentsCache = null;
        }

        private IReadOnlyList<DoorSocket> GetResolvedDoors()
        {
            if (HasUsableSerializedDoors())
            {
                return doors;
            }

            _resolvedDoorCache ??= CollectUniqueDoors();
            return _resolvedDoorCache;
        }

        private bool HasUsableSerializedDoors()
        {
            if (doors == null || doors.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < doors.Count; i++)
            {
                if (doors[i] == null)
                {
                    return false;
                }
            }

            return true;
        }

        private bool NeedsReferenceRefresh()
        {
            if (doors == null || doors.Exists(door => door == null))
            {
                return true;
            }

            List<DoorSocket> current = CollectUniqueDoors();
            if (current.Count != doors.Count)
            {
                return true;
            }

            for (int i = 0; i < current.Count; i++)
            {
                if (current[i] != doors[i])
                {
                    return true;
                }
            }

            RoomBounds expectedBounds = GetComponentInChildren<RoomBounds>(true);
            return bounds != expectedBounds;
        }

        private List<DoorSocket> CollectUniqueDoors()
        {
            DoorSocket[] found = GetComponentsInChildren<DoorSocket>(true);
            List<DoorSocket> result = new(found.Length);
            for (int i = 0; i < found.Length; i++)
            {
                DoorSocket door = found[i];
                if (door == null || result.Contains(door))
                {
                    continue;
                }

                result.Add(door);
            }

            return result;
        }

        private void EnsureGridValidator()
        {
#if UNITY_EDITOR
            if (this == null || gameObject == null)
            {
                return;
            }

            if ((hideFlags & HideFlags.DontSave) != 0)
            {
                return;
            }

            bool isPrefabAsset = UnityEditor.PrefabUtility.IsPartOfPrefabAsset(gameObject);
            bool isPrefabInstance = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(gameObject);
            if (!isPrefabAsset && !isPrefabInstance)
            {
                return;
            }
#endif
            if (GetComponent<RoomPrefabGridValidator>() == null)
            {
                gameObject.AddComponent<RoomPrefabGridValidator>();
            }
        }

        public void InvalidateCachedGeometryForEditorTests()
        {
            _wallCollisionSegmentsCache = null;
            _resolvedDoorCache = null;
            _doorViewCache = null;
        }

        private List<IDoorSocketDescriptor> _doorViewCache;
        private List<DoorSocket> _resolvedDoorCache;
        private IReadOnlyList<RoomWallSegment2D> _wallCollisionSegmentsCache;
        private bool _isRefreshingReferences;

        private Vector3 ResolvePlacementBoundsMin()
        {
            if (!HasPlacementBounds)
            {
                return Vector3.zero;
            }

            BoxCollider box = bounds.Box;
            Vector3 center = transform.InverseTransformPoint(box.transform.TransformPoint(box.center));
            return center - box.size * 0.5f;
        }

        private IReadOnlyList<IDoorSocketDescriptor> GetDoorViews()
        {
            IReadOnlyList<DoorSocket> resolvedDoors = GetResolvedDoors();
            if (_doorViewCache != null && _doorViewCache.Count == resolvedDoors.Count)
            {
                return _doorViewCache;
            }

            _doorViewCache = new List<IDoorSocketDescriptor>(resolvedDoors.Count);
            for (int i = 0; i < resolvedDoors.Count; i++)
            {
                if (resolvedDoors[i] != null)
                {
                    _doorViewCache.Add(new DoorSocketView(this, resolvedDoors[i]));
                }
            }

            return _doorViewCache;
        }

        private int CountAnswerDoors()
        {
            IReadOnlyList<DoorSocket> resolvedDoors = GetResolvedDoors();
            if (resolvedDoors == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < resolvedDoors.Count; i++)
            {
                DoorSocket door = resolvedDoors[i];
                if (door != null
                    && door.enabled
                    && IsDoorEffectivelyActive(door)
                    && door.InitialState == DoorState.Locked)
                {
                    count++;
                }
            }

            return count;
        }

        private bool IsDoorEffectivelyActive(DoorSocket door) => IsTransformActiveInRoom(door != null ? door.transform : null, transform);

        private static bool IsTransformActiveInRoom(Transform target, Transform roomRoot)
        {
            if (target == null)
            {
                return false;
            }

            Transform current = target;
            while (current != null)
            {
                if (!current.gameObject.activeSelf)
                {
                    return false;
                }

                if (current == roomRoot)
                {
                    return true;
                }

                current = current.parent;
            }

            return target.gameObject.activeInHierarchy;
        }

        private sealed class DoorSocketView : IDoorSocketMetricDescriptor, IDoorSocketConnectionPointDescriptor,
            IDoorSocketFrameDescriptor, IDoorSocketInitialStateDescriptor, IDoorSocketFlowDescriptor,
            IDoorSocketAvailabilityDescriptor
        {
            private readonly RoomTemplate _room;
            private readonly DoorSocket _door;
            private bool _connectionResolved;
            private Vector3 _connectionLocalPoint;
            private float _connectionLocalYawDegrees;
            private float _frameWidthMeters;
            private const float MaxWallDerivedDoorCenterOffsetMeters = 0.45f;
            private const float MaxWallDerivedDoorYawDeltaDegrees = 35f;
            private const float MaxWallDerivedFrameWidthMultiplier = 1.65f;
            private const float MaxWallDerivedFrameWidthExtraMeters = 0.85f;

            public DoorSocketView(RoomTemplate room, DoorSocket door)
            {
                _room = room;
                _door = door;
            }

            public string SocketId => _door.SocketId;
            public DoorSize Size => _door.Size;
            public DoorDirection Direction => _door.Direction;
            public int GridOffsetSteps => _door.GridOffsetSteps;
            public int LocalXSteps => _door.LocalXSteps;
            public int LocalZSteps => _door.LocalZSteps;
            public float GridOriginXMeters => _door.GridOriginXMeters;
            public float GridOriginZMeters => _door.GridOriginZMeters;
            public float LocalXMeters => _door.LocalXMeters;
            public float LocalZMeters => _door.LocalZMeters;
            public float ConnectionLocalXMeters => ResolveConnectionLocalPoint().x;
            public float ConnectionLocalYMeters => ResolveConnectionLocalPoint().y;
            public float ConnectionLocalZMeters => ResolveConnectionLocalPoint().z;

            public float FrameWidthMeters
            {
                get
                {
                    ResolveDoorFrame();
                    return _frameWidthMeters;
                }
            }

            public float LocalYawDegrees => ResolveLocalYawDegrees();
            public DoorSocketFlowDirection FlowDirection => _door.FlowDirection;
            public bool CanBeMainPath => _door.CanBeMainPath;
            public bool CanBeSidePath => _door.CanBeSidePath;
            public bool CanBeLocked => _door.CanBeLocked;
            public DoorState InitialState => _door.InitialState;

            public bool IsConnectable => _door != null
                                         && _door.enabled
                                         && IsTransformActiveInRoom(_door.transform,
                                             _room != null ? _room.transform : null)
                                         && _door.InitialState != DoorState.Disabled;

            private Vector3 ResolveConnectionLocalPoint()
            {
                if (_connectionResolved)
                {
                    return _connectionLocalPoint;
                }

                ResolveDoorFrame();
                return _connectionLocalPoint;
            }

            private Vector3 ResolveSocketTransformLocalPoint()
            {
                RoomTemplate room = _room != null ? _room :
                    _door != null ? _door.GetComponentInParent<RoomTemplate>(true) : null;
                if (_door == null)
                {
                    return Vector3.zero;
                }

                if (room == null)
                {
                    return new Vector3(_door.LocalXMeters, 0f, _door.LocalZMeters);
                }

                Vector3 local = room.transform.InverseTransformPoint(_door.ResolveAuthoringWorldCenter());
                return local;
            }

            private float ResolveLocalYawDegrees()
            {
                ResolveDoorFrame();
                return _connectionLocalYawDegrees;
            }

            private void ResolveDoorFrame()
            {
                if (_connectionResolved)
                {
                    return;
                }

                Vector3 fallbackPoint = ResolveSocketTransformLocalPoint();
                float fallbackYaw =
                    NormalizeYaw(_door.ResolveAuthoringLocalYawDegrees(_room != null ? _room.transform : null));
                float defaultFrameWidth = DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(_door.Size);
                _frameWidthMeters = defaultFrameWidth;
                if (_room != null && !_room.UseMinimapWallGeometry)
                {
                    _connectionLocalPoint = fallbackPoint;
                    _connectionLocalYawDegrees = fallbackYaw;
                    _connectionResolved = true;
                    return;
                }

                _connectionLocalPoint = fallbackPoint;

                if (TryInferCorridorFrameFromWallSegments(fallbackPoint, out Vector3 inferredPoint,
                        out float inferredYaw, out float inferredWidth)
                    && IsWallDerivedFrameCompatible(
                        fallbackPoint,
                        fallbackYaw,
                        inferredPoint,
                        inferredYaw,
                        inferredWidth,
                        defaultFrameWidth))
                {
                    // Snap the connection point onto the visible corridor centerline so two opposite doors
                    // resolve to the exact same seam and dock at any yaw (0/180 included). Y is preserved
                    // from the authored door height (fallbackPoint.y) rather than the inferred ground plane.
                    _connectionLocalPoint = new Vector3(inferredPoint.x, fallbackPoint.y, inferredPoint.z);
                    _connectionLocalYawDegrees = inferredYaw;
                    _frameWidthMeters = inferredWidth;
                }
                else
                {
                    _connectionLocalYawDegrees = TryInferCorridorYawFromWallSegments(fallbackPoint, out inferredYaw)
                                                 && IsWallDerivedYawCompatible(fallbackYaw, inferredYaw)
                        ? inferredYaw
                        : fallbackYaw;
                }

                _connectionResolved = true;
            }

            private bool TryInferCorridorFrameFromWallSegments(
                Vector3 approximateLocalPoint,
                out Vector3 center,
                out float yawDegrees,
                out float frameWidthMeters)
            {
                center = default;
                yawDegrees = 0f;
                frameWidthMeters =
                    DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(_door != null ? _door.Size : DoorSize.Medium);
                if (_room == null || _door == null)
                {
                    return false;
                }

                IReadOnlyList<RoomWallSegment2D> segments = _room.WallCollisionSegments;
                if (segments == null || segments.Count < 2)
                {
                    return false;
                }

                Vector2 approximate = new(approximateLocalPoint.x, approximateLocalPoint.z);
                Vector2 roomCenter = CalculateSegmentCenter(segments);
                float bestScore = float.MaxValue;
                bool found = false;
                Vector2 bestCenter = Vector2.zero;
                Vector2 bestDirection = Vector2.right;
                float bestWidth = frameWidthMeters;

                for (int i = 0; i < segments.Count; i++)
                {
                    if (!TryGetSegmentLine(segments[i], out Vector2 a, out Vector2 b, out Vector2 direction))
                    {
                        continue;
                    }

                    float distanceA = DistancePointToSegment(approximate, a, b);
                    if (distanceA > 3.25f)
                    {
                        continue;
                    }

                    Vector2 normal = new(-direction.y, direction.x);
                    float offsetA = Vector2.Dot(a, normal);
                    float approximateOffset = Vector2.Dot(approximate, normal);
                    float approximateAxis = Vector2.Dot(approximate, direction);

                    for (int j = 0; j < segments.Count; j++)
                    {
                        if (j == i)
                        {
                            continue;
                        }

                        if (!TryGetSegmentLine(segments[j], out Vector2 c, out Vector2 d, out Vector2 otherDirection))
                        {
                            continue;
                        }

                        if (Mathf.Abs(Vector2.Dot(direction, otherDirection)) < 0.965f)
                        {
                            continue;
                        }

                        float distanceB = DistancePointToSegment(approximate, c, d);
                        if (distanceB > 7.5f)
                        {
                            continue;
                        }

                        float offsetB = Vector2.Dot(c, normal);
                        float separation = Mathf.Abs(offsetB - offsetA);
                        if (separation < 1.5f || separation > 8.5f)
                        {
                            continue;
                        }

                        float minOffset = Mathf.Min(offsetA, offsetB) - 0.85f;
                        float maxOffset = Mathf.Max(offsetA, offsetB) + 0.85f;
                        if (approximateOffset < minOffset || approximateOffset > maxOffset)
                        {
                            continue;
                        }

                        float centerOffset = (offsetA + offsetB) * 0.5f;
                        Vector2 candidateCenter = direction * approximateAxis + normal * centerOffset;
                        Vector2 outward = candidateCenter - roomCenter;
                        Vector2 candidateDirection = direction;
                        if (outward.sqrMagnitude > 0.0001f && Vector2.Dot(candidateDirection, outward.normalized) < 0f)
                        {
                            candidateDirection = -candidateDirection;
                        }

                        float score = distanceA + distanceB +
                                      Mathf.Abs(Vector2.Dot(candidateCenter - approximate, normal));
                        if (score >= bestScore)
                        {
                            continue;
                        }

                        bestScore = score;
                        bestCenter = candidateCenter;
                        bestDirection = candidateDirection;
                        bestWidth = separation;
                        found = true;
                    }
                }

                if (!found)
                {
                    return false;
                }

                center = new Vector3(bestCenter.x, 0f, bestCenter.y);
                yawDegrees = NormalizeYaw(Mathf.Atan2(-bestDirection.y, bestDirection.x) * Mathf.Rad2Deg);
                frameWidthMeters = bestWidth;
                return true;
            }

            private static bool IsWallDerivedFrameCompatible(
                Vector3 fallbackPoint,
                float fallbackYaw,
                Vector3 inferredPoint,
                float inferredYaw,
                float inferredWidth,
                float defaultFrameWidth)
            {
                Vector2 fallback = new(fallbackPoint.x, fallbackPoint.z);
                Vector2 inferred = new(inferredPoint.x, inferredPoint.z);
                if (Vector2.Distance(fallback, inferred) > MaxWallDerivedDoorCenterOffsetMeters)
                {
                    return false;
                }

                if (!IsWallDerivedYawCompatible(fallbackYaw, inferredYaw))
                {
                    return false;
                }

                float maxWidth = Mathf.Max(
                    defaultFrameWidth * MaxWallDerivedFrameWidthMultiplier,
                    defaultFrameWidth + MaxWallDerivedFrameWidthExtraMeters);
                return inferredWidth <= maxWidth;
            }

            private static bool IsWallDerivedYawCompatible(float fallbackYaw, float inferredYaw) => Mathf.Abs(Mathf.DeltaAngle(fallbackYaw, inferredYaw)) <= MaxWallDerivedDoorYawDeltaDegrees;

            private bool TryInferCorridorYawFromWallSegments(Vector3 approximateLocalPoint, out float yawDegrees)
            {
                yawDegrees = 0f;
                if (_room == null || _door == null)
                {
                    return false;
                }

                IReadOnlyList<RoomWallSegment2D> segments = _room.WallCollisionSegments;
                if (segments == null || segments.Count == 0)
                {
                    return false;
                }

                Vector2 doorPoint = new(approximateLocalPoint.x, approximateLocalPoint.z);
                Vector2 center = CalculateSegmentCenter(segments);
                Vector2 outward = doorPoint - center;
                if (outward.sqrMagnitude <= 0.0001f)
                {
                    outward = AuthoredYawVector(_door.LocalYawDegrees);
                }
                else
                {
                    outward.Normalize();
                }

                bool found = false;
                float bestDistance = float.MaxValue;
                Vector2 bestDirection = Vector2.right;
                for (int i = 0; i < segments.Count; i++)
                {
                    RoomWallSegment2D segment = segments[i];
                    Vector2 a = new(segment.Ax, segment.Az);
                    Vector2 b = new(segment.Bx, segment.Bz);
                    Vector2 wall = b - a;
                    if (wall.sqrMagnitude <= 0.0001f)
                    {
                        continue;
                    }

                    float distance = DistancePointToSegment(doorPoint, a, b);
                    float maxDistance = Mathf.Max(1.1f, segment.WidthMeters + 0.75f);
                    if (distance > maxDistance || distance >= bestDistance)
                    {
                        continue;
                    }

                    Vector2 direction = wall.normalized;
                    if (Vector2.Dot(direction, outward) < 0f)
                    {
                        direction = -direction;
                    }

                    found = true;
                    bestDistance = distance;
                    bestDirection = direction;
                }

                if (!found)
                {
                    return false;
                }

                yawDegrees = NormalizeYaw(Mathf.Atan2(-bestDirection.y, bestDirection.x) * Mathf.Rad2Deg);
                return true;
            }

            private static bool TryGetSegmentLine(RoomWallSegment2D segment, out Vector2 a, out Vector2 b,
                out Vector2 direction)
            {
                a = new Vector2(segment.Ax, segment.Az);
                b = new Vector2(segment.Bx, segment.Bz);
                direction = b - a;
                if (direction.sqrMagnitude <= 0.0001f)
                {
                    return false;
                }

                direction.Normalize();
                return true;
            }

            private static Vector2 CalculateSegmentCenter(IReadOnlyList<RoomWallSegment2D> segments)
            {
                float minX = float.MaxValue;
                float maxX = float.MinValue;
                float minZ = float.MaxValue;
                float maxZ = float.MinValue;
                for (int i = 0; i < segments.Count; i++)
                {
                    IncludePoint(segments[i].Ax, segments[i].Az, ref minX, ref maxX, ref minZ, ref maxZ);
                    IncludePoint(segments[i].Bx, segments[i].Bz, ref minX, ref maxX, ref minZ, ref maxZ);
                }

                return new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            }

            private static void IncludePoint(float x, float z, ref float minX, ref float maxX, ref float minZ,
                ref float maxZ)
            {
                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (z < minZ)
                {
                    minZ = z;
                }

                if (z > maxZ)
                {
                    maxZ = z;
                }
            }

            private static float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
            {
                Vector2 ab = b - a;
                if (ab.sqrMagnitude <= 0.0001f)
                {
                    return Vector2.Distance(point, a);
                }

                float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / ab.sqrMagnitude);
                return Vector2.Distance(point, a + ab * t);
            }

            private static Vector2 AuthoredYawVector(float yawDegrees)
            {
                float radians = yawDegrees * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians)).normalized;
            }

            private bool TryResolveVisualCenter(RoomTemplate room, out Vector3 center)
            {
                center = default;
                if (room == null || _door == null)
                {
                    return false;
                }

                if (TryCollectRendererBounds(out Bounds bounds)
                    || TryCollectColliderBounds(out bounds))
                {
                    center = room.transform.InverseTransformPoint(bounds.center);
                    center.y = 0f;
                    return true;
                }

                return false;
            }

            private bool TryCollectRendererBounds(out Bounds bounds)
            {
                bounds = default;
                Renderer[] renderers = _door.GetComponentsInChildren<Renderer>(true);
                bool hasBounds = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    if (!hasBounds)
                    {
                        bounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }

                return hasBounds;
            }

            private bool TryCollectColliderBounds(out Bounds bounds)
            {
                bounds = default;
                Collider[] colliders = _door.GetComponentsInChildren<Collider>(true);
                bool hasBounds = false;
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null || collider.isTrigger)
                    {
                        continue;
                    }

                    if (!hasBounds)
                    {
                        bounds = collider.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(collider.bounds);
                    }
                }

                return hasBounds;
            }

            private bool TryResolveDoorframeCenter(RoomTemplate room, Vector3 local, out Vector3 center)
            {
                center = local;
                if (room == null || room.Bounds == null || room.Bounds.Box == null)
                {
                    return false;
                }

                BoxCollider box = room.Bounds.Box;
                Vector3 boundsCenter = room.transform.InverseTransformPoint(box.transform.TransformPoint(box.center));
                float minX = boundsCenter.x - box.size.x * 0.5f;
                float maxX = boundsCenter.x + box.size.x * 0.5f;
                float minZ = boundsCenter.z - box.size.z * 0.5f;
                float maxZ = boundsCenter.z + box.size.z * 0.5f;

                switch (_door.Direction)
                {
                    case DoorDirection.East:
                        center.x = maxX;
                        center.z = ResolvePerpendicularDoorCenter(local.z, minZ, maxZ);
                        return true;
                    case DoorDirection.West:
                        center.x = minX;
                        center.z = ResolvePerpendicularDoorCenter(local.z, minZ, maxZ);
                        return true;
                    case DoorDirection.North:
                        center.x = ResolvePerpendicularDoorCenter(local.x, minX, maxX);
                        center.z = maxZ;
                        return true;
                    case DoorDirection.South:
                        center.x = ResolvePerpendicularDoorCenter(local.x, minX, maxX);
                        center.z = minZ;
                        return true;
                    default:
                        return false;
                }
            }

            private static float ResolvePerpendicularDoorCenter(float value, float min, float max)
            {
                const float tolerance = 0.08f;
                if (Mathf.Abs(value - min) <= tolerance)
                {
                    return value + LevelGenerationGridMetrics.DoorSnapMeters * 0.5f;
                }

                if (Mathf.Abs(value - max) <= tolerance)
                {
                    return value - LevelGenerationGridMetrics.DoorSnapMeters * 0.5f;
                }

                return Mathf.Clamp(value, min, max);
            }

            private static float NormalizeYaw(float yaw)
            {
                yaw %= 360f;
                return yaw < 0f ? yaw + 360f : yaw;
            }
        }
    }
}
