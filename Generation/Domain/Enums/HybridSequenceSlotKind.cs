namespace RedoSchool.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// Designer-authored main-path slot for hybrid generation.
    /// </summary>
    public enum HybridSequenceSlotKind
    {
        /// <summary>Procedural filler segment. Expands to one or more normal main-path rooms.</summary>
        Other,

        /// <summary>One room selected by <see cref="RoomKind"/>.</summary>
        RoomKind = 1,

        /// <summary>One concrete room prefab, resolved to its root RoomTemplate metadata.</summary>
        RoomPrefab = 2
    }
}
