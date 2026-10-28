using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Step 2 of generation: pick a concrete room template for a logical node, honouring tileset,
    /// weight overrides, topology constraints. Returns null if no candidate fits.
    /// </summary>
    public interface IRoomSelector
    {
        public IRoomTemplateDescriptor Select(
            RoomNode node,
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase database,
            IDeterministicRng rng);
    }
}
