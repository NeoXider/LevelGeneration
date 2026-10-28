using System;

namespace RedoSchool.Features.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Local 2D wall segment used by generation collision checks.
    /// Coordinates are room-local meters on the X/Z plane.
    /// </summary>
    public readonly struct RoomWallSegment2D : IEquatable<RoomWallSegment2D>
    {
        public RoomWallSegment2D(float ax, float az, float bx, float bz, float widthMeters)
        {
            Ax = ax;
            Az = az;
            Bx = bx;
            Bz = bz;
            WidthMeters = Math.Max(0.01f, widthMeters);
        }

        public float Ax { get; }
        public float Az { get; }
        public float Bx { get; }
        public float Bz { get; }
        public float WidthMeters { get; }

        public bool Equals(RoomWallSegment2D other)
        {
            return Ax.Equals(other.Ax)
                   && Az.Equals(other.Az)
                   && Bx.Equals(other.Bx)
                   && Bz.Equals(other.Bz)
                   && WidthMeters.Equals(other.WidthMeters);
        }

        public override bool Equals(object obj) => obj is RoomWallSegment2D other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Ax, Az, Bx, Bz, WidthMeters);
    }
}
