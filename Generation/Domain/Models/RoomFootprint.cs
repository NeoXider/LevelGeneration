using System;

namespace RedoSchool.Features.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Room size in generator grid cells. Prefab dimensions should be multiples of the level cell size.
    /// </summary>
    public readonly struct RoomFootprint : IEquatable<RoomFootprint>
    {
        public readonly int WidthCells;
        public readonly int DepthCells;

        public RoomFootprint(int widthCells, int depthCells)
        {
            WidthCells = Math.Max(1, widthCells);
            DepthCells = Math.Max(1, depthCells);
        }

        public RoomFootprint Rotated(int rotationDegrees)
        {
            int normalized = (rotationDegrees % 360 + 360) % 360;
            return normalized == 90 || normalized == 270
                ? new RoomFootprint(DepthCells, WidthCells)
                : this;
        }

        public bool Equals(RoomFootprint other) => WidthCells == other.WidthCells && DepthCells == other.DepthCells;

        public override bool Equals(object obj) => obj is RoomFootprint other && Equals(other);

        public override int GetHashCode() => (WidthCells * 397) ^ DepthCells;
    }
}
