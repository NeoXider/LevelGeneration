namespace RedoSchool.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// When the extraction gate becomes usable. See PLAN.md §G.1.
    /// </summary>
    public enum ExtractionAvailability
    {
        /// <summary>[DEFAULT] Extraction is open from the very start (Warframe-style).</summary>
        AlwaysAvailable,

        /// <summary>Extraction unlocks only after the main objective is completed.</summary>
        AfterObjectiveCompleted,

        /// <summary>Extraction unlocks after N seconds (Survival-like).</summary>
        AfterTimer
    }
}
