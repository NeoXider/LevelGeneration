namespace Neoxider.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// Runtime state of a door socket after generation or interaction.
    /// </summary>
    public enum DoorState
    {
        /// <summary>Connected to another room, passable.</summary>
        Open = 0,

        /// <summary>Connectable default state. Runtime logic can open it later.</summary>
        Closed = 1,

        /// <summary>Locked by room logic such as keys, quiz, secrets, or conditions.</summary>
        Locked = 3,

        /// <summary>Ignored by generation and hidden at runtime.</summary>
        Disabled = 5,

        /// <summary>Mission extraction gate.</summary>
        ExitGate = 6
    }
}
