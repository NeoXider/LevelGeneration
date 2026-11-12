using Neoxider.LevelGeneration.Generation.Domain.Enums;

namespace Neoxider.LevelGeneration.Runtime.Domain.Events
{
    /// <summary>
    /// Published on IRoomEventBus when the player enters a room.
    /// Scoped to one mission and does not cross MessagePipe.
    /// </summary>
    public readonly struct RoomEnteredEvent
    {
        public readonly string NodeId;
        public readonly RoomKind Kind;

        public RoomEnteredEvent(string nodeId, RoomKind kind)
        {
            NodeId = nodeId;
            Kind = kind;
        }
    }
}
