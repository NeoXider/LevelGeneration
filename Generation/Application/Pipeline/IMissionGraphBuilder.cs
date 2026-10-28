using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Step 1 of generation: turn an archetype + config into a logical <see cref="LevelGraph"/> of
    /// <see cref="RoomNode"/>s (Start → ... → Objective → Exit + optional branches). Pure C#.
    /// </summary>
    public interface IMissionGraphBuilder
    {
        public LevelGraph Build(IMissionConfigView config, ITilesetView tileset, IDeterministicRng rng);
    }
}
