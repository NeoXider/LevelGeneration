using System;

namespace RedoSchool.Features.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Designer-supplied tag attached to a room template (e.g. "history", "supports_collectibles").
    /// Used by IRoomSelector filters.
    /// </summary>
    public readonly struct RoomTag : IEquatable<RoomTag>
    {
        public string Value { get; }

        public RoomTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Room tag must be non-empty.", nameof(value));
            }

            Value = value;
        }

        public bool Equals(RoomTag other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is RoomTag other && Equals(other);

        public override int GetHashCode() => Value != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(Value) : 0;

        public override string ToString() => Value ?? string.Empty;

        public static bool operator ==(RoomTag a, RoomTag b) => a.Equals(b);

        public static bool operator !=(RoomTag a, RoomTag b) => !a.Equals(b);
    }
}
