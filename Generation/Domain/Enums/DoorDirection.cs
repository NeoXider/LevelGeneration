namespace RedoSchool.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// Authoring-side local side of a door socket. Tools can infer it from the socket position;
    /// generation uses the resulting local yaw/anchor, not the socket id text.
    /// </summary>
    public enum DoorDirection
    {
        East = 0,
        South = 1,
        West = 2,
        North = 3
    }

    /// <summary>
    /// Authoring contract for how a door socket may be used by generation flow.
    /// </summary>
    public enum DoorSocketFlowDirection
    {
        /// <summary>The door may be used both as an incoming entrance and as an outgoing exit.</summary>
        Both = 0,

        /// <summary>The door may be used only as an incoming entrance into this room.</summary>
        EntryOnly = 1,

        /// <summary>The door may be used only as an outgoing exit from this room.</summary>
        ExitOnly = 2,

        /// <summary>The door is ignored by generation and editor snap tools.</summary>
        None = 3
    }
}
