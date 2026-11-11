using System;
using System.Collections.Generic;
using System.Threading;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Replacement hook for a physical-first placer.
    /// For now it keeps the same graph contract as <see cref="RoomPlacer"/> so it can be swapped safely.
    /// </summary>
    public sealed class DoorFrontierRoomPlacer : IRoomPlacer, IRoomPlacementDebugCountersSink
    {
        private readonly SocketBacktrackingRoomPlacer _socketPlacer;

        public DoorFrontierRoomPlacer(IInteractiveRoomKindRules interactiveRoomKinds)
        {
            _socketPlacer = new SocketBacktrackingRoomPlacer(interactiveRoomKinds);
        }

        public void SetDebugCounters(LevelGenerationDebugCounters debugCounters) => _socketPlacer.SetDebugCounters(debugCounters);

        public LevelLayout Place(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default)
        {
            if (graph == null)
            {
                return _socketPlacer.Place(graph, selections, rng, ct);
            }

            if (selections == null)
            {
                throw new ArgumentNullException(nameof(selections));
            }

            return _socketPlacer.Place(graph, selections, rng, ct);
        }
    }
}
