using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Step 3 of generation: place selected rooms on a logical 2D grid, ensuring no AABB overlap.
    /// Returns the layout with grid poses but unconnected doors (DoorConnector finishes that).
    /// </summary>
    public interface IRoomPlacer
    {
        public LevelLayout Place(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default);
    }

    public interface IRoomPlacementDebugCountersSink
    {
        public void SetDebugCounters(LevelGenerationDebugCounters debugCounters);
    }

    public interface IRoomPlacementBudget
    {
        public void ThrowIfExpired();
    }

    /// <summary>
    /// Optional cooperative placement path for single-threaded hosts such as WebGL.
    /// </summary>
    public interface IAsyncRoomPlacer
    {
        public Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default);
    }

    public interface IAsyncRoomPlacerWithBudget : IAsyncRoomPlacer
    {
        public Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            IRoomPlacementBudget budget,
            CancellationToken ct = default);
    }
}
