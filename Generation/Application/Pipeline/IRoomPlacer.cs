using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Application.Pipeline
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

    /// <summary>
    /// Бюджет ВСЕГО прогона генерации по часам. <see cref="ThrowIfExpired"/> бросает
    /// <see cref="System.OperationCanceledException"/>, когда прогон пора прервать (общий таймаут, отмена вызывающим).
    /// Он только прерывает генерацию и никогда не решает, какая раскладка получится у сида: предел самого поиска —
    /// счётный (шаги, откаты, оценённые позы), а не временной.
    /// </summary>
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

    /// <summary>
    /// Размещение, которое пишет подробности вызова в <see cref="RoomPlacementReport"/> (то, чего нет в
    /// <see cref="LevelLayout"/>): например, у какой двери лабиринта не поместилась заглушка неверного ответа.
    /// Отчёт создаёт вызывающий на каждый вызов — общего состояния между прогонами нет.
    /// </summary>
    public interface IReportingRoomPlacer
    {
        public LevelLayout Place(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            RoomPlacementReport report,
            CancellationToken ct = default);

        public Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            IRoomPlacementBudget budget,
            RoomPlacementReport report,
            CancellationToken ct = default);
    }

    /// <summary>Отчёт одного вызова размещения (см. <see cref="IReportingRoomPlacer"/>).</summary>
    public sealed class RoomPlacementReport
    {
        private readonly Dictionary<string, UnplacedMazeCap> _unplacedMazeCaps = new(System.StringComparer.Ordinal);

        /// <summary>Заглушки неверных ответов, которые не поместились без наложения: узел заглушки → место.</summary>
        public IReadOnlyDictionary<string, UnplacedMazeCap> UnplacedMazeCaps => _unplacedMazeCaps;

        public bool TryGetUnplacedMazeCap(string capNodeId, out UnplacedMazeCap cap)
        {
            if (capNodeId != null && _unplacedMazeCaps.TryGetValue(capNodeId, out cap))
            {
                return true;
            }

            cap = default;
            return false;
        }

        public void Clear() => _unplacedMazeCaps.Clear();

        public void AddUnplacedMazeCap(string capNodeId, string mazeNodeId, string mazeSocketId)
        {
            if (!string.IsNullOrEmpty(capNodeId))
            {
                _unplacedMazeCaps[capNodeId] = new UnplacedMazeCap(mazeNodeId, mazeSocketId);
            }
        }

        public readonly struct UnplacedMazeCap
        {
            public UnplacedMazeCap(string mazeNodeId, string mazeSocketId)
            {
                MazeNodeId = mazeNodeId;
                MazeSocketId = mazeSocketId;
            }

            public string MazeNodeId { get; }

            /// <summary>Дверь ответа лабиринта, назначенная этой заглушке; null — если назначения не было.</summary>
            public string MazeSocketId { get; }
        }
    }
}
