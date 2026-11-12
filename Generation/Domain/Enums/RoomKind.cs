namespace Neoxider.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// Canonical generator-facing room kind.
    /// Shape is not encoded here: tunnel, branch and hub are derived from graph degree and connectable doors.
    /// </summary>
    public enum RoomKind
    {
        Passage = 0,
        Interactive = 1,
        Start = 2,
        Exit = 3,

        /// <summary>
        /// Blind terminal room: exactly one connectable door, no through-route. Used to cap free doors
        /// (e.g. MazeBranch wrong answers) and, later, bonus rooms. Kept distinct from <see cref="Passage"/>
        /// so the generator can tell a real dead-end cap apart from a corridor and route content accordingly.
        /// </summary>
        DeadEnd = 4
    }
}
