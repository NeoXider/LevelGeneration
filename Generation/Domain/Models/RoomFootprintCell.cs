using System;

namespace RedoSchool.LevelGeneration.Generation.Domain.Models
{
    [Serializable]
    public struct RoomFootprintCell : IEquatable<RoomFootprintCell>
    {
        public int X;
        public int Z;

        public RoomFootprintCell(int x, int z)
        {
            X = x;
            Z = z;
        }

        public bool Equals(RoomFootprintCell other) => X == other.X && Z == other.Z;

        public override bool Equals(object obj) => obj is RoomFootprintCell other && Equals(other);

        public override int GetHashCode() => (X * 397) ^ Z;
    }
}
