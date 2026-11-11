using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Read-only catalog of room templates available in the project.
    /// Returns descriptor views so Application code stays away from UnityEngine dependencies.
    /// </summary>
    public interface IRoomDatabase
    {
        public IReadOnlyList<IRoomTemplateDescriptor> AllRooms { get; }

        public IReadOnlyList<IRoomTemplateDescriptor> GetRoomsForTileset(string tilesetId);
    }

    public interface IRoomTemplateDescriptor
    {
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
        public IReadOnlyList<string> Tags { get; }
        public int EstimatedDurationSeconds { get; }
        public RoomFootprint Footprint { get; }
        public IReadOnlyList<IDoorSocketDescriptor> Doors { get; }
        public string ModuleAssetId { get; }
    }

    public interface IRoomFootprintShapeDescriptor
    {
        public RoomFootprintShape FootprintShape { get; }
    }

    public interface IRoomPlacementBoundsDescriptor
    {
        public bool HasPlacementBounds { get; }
        public float PlacementBoundsMinXMeters { get; }
        public float PlacementBoundsMinZMeters { get; }
    }

    public interface IRoomWallCollisionDescriptor
    {
        public IReadOnlyList<RoomWallSegment2D> WallCollisionSegments { get; }
    }

    public interface IDoorSocketDescriptor
    {
        public string SocketId { get; }

        public DoorSize Size { get; }

        // Legacy/editor metadata only. Production docking uses resolved connection point and LocalYawDegrees.
        public DoorDirection Direction { get; }
        public int GridOffsetSteps { get; }
        public int LocalXSteps { get; }
        public int LocalZSteps { get; }
        public float LocalYawDegrees { get; }
        public bool CanBeMainPath { get; }
        public bool CanBeSidePath { get; }
        public bool CanBeLocked { get; }
    }

    public interface IDoorSocketMetricDescriptor : IDoorSocketDescriptor
    {
        public float GridOriginXMeters { get; }
        public float GridOriginZMeters { get; }
        public float LocalXMeters { get; }
        public float LocalZMeters { get; }
    }

    public interface IDoorSocketConnectionPointDescriptor : IDoorSocketDescriptor
    {
        public float ConnectionLocalXMeters { get; }
        public float ConnectionLocalYMeters { get; }
        public float ConnectionLocalZMeters { get; }
    }

    public interface IDoorSocketFrameDescriptor : IDoorSocketDescriptor
    {
        public float FrameWidthMeters { get; }
    }

    public interface IDoorSocketInitialStateDescriptor : IDoorSocketDescriptor
    {
        public DoorState InitialState { get; }
    }

    public interface IDoorSocketFlowDescriptor : IDoorSocketDescriptor
    {
        public DoorSocketFlowDirection FlowDirection { get; }
    }

    public interface IDoorSocketAvailabilityDescriptor : IDoorSocketDescriptor
    {
        public bool IsConnectable { get; }
    }

    public static class DoorSocketDescriptorMetrics
    {
        public static float LocalXMeters(this IDoorSocketDescriptor door)
        {
            return door is IDoorSocketMetricDescriptor metric
                ? metric.LocalXMeters
                : door.LocalXSteps * LevelGenerationGridMetrics.DoorSnapMeters;
        }

        public static float LocalZMeters(this IDoorSocketDescriptor door)
        {
            return door is IDoorSocketMetricDescriptor metric
                ? metric.LocalZMeters
                : door.LocalZSteps * LevelGenerationGridMetrics.DoorSnapMeters;
        }

        public static float LocalXDoorSteps(this IDoorSocketDescriptor door) => LocalXMeters(door) / LevelGenerationGridMetrics.DoorSnapMeters;

        public static float LocalZDoorSteps(this IDoorSocketDescriptor door) => LocalZMeters(door) / LevelGenerationGridMetrics.DoorSnapMeters;

        public static float ConnectionLocalXMeters(this IDoorSocketDescriptor door)
        {
            return door is IDoorSocketConnectionPointDescriptor connection
                ? connection.ConnectionLocalXMeters
                : LocalXMeters(door);
        }

        public static float ConnectionLocalZMeters(this IDoorSocketDescriptor door)
        {
            return door is IDoorSocketConnectionPointDescriptor connection
                ? connection.ConnectionLocalZMeters
                : LocalZMeters(door);
        }

        public static float ConnectionLocalYMeters(this IDoorSocketDescriptor door)
        {
            return door is IDoorSocketConnectionPointDescriptor connection
                ? connection.ConnectionLocalYMeters
                : 0f;
        }

        public static float ConnectionLocalXDoorSteps(this IDoorSocketDescriptor door) => ConnectionLocalXMeters(door) / LevelGenerationGridMetrics.DoorSnapMeters;

        public static float ConnectionLocalZDoorSteps(this IDoorSocketDescriptor door) => ConnectionLocalZMeters(door) / LevelGenerationGridMetrics.DoorSnapMeters;

        public static float FrameWidthMeters(this IDoorSocketDescriptor door)
        {
            return door is IDoorSocketFrameDescriptor frame
                ? frame.FrameWidthMeters
                : DefaultFrameWidthMeters(door?.Size ?? DoorSize.Medium);
        }

        public static float DefaultFrameWidthMeters(DoorSize size)
        {
            return size switch
            {
                DoorSize.Small => 1.2f,
                DoorSize.Large => 3.2f,
                _ => 2.1f
            };
        }

        public static float GridOriginXMeters(this IDoorSocketDescriptor door) => door is IDoorSocketMetricDescriptor metric ? metric.GridOriginXMeters : 0f;

        public static float GridOriginZMeters(this IDoorSocketDescriptor door) => door is IDoorSocketMetricDescriptor metric ? metric.GridOriginZMeters : 0f;

        public static bool IsConnectable(this IDoorSocketDescriptor door)
        {
            return door != null
                   && door.FlowDirection() != DoorSocketFlowDirection.None
                   && (door is not IDoorSocketAvailabilityDescriptor availability || availability.IsConnectable)
                   && (door is not IDoorSocketInitialStateDescriptor initial
                       || initial.InitialState != DoorState.Disabled);
        }

        public static DoorSocketFlowDirection FlowDirection(this IDoorSocketDescriptor door)
        {
            return door is IDoorSocketFlowDescriptor flow
                ? flow.FlowDirection
                : DoorSocketFlowDirection.Both;
        }

        public static bool CanBeEntry(this IDoorSocketDescriptor door)
        {
            DoorSocketFlowDirection flow = FlowDirection(door);
            return flow == DoorSocketFlowDirection.Both || flow == DoorSocketFlowDirection.EntryOnly;
        }

        public static bool CanBeExit(this IDoorSocketDescriptor door)
        {
            DoorSocketFlowDirection flow = FlowDirection(door);
            return flow == DoorSocketFlowDirection.Both || flow == DoorSocketFlowDirection.ExitOnly;
        }
    }
}
