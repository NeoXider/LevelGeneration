namespace Neoxider.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// How the player exits a mission. See PLAN.md §G.
    /// </summary>
    public enum ExtractionMode
    {
        /// <summary>[DEFAULT] Start and Exit in different rooms.</summary>
        SeparateExitRoom,

        /// <summary>Start = Exit. Player returns to insertion room.</summary>
        SameAsInsertion,

        /// <summary>Exit gate appears in the Objective room after completion.</summary>
        InObjectiveRoom,

        /// <summary>Multiple exit rooms — player picks one.</summary>
        DoubleExit
    }
}
