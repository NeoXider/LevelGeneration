namespace RedoSchool.LevelGeneration.Generation.Domain.Enums
{
    /// <summary>
    /// Built-in archetype shapes referenced by <c>MissionArchetypeSO</c>.
    /// Designers can build custom archetypes — the enum is a hint, not a hard switch.
    /// </summary>
    public enum MissionArchetypeKind
    {
        Simple,
        Collection,
        WithSideBranches,
        Final
    }
}
