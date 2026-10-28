using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.Features.LevelGeneration.Generation.Application
{
    /// <summary>
    /// One room of an exactly forced mission path: the room kind, the mission content it hosts and an
    /// optional preferred room template. Unlike <see cref="IHybridSequenceStep"/> this carries the
    /// content id, which is what makes a planned interactive room able to render real lesson content.
    /// </summary>
    public interface IForcedMainPathRoom
    {
        /// <summary>Room kind of the forced node (used to pick a compatible room template).</summary>
        public RoomKind Kind { get; }

        /// <summary>Mission content id placed in this room; empty means a room without mission content.</summary>
        public string ContentId { get; }

        /// <summary>Room template id to prefer when selecting the prefab; may be empty.</summary>
        public string PreferredRoomTemplateId { get; }
    }

    /// <summary>
    /// Planning input that replaces the whole generated main path: start room, the listed rooms in
    /// order, exit room. Implemented by a mission config view that asks for one exact level
    /// (currently the cheat «тестовый уровень»); every other config generates its path as before.
    /// </summary>
    public interface IForcedMainPathProvider
    {
        /// <summary>
        /// Rooms of the forced path in order. An empty or missing list means «no forced path», and the
        /// planner then behaves exactly as for any other config.
        /// </summary>
        public IReadOnlyList<IForcedMainPathRoom> ForcedMainPath { get; }
    }

    /// <summary>Plain <see cref="IForcedMainPathRoom"/> value used by the forced-path planner.</summary>
    public sealed class ForcedMainPathRoom : IForcedMainPathRoom
    {
        public ForcedMainPathRoom(RoomKind kind, string contentId = null, string preferredRoomTemplateId = null)
        {
            Kind = kind;
            ContentId = contentId ?? string.Empty;
            PreferredRoomTemplateId = preferredRoomTemplateId ?? string.Empty;
        }

        public RoomKind Kind { get; }

        public string ContentId { get; }

        public string PreferredRoomTemplateId { get; }
    }
}