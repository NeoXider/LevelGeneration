using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Domain.Enums;

namespace Neoxider.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Logical placement of a room in the level layout. Holds the chosen room template id and
    /// the per-socket door state after wiring. Does not reference Unity transforms.
    /// </summary>
    public sealed class PlacedRoom
    {
        public PlacedRoom(
            string nodeId,
            string roomTemplateId,
            GridPose pose,
            IReadOnlyDictionary<string, DoorState> doorStates,
            RoomWorldPose? worldPose = null,
            RoomFootprint? footprint = null,
            RoomFootprintShape? footprintShape = null,
            string contentId = null)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                throw new ArgumentException("Node id must be non-empty.", nameof(nodeId));
            }

            if (string.IsNullOrWhiteSpace(roomTemplateId))
            {
                throw new ArgumentException("Room template id must be non-empty.", nameof(roomTemplateId));
            }

            NodeId = nodeId;
            RoomTemplateId = roomTemplateId;
            Pose = pose;
            DoorStates = doorStates ?? new Dictionary<string, DoorState>(0);
            WorldPose = worldPose;
            Footprint = footprint ?? new RoomFootprint(1, 1);
            FootprintShape = footprintShape ?? RoomFootprintShape.Rectangle(Footprint);
            ContentId = contentId;
        }

        public string NodeId { get; }
        public string RoomTemplateId { get; }
        public GridPose Pose { get; }
        public IReadOnlyDictionary<string, DoorState> DoorStates { get; }
        public RoomWorldPose? WorldPose { get; }
        public RoomFootprint Footprint { get; }
        public RoomFootprintShape FootprintShape { get; }
        public string ContentId { get; }
    }

    /// <summary>
    /// Optional Unity-space pose for non-grid placement backends.
    /// </summary>
    public readonly struct RoomWorldPose : IEquatable<RoomWorldPose>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float RotationX;
        public readonly float RotationY;
        public readonly float RotationZ;
        public readonly float RotationW;

        public RoomWorldPose(
            float x,
            float y,
            float z,
            float rotationX,
            float rotationY,
            float rotationZ,
            float rotationW)
        {
            X = x;
            Y = y;
            Z = z;
            RotationX = rotationX;
            RotationY = rotationY;
            RotationZ = rotationZ;
            RotationW = rotationW;
        }

        public bool Equals(RoomWorldPose other)
        {
            return X.Equals(other.X)
                   && Y.Equals(other.Y)
                   && Z.Equals(other.Z)
                   && RotationX.Equals(other.RotationX)
                   && RotationY.Equals(other.RotationY)
                   && RotationZ.Equals(other.RotationZ)
                   && RotationW.Equals(other.RotationW);
        }

        public override bool Equals(object obj) => obj is RoomWorldPose other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z, RotationX, RotationY, RotationZ, RotationW);
    }

    /// <summary>
    /// 2D snap-grid pose. Coordinates are measured in door-snap units, not room cells.
    /// With the default authoring contract one snap unit is 2 meters.
    /// </summary>
    public readonly struct GridPose : IEquatable<GridPose>
    {
        public readonly float CellX;
        public readonly float CellY;
        public readonly int RotationDegrees;

        public GridPose(float cellX, float cellY, int rotationDegrees)
        {
            CellX = cellX;
            CellY = cellY;
            RotationDegrees = rotationDegrees;
        }

        public bool Equals(GridPose other) => CellX.Equals(other.CellX) && CellY.Equals(other.CellY) && RotationDegrees == other.RotationDegrees;

        public override bool Equals(object obj) => obj is GridPose other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(CellX, CellY, RotationDegrees);
    }
}
