using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Runtime.Domain.Events
{
    /// <summary>
    /// Published on <c>IRoomEventBus</c> after <c>ILevelContext.SetDoorState</c> mutates a door.
    /// Scoped to one mission.
    /// </summary>
    public readonly struct DoorStateChangedEvent
    {
        public readonly string NodeId;
        public readonly string SocketId;
        public readonly DoorState NewState;

        public DoorStateChangedEvent(string nodeId, string socketId, DoorState newState)
        {
            NodeId = nodeId;
            SocketId = socketId;
            NewState = newState;
        }
    }
}
