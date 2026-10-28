using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Ссылочное равенство шаблонов комнат для кэшей на время одного прогона (снимки шаблонов, начальные состояния
    /// дверей): шаблоны не переопределяют равенство осмысленно, а Unity API в рабочем потоке трогать нельзя.
    /// </summary>
    internal sealed class TemplateReferenceComparer : IEqualityComparer<IRoomTemplateDescriptor>
    {
        public static readonly TemplateReferenceComparer Instance = new();

        public bool Equals(IRoomTemplateDescriptor x, IRoomTemplateDescriptor y) => ReferenceEquals(x, y);

        public int GetHashCode(IRoomTemplateDescriptor obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Immutable plain C# copy of a room template descriptor.
    /// Used by async/off-main-thread generation steps so placement never reads Unity objects.
    /// </summary>
    public sealed class RoomTemplateDescriptorSnapshot :
        IRoomTemplateDescriptor,
        IRoomFootprintShapeDescriptor,
        IRoomPlacementBoundsDescriptor,
        IRoomWallCollisionDescriptor,
        IInteractiveRoomTemplateDescriptor
    {
        private readonly IReadOnlyList<string> _tags;
        private readonly IReadOnlyList<IDoorSocketDescriptor> _doors;
        private readonly IReadOnlyList<RoomWallSegment2D> _wallSegments;

        public RoomTemplateDescriptorSnapshot(IRoomTemplateDescriptor source)
        {
            RoomId = source.RoomId;
            Kind = source.Kind;
            TilesetId = source.TilesetId;
            Difficulty = source.Difficulty;
            Weight = source.Weight;
            CanBeMainPath = source.CanBeMainPath;
            CanBeSideBranch = source.CanBeSideBranch;
            CanBeStart = source.CanBeStart;
            CanBeExit = source.CanBeExit;
            CanBeDeadEnd = source.CanBeDeadEnd;
            _tags = source.Tags != null ? new List<string>(source.Tags) : new List<string>(0);
            EstimatedDurationSeconds = source.EstimatedDurationSeconds;
            Footprint = source.Footprint;
            FootprintShape = RoomFootprintShapeResolver.ShapeFor(source);
            ModuleAssetId = source.ModuleAssetId;
            InteractiveKind = source is IInteractiveRoomTemplateDescriptor interactive
                ? interactive.InteractiveKind ?? string.Empty
                : string.Empty;
            InteractiveModuleHint = (source as IInteractiveRoomTemplateDescriptor)?.InteractiveModuleHint;
            InteractiveOptionCapacity = InteractiveMissionContentMatcher.ResolveInteractiveOptionCapacity(source);

            if (source is IRoomPlacementBoundsDescriptor bounds)
            {
                HasPlacementBounds = bounds.HasPlacementBounds;
                PlacementBoundsMinXMeters = bounds.PlacementBoundsMinXMeters;
                PlacementBoundsMinZMeters = bounds.PlacementBoundsMinZMeters;
            }

            _wallSegments = source is IRoomWallCollisionDescriptor walls && walls.WallCollisionSegments != null
                ? new List<RoomWallSegment2D>(walls.WallCollisionSegments)
                : new List<RoomWallSegment2D>(0);

            List<IDoorSocketDescriptor> doors = new(source.Doors?.Count ?? 0);
            if (source.Doors != null)
            {
                for (int i = 0; i < source.Doors.Count; i++)
                {
                    if (source.Doors[i] != null)
                    {
                        doors.Add(new DoorSocketDescriptorSnapshot(source.Doors[i]));
                    }
                }
            }

            _doors = doors;
        }

        public string RoomId { get; }
        public RoomKind Kind { get; }
        public string TilesetId { get; }
        public int Difficulty { get; }
        public int Weight { get; }
        public bool CanBeMainPath { get; }
        public bool CanBeSideBranch { get; }
        public bool CanBeStart { get; }
        public bool CanBeExit { get; }
        public bool CanBeDeadEnd { get; }
        public IReadOnlyList<string> Tags => _tags;
        public int EstimatedDurationSeconds { get; }
        public RoomFootprint Footprint { get; }
        public IReadOnlyList<IDoorSocketDescriptor> Doors => _doors;
        public string ModuleAssetId { get; }
        public RoomFootprintShape FootprintShape { get; }
        public bool HasPlacementBounds { get; }
        public float PlacementBoundsMinXMeters { get; }
        public float PlacementBoundsMinZMeters { get; }
        public IReadOnlyList<RoomWallSegment2D> WallCollisionSegments => _wallSegments;
        public string InteractiveKind { get; }
        public string InteractiveModuleHint { get; }
        public int InteractiveOptionCapacity { get; }

        public static Dictionary<string, IRoomTemplateDescriptor> Capture(
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> source)
        {
            Dictionary<string, IRoomTemplateDescriptor> result = new(source.Count, System.StringComparer.Ordinal);
            foreach (KeyValuePair<string, IRoomTemplateDescriptor> kv in source)
            {
                if (kv.Value != null)
                {
                    result[kv.Key] = new RoomTemplateDescriptorSnapshot(kv.Value);
                }
            }

            return result;
        }

        private sealed class DoorSocketDescriptorSnapshot :
            IDoorSocketMetricDescriptor,
            IDoorSocketConnectionPointDescriptor,
            IDoorSocketFrameDescriptor,
            IDoorSocketInitialStateDescriptor,
            IDoorSocketFlowDescriptor,
            IDoorSocketAvailabilityDescriptor
        {
            public DoorSocketDescriptorSnapshot(IDoorSocketDescriptor source)
            {
                SocketId = source.SocketId;
                Size = source.Size;
                Direction = source.Direction;
                GridOffsetSteps = source.GridOffsetSteps;
                LocalXSteps = source.LocalXSteps;
                LocalZSteps = source.LocalZSteps;
                LocalYawDegrees = source.LocalYawDegrees;
                FlowDirection = source.FlowDirection();
                CanBeMainPath = source.CanBeMainPath;
                CanBeSidePath = source.CanBeSidePath;
                CanBeLocked = source.CanBeLocked;
                GridOriginXMeters = source.GridOriginXMeters();
                GridOriginZMeters = source.GridOriginZMeters();
                LocalXMeters = source.LocalXMeters();
                LocalZMeters = source.LocalZMeters();
                ConnectionLocalXMeters = source.ConnectionLocalXMeters();
                ConnectionLocalYMeters = source.ConnectionLocalYMeters();
                ConnectionLocalZMeters = source.ConnectionLocalZMeters();
                FrameWidthMeters = source.FrameWidthMeters();
                InitialState = source is IDoorSocketInitialStateDescriptor state
                    ? state.InitialState
                    : DoorState.Closed;
                IsConnectable = source.IsConnectable();
            }

            public string SocketId { get; }
            public DoorSize Size { get; }
            public DoorDirection Direction { get; }
            public int GridOffsetSteps { get; }
            public int LocalXSteps { get; }
            public int LocalZSteps { get; }
            public float LocalYawDegrees { get; }
            public DoorSocketFlowDirection FlowDirection { get; }
            public bool CanBeMainPath { get; }
            public bool CanBeSidePath { get; }
            public bool CanBeLocked { get; }
            public float GridOriginXMeters { get; }
            public float GridOriginZMeters { get; }
            public float LocalXMeters { get; }
            public float LocalZMeters { get; }
            public float ConnectionLocalXMeters { get; }
            public float ConnectionLocalYMeters { get; }
            public float ConnectionLocalZMeters { get; }
            public float FrameWidthMeters { get; }
            public DoorState InitialState { get; }
            public bool IsConnectable { get; }
        }
    }
}
