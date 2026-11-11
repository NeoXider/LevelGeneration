namespace RedoSchool.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// Toggle between full procedural, strict scripted and designer-authored hybrid generation.
    /// </summary>
    public enum LevelGenerationMode
    {
        /// <summary>Full procedural pipeline driven by archetype and seed.</summary>
        Procedural,

        /// <summary>Explicit Start -> sequence -> Exit path. No procedural branches or Other expansion.</summary>
        Scripted,

        /// <summary>[DEFAULT] Designer-authored critical path with procedural Other slots and branches.</summary>
        Hybrid
    }
}
