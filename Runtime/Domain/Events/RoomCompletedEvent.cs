namespace RedoSchool.LevelGeneration.Runtime.Domain.Events
{
    /// <summary>
    /// Published on <c>IRoomEventBus</c> when an IRoomGameplayModule reports <c>Success=true</c>.
    /// Scoped to one mission.
    /// </summary>
    public readonly struct RoomCompletedEvent
    {
        public readonly string NodeId;
        public readonly bool Success;

        public RoomCompletedEvent(string nodeId, bool success)
        {
            NodeId = nodeId;
            Success = success;
        }
    }
}
