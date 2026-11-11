using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Step 5: validate a finished layout against the rules listed in PLAN.md §3.7
    /// (start/exit presence, main path connectivity, door compatibility, room overlap, topology symmetry,
    /// key-lock distance, ...). Errors abort generation, warnings are reported.
    /// </summary>
    public interface ILevelValidator
    {
        public LevelValidationResult Validate(LevelLayout layout, IMissionConfigView config, ITilesetView tileset);
    }

    /// <summary>
    /// Optional validator extension for checks that need the original logical graph, such as full node/edge coverage.
    /// Existing custom validators can keep implementing <see cref="ILevelValidator"/> only.
    /// </summary>
    public interface IGraphAwareLevelValidator : ILevelValidator
    {
        public LevelValidationResult Validate(
            LevelLayout layout,
            LevelGraph graph,
            IMissionConfigView config,
            ITilesetView tileset);
    }
}
