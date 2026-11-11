using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.Pipeline
{
    public enum LevelGenerationTraceStepKind
    {
        Info = 0,
        TryCandidate = 1,
        Accepted = 2,
        Rejected = 3,
        Backtracked = 4
    }

    public sealed class LevelGenerationTraceStep
    {
        public LevelGenerationTraceStep(
            LevelGenerationTraceStepKind kind,
            string nodeId,
            string roomTemplateId,
            string reason,
            PlacedRoom room = null,
            string placedNodeId = null,
            string placedSocketId = null,
            string unplacedSocketId = null)
        {
            Kind = kind;
            NodeId = nodeId ?? string.Empty;
            RoomTemplateId = roomTemplateId ?? string.Empty;
            Reason = reason ?? string.Empty;
            Room = room;
            PlacedNodeId = placedNodeId ?? string.Empty;
            PlacedSocketId = placedSocketId ?? string.Empty;
            UnplacedSocketId = unplacedSocketId ?? string.Empty;
        }

        public LevelGenerationTraceStepKind Kind { get; }
        public string NodeId { get; }
        public string RoomTemplateId { get; }
        public string Reason { get; }
        public PlacedRoom Room { get; }
        public string PlacedNodeId { get; }
        public string PlacedSocketId { get; }
        public string UnplacedSocketId { get; }
    }

    public sealed class LevelGenerationTraceStoppedException : Exception
    {
        public LevelGenerationTraceStoppedException(string message) : base(message)
        {
        }
    }

    public sealed class LevelGenerationPlacementTraceScope : IDisposable
    {
        private readonly bool _previousEnabled;
        private readonly bool _previousStopOnFirstReject;

        public LevelGenerationPlacementTraceScope(bool stopOnFirstReject)
        {
            _previousEnabled = LevelGenerationPlacementTrace.Enabled;
            _previousStopOnFirstReject = LevelGenerationPlacementTrace.StopOnFirstReject;
            LevelGenerationPlacementTrace.Reset();
            LevelGenerationPlacementTrace.Enabled = true;
            LevelGenerationPlacementTrace.StopOnFirstReject = stopOnFirstReject;
        }

        public void Dispose()
        {
            LevelGenerationPlacementTrace.Enabled = _previousEnabled;
            LevelGenerationPlacementTrace.StopOnFirstReject = _previousStopOnFirstReject;
        }
    }

    public static class LevelGenerationPlacementTrace
    {
        private static readonly object Gate = new();
        private static readonly List<LevelGenerationTraceStep> Steps = new();

        public static bool Enabled { get; set; }
        public static bool StopOnFirstReject { get; set; } = true;

        public static IReadOnlyList<LevelGenerationTraceStep> Snapshot()
        {
            lock (Gate)
            {
                return Steps.ToArray();
            }
        }

        public static void Reset()
        {
            lock (Gate)
            {
                Steps.Clear();
            }
        }

        public static void TryCandidate(
            string nodeId,
            string roomTemplateId,
            string placedNodeId,
            string placedSocketId,
            string unplacedSocketId,
            PlacedRoom room)
        {
            // Hot path: this runs for every door pair in BuildCandidates. Bail before allocating the
            // trace step when tracing is off, otherwise we churn the GC on the single-threaded WebGL loop.
            if (!Enabled)
            {
                return;
            }

            Record(new LevelGenerationTraceStep(
                LevelGenerationTraceStepKind.TryCandidate,
                nodeId,
                roomTemplateId,
                "try candidate",
                room,
                placedNodeId,
                placedSocketId,
                unplacedSocketId));
        }

        public static void Accepted(
            string nodeId,
            string roomTemplateId,
            string placedNodeId,
            string placedSocketId,
            string unplacedSocketId,
            PlacedRoom room)
        {
            if (!Enabled)
            {
                return;
            }

            Record(new LevelGenerationTraceStep(
                LevelGenerationTraceStepKind.Accepted,
                nodeId,
                roomTemplateId,
                "accepted",
                room,
                placedNodeId,
                placedSocketId,
                unplacedSocketId));
        }

        public static void Rejected(
            string nodeId,
            string roomTemplateId,
            string reason,
            PlacedRoom room = null,
            string placedNodeId = null,
            string placedSocketId = null,
            string unplacedSocketId = null)
        {
            if (!Enabled)
            {
                return;
            }

            Record(new LevelGenerationTraceStep(
                LevelGenerationTraceStepKind.Rejected,
                nodeId,
                roomTemplateId,
                reason,
                room,
                placedNodeId,
                placedSocketId,
                unplacedSocketId));

            if (StopOnFirstReject && room != null)
            {
                throw new LevelGenerationTraceStoppedException(
                    $"Placement trace stopped on first reject: {nodeId} {roomTemplateId} - {reason}");
            }
        }

        public static void Backtracked(string nodeId, string roomTemplateId)
        {
            if (!Enabled)
            {
                return;
            }

            Record(new LevelGenerationTraceStep(
                LevelGenerationTraceStepKind.Backtracked,
                nodeId,
                roomTemplateId,
                "backtracked"));
        }

        private static void Record(LevelGenerationTraceStep step)
        {
            if (!Enabled || step == null)
            {
                return;
            }

            lock (Gate)
            {
                Steps.Add(step);
            }
        }
    }

    /// <summary>
    /// Узлы «неверный ответ лабиринта → тупиковая заглушка» и обрезка графа по ним. Единственный источник этого
    /// набора: размещение не требует таких узлов для успеха (ставит их только без наложения), а
    /// <see cref="MissionGenerator"/> не считает их отсутствие провалом покрытия и убирает неразмещённые из графа
    /// до соединения дверей и проверки — их дверь запечатывается, генератор пишет предупреждение.
    /// </summary>
    public static class MazeWrongAnswerDeadEnds
    {
        public static HashSet<string> CollectNodeIds(LevelGraph graph)
        {
            HashSet<string> result = new(StringComparer.Ordinal);
            if (graph?.Edges == null)
            {
                return result;
            }

            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd && !string.IsNullOrEmpty(edge.ToNodeId))
                {
                    result.Add(edge.ToNodeId);
                }
            }

            return result;
        }

        /// <summary>
        /// Граф без неразмещённых узлов из <paramref name="bestEffortNodeIds"/> и всех их рёбер. Возвращает тот же
        /// экземпляр, если убирать нечего. Убранные рёбра «неверный ответ → заглушка» дописываются в
        /// <paramref name="droppedEdges"/> (если передан) — по ним генератор формирует предупреждения.
        /// </summary>
        public static LevelGraph WithoutUnplacedNodes(
            LevelGraph graph,
            LevelLayout layout,
            ISet<string> bestEffortNodeIds,
            List<LevelGraphEdge> droppedEdges = null)
        {
            if (graph?.Nodes == null || bestEffortNodeIds == null || bestEffortNodeIds.Count == 0)
            {
                return graph;
            }

            HashSet<string> placed = new(StringComparer.Ordinal);
            if (layout?.Rooms != null)
            {
                for (int i = 0; i < layout.Rooms.Count; i++)
                {
                    if (!string.IsNullOrEmpty(layout.Rooms[i]?.NodeId))
                    {
                        placed.Add(layout.Rooms[i].NodeId);
                    }
                }
            }

            HashSet<string> dropped = null;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                string nodeId = graph.Nodes[i].NodeId;
                if (bestEffortNodeIds.Contains(nodeId) && !placed.Contains(nodeId))
                {
                    dropped ??= new HashSet<string>(StringComparer.Ordinal);
                    dropped.Add(nodeId);
                }
            }

            if (dropped == null)
            {
                return graph;
            }

            List<RoomNode> nodes = new(graph.Nodes.Count - dropped.Count);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (!dropped.Contains(graph.Nodes[i].NodeId))
                {
                    nodes.Add(graph.Nodes[i]);
                }
            }

            List<LevelGraphEdge> edges = new(graph.Edges.Count);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                bool touchesDropped = (edge.FromNodeId != null && dropped.Contains(edge.FromNodeId))
                                      || (edge.ToNodeId != null && dropped.Contains(edge.ToNodeId));
                if (!touchesDropped)
                {
                    edges.Add(edge);
                    continue;
                }

                if (edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd
                    && edge.ToNodeId != null
                    && dropped.Contains(edge.ToNodeId))
                {
                    droppedEdges?.Add(edge);
                }
            }

            return new LevelGraph(nodes, edges);
        }
    }

    /// <summary>
    /// Constraint placer for authored room prefabs.
    /// Places rooms by matching door socket centers, supports arbitrary yaw angles, and backtracks
    /// when a local placement blocks later graph edges.
    /// <para>
    /// Исход для сида детерминирован и не зависит от скорости машины: поиск ограничен счётными бюджетами
    /// (шаги поиска, откаты, перезапуски и <see cref="CandidateEvaluationBudget"/> — оценённые позы-кандидаты).
    /// Часы (токен отмены, <see cref="IRoomPlacementBudget"/>) только прерывают весь прогон генерации и никогда не
    /// решают, какая раскладка получится у сида.
    /// </para>
    /// </summary>
    public sealed class SocketBacktrackingRoomPlacer : IRoomPlacer, IAsyncRoomPlacerWithBudget,
        IReportingRoomPlacer, IRoomPlacementDebugCountersSink
    {
        private const int MaxRestarts = 3;
        private const int MinSearchStepsPerRestart = 2000;
        private const int SearchStepsPerGraphNode = 140;
        private const int SearchStepsPerGraphEdge = 30;
        private const int MaxCandidatesPerFrontier = 16;
        private const int MaxBacktracksPerRestart = 8000;
        private const int CancellationCheckBacktrackInterval = 64;

        /// <summary>Нижняя граница автоматического <see cref="CandidateEvaluationBudget"/>.</summary>
        public const int MinCandidateEvaluationsPerPlacement = 40000;

        /// <summary>Сколько оценок позы добавляет каждый узел графа к автоматическому бюджету.</summary>
        public const int CandidateEvaluationsPerGraphNode = 2000;

        // Позы комнаты-лабиринта, у которых не помещается заглушка неверного ответа, уходят в конец очереди:
        // штраф больше любой суммы обычных слагаемых предпочтения (порядок дверей ×1e6, теснота, случайный разброс).
        // Число учитываемых промахов ограничено, чтобы сумма не вышла за int.
        private const int MazeCapMissPenalty = 50_000_000;
        private const int MaxPenalizedMazeCapMisses = 40;

        // Кооперативная уступка меряется ВРЕМЕНЕМ, а не числом шагов. Смысл уступки — не занимать поток
        // хоста дольше этого бюджета; сколько шагов поиска в него уложится, зависит от машины и графа.
        // Уступка не влияет на исход: поиск после неё продолжается с того же места.
        //
        // 33 мс ≈ два кадра при 60 fps и заметно ниже браузерного порога «долгой задачи» (50 мс).
        // Генерация идёт на экране загрузки, а не в геймплее, поэтому редкая двухкадровая заминка там
        // невидима — а вот накладные расходы на уступки видны напрямую: при 8 мс coop-путь тратил вдвое
        // больше времени на планировщик, чем на саму работу.
        private const double CooperativeYieldIntervalMilliseconds = 33.0;

        // Как часто СМОТРЕТЬ на часы (сама проверка дешёвая, но не бесплатная в рекурсии поиска).
        private const int AsyncYieldCheckStepInterval = 16;
        private const int AsyncYieldGreedyEdgeInterval = 4;
        private const float DoorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;

        // Broadphase margin in meters. The detailed per-segment test only runs for rooms whose wall AABBs
        // are within this distance. It is deliberately generous (well above the seam tolerance) so the
        // pre-reject can never discard a pair the detailed test would flag, keeping placement deterministic.
        private const float BroadphaseMarginMeters = DoorSnapMeters;

        private static readonly IReadOnlyDictionary<string, DoorState> NoDoorStates =
            new ReadOnlyDictionary<string, DoorState>(new Dictionary<string, DoorState>(0));

        private readonly IInteractiveRoomKindRules _interactiveRoomKinds;
        private LevelGenerationDebugCounters _debugCounters;

        /// <param name="interactiveRoomKinds">
        /// Interactive room types of the host game: which rooms answer through door-per-option branches.
        /// </param>
        public SocketBacktrackingRoomPlacer(IInteractiveRoomKindRules interactiveRoomKinds)
        {
            _interactiveRoomKinds = interactiveRoomKinds
                                    ?? throw new ArgumentNullException(nameof(interactiveRoomKinds));
        }

        /// <summary>
        /// Детерминированный бюджет одного вызова <see cref="Place"/>/<c>PlaceAsync</c>: сколько поз-кандидатов
        /// (пара дверей → поза комнаты → проверка пересечений) можно оценить на все жадные проходы, поиски и
        /// перезапуски вместе. Исчерпан — размещение возвращает лучшую частичную раскладку, генератор переходит к
        /// следующей стадии лесенки того же сида. Ноль или меньше — автоматически:
        /// <c>max(MinCandidateEvaluationsPerPlacement, узлы графа × CandidateEvaluationsPerGraphNode)</c>.
        /// Это настройка, а не таймаут: одно и то же значение даёт одну и ту же раскладку на любой машине.
        /// </summary>
        public int CandidateEvaluationBudget { get; set; }

        public void SetDebugCounters(LevelGenerationDebugCounters debugCounters) => _debugCounters = debugCounters;

        public LevelLayout Place(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default) =>
            Place(graph, selections, rng, null, ct);

        /// <summary>
        /// Размещение с отчётом: какие заглушки неверных ответов не поместились и у какой двери лабиринта.
        /// </summary>
        public LevelLayout Place(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            RoomPlacementReport report,
            CancellationToken ct = default)
        {
            report?.Clear();
            if (graph == null || graph.Nodes == null || graph.Nodes.Count == 0)
            {
                return new LevelLayout(Array.Empty<PlacedRoom>(), Array.Empty<DoorConnection>(), "n_start",
                    Array.Empty<string>());
            }

            if (selections == null)
            {
                throw new ArgumentNullException(nameof(selections));
            }

            rng ??= new NullRng();
            PlacementRun run = new(graph, selections, rng, ResolveCandidateEvaluationBudget(graph),
                _interactiveRoomKinds, report);
            LevelLayout best = null;
            for (int attempt = 0; attempt < MaxRestarts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (run.Work.IsExhausted)
                {
                    break;
                }

                PlacementState greedyState = new(run, attempt, ct, _debugCounters);
                if (greedyState.TryPlaceMainThenSideLeaves())
                {
                    return greedyState.ToLayout();
                }

                PlacementState state = new(run, attempt, ct, _debugCounters);
                if (state.Search())
                {
                    return state.ToLayout();
                }

                LevelLayout partial = state.ToBestLayout();
                if (best == null || partial.Rooms.Count > best.Rooms.Count)
                {
                    best = partial;
                }
            }

            ct.ThrowIfCancellationRequested();
            ReportExhaustion(run);
            return best ?? new PlacementState(run, 0, ct, _debugCounters).ToBestLayout();
        }

        public async Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default) =>
            await PlaceAsync(graph, selections, rng, null, null, ct);

        public async Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            IRoomPlacementBudget budget,
            CancellationToken ct = default) =>
            await PlaceAsync(graph, selections, rng, budget, null, ct);

        public async Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            IRoomPlacementBudget budget,
            RoomPlacementReport report,
            CancellationToken ct = default)
        {
            report?.Clear();
            if (graph == null || graph.Nodes == null || graph.Nodes.Count == 0)
            {
                return new LevelLayout(Array.Empty<PlacedRoom>(), Array.Empty<DoorConnection>(), "n_start",
                    Array.Empty<string>());
            }

            if (selections == null)
            {
                throw new ArgumentNullException(nameof(selections));
            }

            rng ??= new NullRng();
            PlacementRun run = new(graph, selections, rng, ResolveCandidateEvaluationBudget(graph),
                _interactiveRoomKinds, report);
            LevelLayout best = null;
            for (int attempt = 0; attempt < MaxRestarts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (run.Work.IsExhausted)
                {
                    break;
                }

                await YieldAsync(ct, budget);

                PlacementState greedyState = new(run, attempt, ct, _debugCounters, budget);
                if (await greedyState.TryPlaceMainThenSideLeavesAsync())
                {
                    return greedyState.ToLayout();
                }

                await YieldAsync(ct, budget);

                PlacementState state = new(run, attempt, ct, _debugCounters, budget);
                if (await state.SearchAsync())
                {
                    return state.ToLayout();
                }

                LevelLayout partial = state.ToBestLayout();
                if (best == null || partial.Rooms.Count > best.Rooms.Count)
                {
                    best = partial;
                }
            }

            ct.ThrowIfCancellationRequested();
            ReportExhaustion(run);
            return best ?? new PlacementState(run, 0, ct, _debugCounters).ToBestLayout();
        }

        private int ResolveCandidateEvaluationBudget(LevelGraph graph)
        {
            if (CandidateEvaluationBudget > 0)
            {
                return CandidateEvaluationBudget;
            }

            long scaled = (long)(graph?.Nodes?.Count ?? 0) * CandidateEvaluationsPerGraphNode;
            return (int)Math.Min(int.MaxValue, Math.Max(MinCandidateEvaluationsPerPlacement, scaled));
        }

        private void ReportExhaustion(PlacementRun run)
        {
            if (run.Work.IsExhausted)
            {
                _debugCounters?.IncrementPlacementBudgetExhaustions();
            }
        }

        private static async Task YieldAsync(CancellationToken ct, IRoomPlacementBudget budget)
        {
            ct.ThrowIfCancellationRequested();
            budget?.ThrowIfExpired();
            await Task.Yield();
            budget?.ThrowIfExpired();
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Неизменяемые на время одного вызова Place данные: граф, выбор комнат, назначение дверей лабиринтов,
        /// счётный бюджет работы и кэши, общие для всех жадных проходов, поисков и перезапусков этого вызова.
        /// </summary>
        private sealed class PlacementRun
        {
            private readonly Dictionary<IRoomTemplateDescriptor, IReadOnlyDictionary<string, DoorState>>
                _emptyDoorStatesByTemplate = new(TemplateReferenceComparer.Instance);

            public PlacementRun(
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
                IDeterministicRng rng,
                int candidateEvaluationBudget,
                IInteractiveRoomKindRules interactiveRoomKinds,
                RoomPlacementReport report)
            {
                Graph = graph;
                Report = report;
                Selections = selections;
                Rng = rng;
                InteractiveRoomKinds = interactiveRoomKinds ?? InteractiveRoomKindRules.Empty;
                NodesById = BuildNodeLookup(graph);
                BestEffortNodeIds = MazeWrongAnswerDeadEnds.CollectNodeIds(graph);
                CapEdgesByMazeNode = BuildCapEdges(graph);
                int edgeCount = graph.Edges?.Count ?? 0;
                MaxSearchSteps = Math.Max(
                    MinSearchStepsPerRestart,
                    graph.Nodes.Count * SearchStepsPerGraphNode + edgeCount * SearchStepsPerGraphEdge);
                Work = new PlacementWorkBudget(candidateEvaluationBudget);

                // Планы дверей лабиринтов — первым потреблением ГСЧ этого вызова: правильная дверь выбирается
                // сидом, а не «самая прямая». Для миссий без лабиринтов ГСЧ здесь не трогается.
                _mazePlans = BuildMazePlans(graph, selections, rng, InteractiveRoomKinds);
            }

            private readonly IReadOnlyDictionary<string, MazeDoorPlan> _mazePlans;

            public RoomPlacementReport Report { get; }

            /// <summary>
            /// Назначение дверей лабиринта на перезапуск <paramref name="restartIndex"/>. Каждый перезапуск берёт
            /// следующую по кругу дверь ответа как правильную (начало круга — от сида), поэтому неудачный выбор
            /// не повторяется во всех перезапусках.
            /// </summary>
            public bool TryGetAssignment(string mazeNodeId, int restartIndex, out MazeDoorAssignment assignment)
            {
                if (mazeNodeId != null && _mazePlans.TryGetValue(mazeNodeId, out MazeDoorPlan plan))
                {
                    assignment = plan.ForRestart(restartIndex);
                    return true;
                }

                assignment = null;
                return false;
            }

            public LevelGraph Graph { get; }
            public IReadOnlyDictionary<string, IRoomTemplateDescriptor> Selections { get; }
            public IDeterministicRng Rng { get; }
            public IInteractiveRoomKindRules InteractiveRoomKinds { get; }
            public IReadOnlyDictionary<string, RoomNode> NodesById { get; }

            // Неверные ответы лабиринтов: ставятся только без наложения и не требуются для успеха поиска.
            public HashSet<string> BestEffortNodeIds { get; }

            // Рёбра «лабиринт → заглушка неверного ответа» по узлу лабиринта: под них резервируется место при
            // постановке самой комнаты-лабиринта.
            public IReadOnlyDictionary<string, List<LevelGraphEdge>> CapEdgesByMazeNode { get; }
            public int MaxSearchSteps { get; }
            public PlacementWorkBudget Work { get; }

            /// <summary>
            /// Начальные состояния дверей шаблона — одна неизменяемая копия на шаблон вместо нового словаря на
            /// каждую позу-кандидата. Все потребители <see cref="PlacedRoom.DoorStates"/> только читают и копируют.
            /// </summary>
            public IReadOnlyDictionary<string, DoorState> EmptyDoorStatesFor(IRoomTemplateDescriptor template)
            {
                if (template == null)
                {
                    return NoDoorStates;
                }

                if (!_emptyDoorStatesByTemplate.TryGetValue(template, out IReadOnlyDictionary<string, DoorState> states))
                {
                    states = new ReadOnlyDictionary<string, DoorState>(EmptyDoorStates(template));
                    _emptyDoorStatesByTemplate[template] = states;
                }

                return states;
            }

            private static IReadOnlyDictionary<string, List<LevelGraphEdge>> BuildCapEdges(LevelGraph graph)
            {
                Dictionary<string, List<LevelGraphEdge>> result = new(StringComparer.Ordinal);
                if (graph?.Edges == null)
                {
                    return result;
                }

                for (int i = 0; i < graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = graph.Edges[i];
                    if (edge.BranchRole != EdgeBranchRole.MazeWrongDeadEnd
                        || string.IsNullOrEmpty(edge.FromNodeId)
                        || string.IsNullOrEmpty(edge.ToNodeId))
                    {
                        continue;
                    }

                    if (!result.TryGetValue(edge.FromNodeId, out List<LevelGraphEdge> edges))
                    {
                        edges = new List<LevelGraphEdge>(3);
                        result[edge.FromNodeId] = edges;
                    }

                    edges.Add(edge);
                }

                return result;
            }
        }

        /// <summary>
        /// Счётный бюджет работы одного вызова Place: число оценённых поз-кандидатов. Детерминирован — один и тот же
        /// граф, выбор и сид исчерпывают его в одной и той же точке поиска на любой машине.
        /// </summary>
        private sealed class PlacementWorkBudget
        {
            private readonly int _limit;
            private int _used;

            public PlacementWorkBudget(int limit)
            {
                _limit = Math.Max(1, limit);
            }

            public bool IsExhausted => _used >= _limit;

            public void Consume() => _used++;
        }

        /// <summary>
        /// Назначение дверей комнаты-лабиринта: вход, дверь правильного ответа (основной путь) и двери неверных
        /// ответов. Считается один раз на вызов Place и не зависит от позы комнаты, поэтому все рёбра лабиринта и
        /// все откаты поиска видят одно и то же назначение. Правильная дверь выбирается сидом среди дверей ответа:
        /// раньше она всегда оказывалась «прямо по курсу» (ближайший к 0° мировой угол), и ответ угадывался по
        /// направлению двери.
        /// </summary>
        private sealed class MazeDoorAssignment
        {
            private readonly Dictionary<string, int> _wrongExitIndexByTargetNode;

            public MazeDoorAssignment(
                int entryIndex,
                int correctIndex,
                Dictionary<string, int> wrongExitIndexByTargetNode)
            {
                EntryIndex = entryIndex;
                CorrectIndex = correctIndex;
                _wrongExitIndexByTargetNode = wrongExitIndexByTargetNode;
            }

            public int EntryIndex { get; }
            public int CorrectIndex { get; }

            public int ResolveDoorIndex(LevelGraphEdge edge)
            {
                switch (edge.BranchRole)
                {
                    case EdgeBranchRole.MazeEntry:
                        return EntryIndex;
                    case EdgeBranchRole.MazeCorrectExit:
                        return CorrectIndex;
                    case EdgeBranchRole.MazeWrongDeadEnd:
                        return edge.ToNodeId != null
                               && _wrongExitIndexByTargetNode.TryGetValue(edge.ToNodeId, out int index)
                            ? index
                            : -1;
                    default:
                        return -1;
                }
            }
        }

        /// <summary>
        /// Двери лабиринта, годные под ответы, и выбранное сидом начало круга правильной двери. Назначение на
        /// перезапуск r: правильная — <c>pool[(start + r) % pool.Count]</c>, неверные — остальные двери ответа по порядку
        /// шаблона. Детерминировано для сида; разные перезапуски пробуют разные правильные двери.
        /// </summary>
        private sealed class MazeDoorPlan
        {
            private readonly int _entryIndex;
            private readonly List<int> _lockedExits;
            private readonly List<int> _otherExits;
            private readonly bool _hasCorrectEdge;
            private readonly IReadOnlyList<LevelGraphEdge> _wrongEdges;
            private readonly int _correctRotationStart;
            private readonly MazeDoorAssignment[] _byRestart = new MazeDoorAssignment[MaxRestarts];

            private MazeDoorPlan(
                int entryIndex,
                List<int> lockedExits,
                List<int> otherExits,
                bool hasCorrectEdge,
                IReadOnlyList<LevelGraphEdge> wrongEdges,
                int correctRotationStart)
            {
                _entryIndex = entryIndex;
                _lockedExits = lockedExits;
                _otherExits = otherExits;
                _hasCorrectEdge = hasCorrectEdge;
                _wrongEdges = wrongEdges;
                _correctRotationStart = correctRotationStart;
            }

            public MazeDoorAssignment ForRestart(int restartIndex)
            {
                int slot = Math.Abs(restartIndex) % _byRestart.Length;
                return _byRestart[slot] ??= Build(slot);
            }

            public static MazeDoorPlan Create(
                string mazeNodeId,
                IRoomTemplateDescriptor template,
                LevelGraphEdge? entryEdge,
                LevelGraphEdge? correctEdge,
                IReadOnlyList<LevelGraphEdge> wrongEdges,
                IDeterministicRng rng)
            {
                int entryIndex = entryEdge.HasValue
                    ? ResolveMazeEntryDoorIndex(template, entryEdge.Value, mazeNodeId)
                    : -1;

                // Годные выходы — как раньше: подключаемые, на выход, допустимые для пути правильного ответа (или
                // неверного, если правильного ребра нет), кроме входа. Двери ответа (Locked) идут первыми.
                List<int> lockedExits = new();
                List<int> otherExits = new();
                LevelGraphEdge? orderEdge = correctEdge ?? (wrongEdges != null && wrongEdges.Count > 0
                    ? wrongEdges[0]
                    : (LevelGraphEdge?)null);
                if (orderEdge.HasValue && template?.Doors != null)
                {
                    for (int i = 0; i < template.Doors.Count; i++)
                    {
                        IDoorSocketDescriptor door = template.Doors[i];
                        if (i == entryIndex || door == null || !CanUseDoorForConnection(orderEdge.Value, mazeNodeId, door))
                        {
                            continue;
                        }

                        (IsLockedDoor(door) ? lockedExits : otherExits).Add(i);
                    }
                }

                int rotationStart = 0;
                List<int> pool = lockedExits.Count > 0 ? lockedExits : otherExits;
                if (correctEdge.HasValue && pool.Count > 0)
                {
                    rotationStart = rng.NextInt(0, pool.Count);
                }

                return new MazeDoorPlan(entryIndex, lockedExits, otherExits, correctEdge.HasValue, wrongEdges,
                    rotationStart);
            }

            private MazeDoorAssignment Build(int restartIndex)
            {
                int correctIndex = -1;
                List<int> pool = _lockedExits.Count > 0 ? _lockedExits : _otherExits;
                if (_hasCorrectEdge && pool.Count > 0)
                {
                    correctIndex = pool[(_correctRotationStart + restartIndex) % pool.Count];
                }

                Dictionary<string, int> wrongByTarget = new(StringComparer.Ordinal);
                if (_wrongEdges != null && _wrongEdges.Count > 0)
                {
                    List<int> wrongOrder = new(_lockedExits.Count + _otherExits.Count);
                    AppendExcept(wrongOrder, _lockedExits, correctIndex);
                    AppendExcept(wrongOrder, _otherExits, correctIndex);
                    int next = 0;
                    for (int i = 0; i < _wrongEdges.Count; i++)
                    {
                        string target = _wrongEdges[i].ToNodeId;
                        if (string.IsNullOrEmpty(target) || wrongByTarget.ContainsKey(target))
                        {
                            continue;
                        }

                        wrongByTarget[target] = next < wrongOrder.Count ? wrongOrder[next] : -1;
                        next++;
                    }
                }

                return new MazeDoorAssignment(_entryIndex, correctIndex, wrongByTarget);
            }

            private static void AppendExcept(List<int> target, List<int> source, int excluded)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    if (source[i] != excluded)
                    {
                        target.Add(source[i]);
                    }
                }
            }

            // Вход не зависит от позы: дверь без замка, ближайшая к локальным 180° (см. DoorPreferenceScore).
            private static int ResolveMazeEntryDoorIndex(
                IRoomTemplateDescriptor template,
                LevelGraphEdge entryEdge,
                string mazeNodeId)
            {
                int count = template?.Doors?.Count ?? 0;
                if (count == 0)
                {
                    return -1;
                }

                List<int> ordered = new(count);
                for (int i = 0; i < count; i++)
                {
                    ordered.Add(i);
                }

                DoorIndexComparer comparer = new();
                comparer.Configure(template, entryEdge, mazeNodeId, null);
                ordered.Sort(comparer);
                for (int i = 0; i < ordered.Count; i++)
                {
                    IDoorSocketDescriptor door = template.Doors[ordered[i]];
                    if (door != null && CanUseDoorForConnection(entryEdge, mazeNodeId, door) && !IsLockedDoor(door))
                    {
                        return ordered[i];
                    }
                }

                return -1;
            }
        }

        private static IReadOnlyDictionary<string, MazeDoorPlan> BuildMazePlans(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            IInteractiveRoomKindRules interactiveRoomKinds)
        {
            Dictionary<string, MazeDoorPlan> result = new(StringComparer.Ordinal);
            if (graph?.Nodes == null || graph.Edges == null || selections == null)
            {
                return result;
            }

            // Порядок узлов графа — порядок потребления ГСЧ: детерминирован для сида.
            for (int n = 0; n < graph.Nodes.Count; n++)
            {
                string mazeNodeId = graph.Nodes[n].NodeId;
                if (result.ContainsKey(mazeNodeId)
                    || !selections.TryGetValue(mazeNodeId, out IRoomTemplateDescriptor template)
                    || template?.Doors == null
                    || !InteractiveMissionContentMatcher.HasDoorPerOptionAnswers(interactiveRoomKinds, template))
                {
                    continue;
                }

                LevelGraphEdge? entryEdge = null;
                LevelGraphEdge? correctEdge = null;
                List<LevelGraphEdge> wrongEdges = null;
                for (int i = 0; i < graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = graph.Edges[i];
                    if (edge.BranchRole == EdgeBranchRole.MazeEntry
                        && string.Equals(edge.ToNodeId, mazeNodeId, StringComparison.Ordinal))
                    {
                        entryEdge ??= edge;
                    }
                    else if (edge.BranchRole == EdgeBranchRole.MazeCorrectExit
                             && string.Equals(edge.FromNodeId, mazeNodeId, StringComparison.Ordinal))
                    {
                        correctEdge ??= edge;
                    }
                    else if (edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd
                             && string.Equals(edge.FromNodeId, mazeNodeId, StringComparison.Ordinal))
                    {
                        wrongEdges ??= new List<LevelGraphEdge>(3);
                        wrongEdges.Add(edge);
                    }
                }

                if (!entryEdge.HasValue && !correctEdge.HasValue && wrongEdges == null)
                {
                    continue;
                }

                result[mazeNodeId] =
                    MazeDoorPlan.Create(mazeNodeId, template, entryEdge, correctEdge, wrongEdges, rng);
            }

            return result;
        }

        private sealed class PlacementState
        {
            private readonly PlacementRun _run;
            private readonly int _restartIndex;
            private readonly LevelGraph _graph;
            private readonly IReadOnlyDictionary<string, IRoomTemplateDescriptor> _selections;
            private readonly IDeterministicRng _rng;
            private readonly CancellationToken _ct;
            private readonly LevelGenerationDebugCounters _debugCounters;
            private readonly IRoomPlacementBudget _budget;

            // Часы кооперативной уступки: сколько прошло с прошлой отдачи управления хосту. На исход не влияют.
            private readonly System.Diagnostics.Stopwatch _yieldClock = System.Diagnostics.Stopwatch.StartNew();

            private Dictionary<string, PlacedRoom> _placedByNodeId = new(StringComparer.Ordinal);
            private Dictionary<string, HashSet<string>> _usedSocketsByNodeId = new(StringComparer.Ordinal);

            // Wall OBBs of placed rooms only depend on their (immutable) pose and template, so they are
            // transformed once when a room is placed and reused across every candidate overlap test.
            // This cache MUST stay consistent with _placedByNodeId across backtracking: a node placed in an
            // abandoned branch (pose P2) is not re-placed when the search reverts to the snapshot pose (P1),
            // so without rollback GetCollision would hand back P2 geometry for a P1 pose and accept
            // overlapping layouts or reject valid ones. It is therefore snapshotted and restored alongside
            // _placedByNodeId. The entries (RoomCollision) are immutable value structs holding immutable wall
            // data and are only ever replaced wholesale, so a shallow Dictionary copy is sufficient.
            private Dictionary<string, RoomCollision> _collisionByNodeId = new(StringComparer.Ordinal);
            private List<DoorConnection> _connections = new();
            private List<string> _placedOrder = new();
            private Dictionary<string, PlacedRoom> _bestPlacedByNodeId = new(StringComparer.Ordinal);
            private List<DoorConnection> _bestConnections = new();
            private readonly List<SocketUseMutation> _socketUseMutations = new();
            private readonly List<CollisionMutation> _collisionMutations = new();
            private readonly Stack<List<Candidate>> _candidateListPool = new();
            private readonly Stack<List<int>> _doorIndexListPool = new();
            private readonly DoorIndexComparer _doorIndexComparer = new();

            // Переиспользуемый буфер стен позы-кандидата (проверка пересечений без нового массива на позу).
            private RoomWallCollision.WallObb[] _candidateWallBuffer;
            private int _searchSteps;
            private int _backtracks;

            public PlacementState(
                PlacementRun run,
                int restartIndex,
                CancellationToken ct,
                LevelGenerationDebugCounters debugCounters,
                IRoomPlacementBudget budget = null)
            {
                _run = run;
                _restartIndex = restartIndex;
                _graph = run.Graph;
                _selections = run.Selections;
                _rng = run.Rng;
                _ct = ct;
                _debugCounters = debugCounters;
                _budget = budget;
                PlaceStart();
            }

            public bool Search()
            {
                _ct.ThrowIfCancellationRequested();
                if (AllRequiredNodesPlaced())
                {
                    return true;
                }

                if (++_searchSteps > _run.MaxSearchSteps || _run.Work.IsExhausted)
                {
                    return false;
                }

                if (!TrySelectFrontier(out List<Candidate> candidates) || candidates.Count == 0)
                {
                    return false;
                }

                ShuffleEqualPreferenceBuckets(candidates);
                for (int i = 0; i < candidates.Count; i++)
                {
                    // Вариант 0 — с резервом заглушек неверных ответов; если поддерево под ним провалилось, а резерв
                    // что-то поставил, тот же кандидат пробуется вариантом 1 — без резерва (заглушки в конце, только
                    // без наложения). Заглушка — best-effort и не может заблокировать обязательные комнаты.
                    for (int variant = 0; variant < 2; variant++)
                    {
                        _ct.ThrowIfCancellationRequested();
                        bool reserveCaps = variant == 0;
                        Snapshot snapshot = CreateSnapshot();
                        int reservedCaps = Apply(candidates[i], reserveCaps);
                        if (Search())
                        {
                            ReleaseCandidateList(candidates);
                            return true;
                        }

                        _backtracks++;
                        _debugCounters?.IncrementBacktracks();
                        if (_backtracks > MaxBacktracksPerRestart
                            || _run.Work.IsExhausted
                            || (_backtracks % CancellationCheckBacktrackInterval == 0 && ShouldAbortSearch()))
                        {
                            ReleaseCandidateList(candidates);
                            return false;
                        }

                        Restore(snapshot);
                        if (!reserveCaps || reservedCaps == 0)
                        {
                            break;
                        }
                    }
                }

                ReleaseCandidateList(candidates);
                return false;
            }

            /// <summary>
            /// Отдаёт управление хосту, только если с прошлой уступки прошёл кадровый бюджет.
            /// <para>
            /// Отмена и бюджет всего прогона проверяются на КАЖДОМ чекпойнте, даже когда уступки не происходит:
            /// иначе более редкие уступки сделали бы более редкой и реакцию на общий таймаут генерации.
            /// </para>
            /// </summary>
            private async Task YieldIfDueAsync()
            {
                _ct.ThrowIfCancellationRequested();
                _budget?.ThrowIfExpired();
                if (_yieldClock.Elapsed.TotalMilliseconds < CooperativeYieldIntervalMilliseconds)
                {
                    return;
                }

                await YieldAsync(_ct, _budget);
                _yieldClock.Restart();
            }

            public async Task<bool> SearchAsync()
            {
                _ct.ThrowIfCancellationRequested();
                if (AllRequiredNodesPlaced())
                {
                    return true;
                }

                if (++_searchSteps > _run.MaxSearchSteps || _run.Work.IsExhausted)
                {
                    return false;
                }

                if (_searchSteps % AsyncYieldCheckStepInterval == 0)
                {
                    await YieldIfDueAsync();
                }

                if (!TrySelectFrontier(out List<Candidate> candidates) || candidates.Count == 0)
                {
                    return false;
                }

                ShuffleEqualPreferenceBuckets(candidates);
                for (int i = 0; i < candidates.Count; i++)
                {
                    // Те же два варианта, что в Search: с резервом заглушек, затем без него.
                    for (int variant = 0; variant < 2; variant++)
                    {
                        _ct.ThrowIfCancellationRequested();
                        bool reserveCaps = variant == 0;
                        Snapshot snapshot = CreateSnapshot();
                        int reservedCaps = Apply(candidates[i], reserveCaps);
                        if (await SearchAsync())
                        {
                            ReleaseCandidateList(candidates);
                            return true;
                        }

                        _backtracks++;
                        _debugCounters?.IncrementBacktracks();
                        if (_backtracks > MaxBacktracksPerRestart
                            || _run.Work.IsExhausted
                            || (_backtracks % CancellationCheckBacktrackInterval == 0 && ShouldAbortSearch()))
                        {
                            ReleaseCandidateList(candidates);
                            return false;
                        }

                        if (_backtracks % AsyncYieldCheckStepInterval == 0)
                        {
                            await YieldIfDueAsync();
                        }

                        Restore(snapshot);
                        if (!reserveCaps || reservedCaps == 0)
                        {
                            break;
                        }
                    }
                }

                ReleaseCandidateList(candidates);
                return false;
            }

            public bool TryPlaceMainThenSideLeaves()
            {
                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (!edge.IsMainPath)
                    {
                        continue;
                    }

                    if (!TryApplyFrontierEdge(edge))
                    {
                        return false;
                    }
                }

                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (edge.IsMainPath || edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd)
                    {
                        continue;
                    }

                    if (!TryApplyFrontierEdge(edge))
                    {
                        return false;
                    }
                }

                return AllRequiredNodesPlaced();
            }

            public async Task<bool> TryPlaceMainThenSideLeavesAsync()
            {
                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    if (i > 0 && i % AsyncYieldGreedyEdgeInterval == 0)
                    {
                        await YieldIfDueAsync();
                    }

                    LevelGraphEdge edge = _graph.Edges[i];
                    if (!edge.IsMainPath)
                    {
                        continue;
                    }

                    if (!TryApplyFrontierEdge(edge))
                    {
                        return false;
                    }
                }

                await YieldAsync(_ct, _budget);

                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    if (i > 0 && i % AsyncYieldGreedyEdgeInterval == 0)
                    {
                        await YieldIfDueAsync();
                    }

                    LevelGraphEdge edge = _graph.Edges[i];
                    if (edge.IsMainPath || edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd)
                    {
                        continue;
                    }

                    if (!TryApplyFrontierEdge(edge))
                    {
                        return false;
                    }
                }

                return AllRequiredNodesPlaced();
            }

            private bool TryApplyFrontierEdge(LevelGraphEdge edge)
            {
                bool fromPlaced = _placedByNodeId.ContainsKey(edge.FromNodeId);
                bool toPlaced = _placedByNodeId.ContainsKey(edge.ToNodeId);
                if (fromPlaced && toPlaced)
                {
                    return true;
                }

                if (!fromPlaced && !toPlaced)
                {
                    return false;
                }

                string placedNodeId = fromPlaced ? edge.FromNodeId : edge.ToNodeId;
                string unplacedNodeId = fromPlaced ? edge.ToNodeId : edge.FromNodeId;
                List<Candidate> candidates = BuildCandidates(edge, placedNodeId, unplacedNodeId);
                if (candidates.Count == 0)
                {
                    _debugCounters?.IncrementDomainEmptyRejects();
                    ReleaseCandidateList(candidates);
                    return false;
                }

                Apply(candidates[0], true);
                ReleaseCandidateList(candidates);
                return true;
            }

            private bool ShouldAbortSearch()
            {
                try
                {
                    _ct.ThrowIfCancellationRequested();
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
            }

            public LevelLayout ToLayout()
            {
                PlaceOptionalMazeDeadEnds();
                ReportUnplacedMazeCaps();
                return BuildLayout(_placedByNodeId, _connections);
            }

            // Для итоговой раскладки: какая заглушка не поместилась и у какой двери (назначение этого перезапуска).
            private void ReportUnplacedMazeCaps()
            {
                RoomPlacementReport report = _run.Report;
                if (report == null || _run.BestEffortNodeIds.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (edge.BranchRole != EdgeBranchRole.MazeWrongDeadEnd
                        || !_placedByNodeId.ContainsKey(edge.FromNodeId)
                        || _placedByNodeId.ContainsKey(edge.ToNodeId))
                    {
                        continue;
                    }

                    string socketId = null;
                    if (_run.TryGetAssignment(edge.FromNodeId, _restartIndex, out MazeDoorAssignment assignment)
                        && _selections.TryGetValue(edge.FromNodeId, out IRoomTemplateDescriptor mazeTemplate)
                        && mazeTemplate?.Doors != null)
                    {
                        int doorIndex = assignment.ResolveDoorIndex(edge);
                        if (doorIndex >= 0 && doorIndex < mazeTemplate.Doors.Count)
                        {
                            socketId = mazeTemplate.Doors[doorIndex]?.SocketId;
                        }
                    }

                    report.AddUnplacedMazeCap(edge.ToNodeId, edge.FromNodeId, socketId);
                }
            }

            // Completion criterion: every REQUIRED node placed. Maze wrong-answer dead-ends are best-effort, so a
            // layout that places the whole main path + correct tunnels is a success even if some dead-end caps
            // could not fit without overlap (MissionGenerator removes them from the graph and their doors are
            // sealed). For non-maze missions the best-effort set is empty, so this is "all nodes placed".
            private bool AllRequiredNodesPlaced()
            {
                if (_run.BestEffortNodeIds.Count == 0)
                {
                    return _placedByNodeId.Count == _graph.Nodes.Count;
                }

                for (int i = 0; i < _graph.Nodes.Count; i++)
                {
                    string nodeId = _graph.Nodes[i].NodeId;
                    if (!_run.BestEffortNodeIds.Contains(nodeId) && !_placedByNodeId.ContainsKey(nodeId))
                    {
                        return false;
                    }
                }

                return true;
            }

            // Последний шанс для заглушек, которым не нашлось места при постановке их лабиринта. Ставятся ТОЛЬКО без
            // наложения: раньше здесь заглушку ставили принудительно поверх соседей, и раскладка с пересечением
            // уходила в игру (валидатор понижал её до предупреждения). Неподошедшая заглушка остаётся неразмещённой —
            // генератор убирает её из графа, дверь запечатывается, в LastWarnings уходит предупреждение.
            private void PlaceOptionalMazeDeadEnds()
            {
                if (_run.BestEffortNodeIds.Count == 0)
                {
                    return;
                }

                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (edge.BranchRole != EdgeBranchRole.MazeWrongDeadEnd)
                    {
                        continue;
                    }

                    if (!_placedByNodeId.ContainsKey(edge.FromNodeId) || _placedByNodeId.ContainsKey(edge.ToNodeId))
                    {
                        continue;
                    }

                    List<Candidate> candidates = BuildCandidates(edge, edge.FromNodeId, edge.ToNodeId);
                    if (candidates.Count > 0)
                    {
                        ApplyCandidate(candidates[0]);
                    }

                    ReleaseCandidateList(candidates);
                }
            }

            public LevelLayout ToBestLayout() => BuildLayout(_bestPlacedByNodeId, _bestConnections);

            private LevelLayout BuildLayout(
                IReadOnlyDictionary<string, PlacedRoom> roomsByNodeId,
                IReadOnlyList<DoorConnection> connections)
            {
                List<PlacedRoom> rooms = new(_placedOrder.Count);
                List<string> exits = new();
                for (int i = 0; i < _graph.Nodes.Count; i++)
                {
                    RoomNode node = _graph.Nodes[i];
                    if (!roomsByNodeId.TryGetValue(node.NodeId, out PlacedRoom room))
                    {
                        continue;
                    }

                    rooms.Add(room);
                    if (node.Kind == RoomKind.Exit)
                    {
                        exits.Add(node.NodeId);
                    }
                }

                DoorConnection[] connectionArray = new DoorConnection[connections.Count];
                for (int i = 0; i < connections.Count; i++)
                {
                    connectionArray[i] = connections[i];
                }

                return new LevelLayout(rooms, connectionArray, _graph.Nodes[0].NodeId, exits);
            }

            private void PlaceStart()
            {
                RoomNode startNode = _graph.Nodes[0];
                _selections.TryGetValue(startNode.NodeId, out IRoomTemplateDescriptor template);
                PlacedRoom start = new(
                    startNode.NodeId,
                    template?.RoomId ?? "MISSING_START",
                    new GridPose(0f, 0f, 0),
                    _run.EmptyDoorStatesFor(template),
                    new RoomWorldPose(0f, 0f, 0f, 0f, 0f, 0f, 1f),
                    template?.Footprint,
                    RoomFootprintShapeResolver.ShapeFor(template),
                    startNode.ContentId);

                _placedByNodeId[startNode.NodeId] = start;
                CacheCollision(start, template);
                _usedSocketsByNodeId[startNode.NodeId] = new HashSet<string>(StringComparer.Ordinal);
                _placedOrder.Add(startNode.NodeId);
                LevelGenerationPlacementTrace.Accepted(
                    startNode.NodeId,
                    start.RoomTemplateId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    start);
                CaptureBestIfNeeded();
            }

            private bool TrySelectFrontier(out List<Candidate> selectedCandidates)
            {
                selectedCandidates = null;
                int selectedPriority = int.MaxValue;
                int selectedCount = int.MaxValue;

                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    // Maze wrong-answer dead-ends are not expanded by the search: their space is reserved when the
                    // maze room is placed (Apply → ReserveMazeCaps) and leftovers are tried once more in ToLayout().
                    // Skipping them keeps an unfittable cap from failing the whole frontier.
                    if (edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd)
                    {
                        continue;
                    }

                    bool fromPlaced = _placedByNodeId.ContainsKey(edge.FromNodeId);
                    bool toPlaced = _placedByNodeId.ContainsKey(edge.ToNodeId);
                    if (fromPlaced == toPlaced)
                    {
                        continue;
                    }

                    string placedNodeId = fromPlaced ? edge.FromNodeId : edge.ToNodeId;
                    string unplacedNodeId = fromPlaced ? edge.ToNodeId : edge.FromNodeId;
                    List<Candidate> candidates = BuildCandidates(edge, placedNodeId, unplacedNodeId);
                    if (candidates.Count == 0)
                    {
                        _debugCounters?.IncrementDomainEmptyRejects();
                        ReleaseCandidateList(candidates);
                        ReleaseCandidateList(selectedCandidates);
                        selectedCandidates = null;
                        return false;
                    }

                    int priority = edge.IsMainPath
                        ? 0
                        : HasUnplacedMainEdge(placedNodeId)
                            ? 2
                            : 1;
                    if (priority < selectedPriority
                        || (priority == selectedPriority && candidates.Count < selectedCount))
                    {
                        ReleaseCandidateList(selectedCandidates);
                        selectedCandidates = candidates;
                        selectedPriority = priority;
                        selectedCount = candidates.Count;
                        if (selectedPriority == 0 && selectedCount == 1)
                        {
                            break;
                        }
                    }
                    else
                    {
                        ReleaseCandidateList(candidates);
                    }
                }

                return selectedCandidates != null;
            }

            private bool HasUnplacedMainEdge(string placedNodeId)
            {
                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (!edge.IsMainPath)
                    {
                        continue;
                    }

                    bool fromMatches = string.Equals(edge.FromNodeId, placedNodeId, StringComparison.Ordinal);
                    bool toMatches = string.Equals(edge.ToNodeId, placedNodeId, StringComparison.Ordinal);
                    if (!fromMatches && !toMatches)
                    {
                        continue;
                    }

                    string otherNodeId = fromMatches ? edge.ToNodeId : edge.FromNodeId;
                    if (!_placedByNodeId.ContainsKey(otherNodeId))
                    {
                        return true;
                    }
                }

                return false;
            }

            private List<Candidate> BuildCandidates(
                LevelGraphEdge edge,
                string placedNodeId,
                string unplacedNodeId)
            {
                // Candidate construction (door pairing + overlap tests) is the heaviest unit of work in the
                // search, so honour cancellation here to keep the run-wide time budget from overshooting by
                // more than a single frontier evaluation.
                _ct.ThrowIfCancellationRequested();
                List<Candidate> result = RentCandidateList();
                if (_run.Work.IsExhausted)
                {
                    return result;
                }

                if (!_placedByNodeId.TryGetValue(placedNodeId, out PlacedRoom placedRoom))
                {
                    return result;
                }

                if (!_selections.TryGetValue(placedNodeId, out IRoomTemplateDescriptor placedTemplate))
                {
                    return result;
                }

                if (!_selections.TryGetValue(unplacedNodeId, out IRoomTemplateDescriptor unplacedTemplate))
                {
                    return result;
                }

                if (placedTemplate?.Doors == null || unplacedTemplate?.Doors == null)
                {
                    return result;
                }

                bool traceEnabled = LevelGenerationPlacementTrace.Enabled;
                bool reservesMazeCaps = _run.CapEdgesByMazeNode.ContainsKey(unplacedNodeId);
                float placedYaw = YawDegrees(placedRoom);
                List<int> placedDoorIndices = RentDoorIndexList();
                List<int> unplacedDoorIndices = RentDoorIndexList();
                try
                {
                    FillOrderedDoorIndices(placedTemplate, edge, placedNodeId, placedDoorIndices, placedYaw);
                    FillOrderedDoorIndices(unplacedTemplate, edge, unplacedNodeId, unplacedDoorIndices);

                    for (int p = 0; p < placedDoorIndices.Count; p++)
                    {
                        IDoorSocketDescriptor placedDoor = placedTemplate.Doors[placedDoorIndices[p]];
                        if (!CanUsePlacedDoorForCandidate(edge, placedDoor))
                        {
                            if (traceEnabled)
                            {
                                LevelGenerationPlacementTrace.Rejected(
                                    unplacedNodeId,
                                    unplacedTemplate.RoomId,
                                    $"placed door is not an exit/both socket: {placedNodeId}.{placedDoor.SocketId}");
                            }

                            continue;
                        }

                        if (IsSocketUsed(placedNodeId, placedDoor.SocketId))
                        {
                            if (traceEnabled)
                            {
                                LevelGenerationPlacementTrace.Rejected(
                                    unplacedNodeId,
                                    unplacedTemplate.RoomId,
                                    $"placed door already used: {placedNodeId}.{placedDoor.SocketId}");
                            }

                            continue;
                        }

                        (float x, float y, float z) placedDoorPoint = DoorWorldPointMeters(placedRoom, placedDoor);
                        float placedDoorYaw = NormalizeYaw(placedYaw + placedDoor.LocalYawDegrees);

                        for (int u = 0; u < unplacedDoorIndices.Count; u++)
                        {
                            IDoorSocketDescriptor unplacedDoor = unplacedTemplate.Doors[unplacedDoorIndices[u]];
                            if (!CanUseUnplacedDoorForCandidate(edge, unplacedDoor))
                            {
                                continue;
                            }

                            // Дешёвые отказы (замок на входе, размер, рама) не зависят от позы, поэтому без трассировки
                            // поза для них не строится вовсе. С трассировкой порядок шагов прежний: поза, TryCandidate,
                            // затем причина отказа.
                            bool lockedIncoming = WouldUseLockedIncomingDoor(edge, placedNodeId, placedDoor,
                                unplacedNodeId, unplacedDoor);
                            bool sizeMismatch = placedDoor.Size != unplacedDoor.Size;
                            bool frameMismatch = !DoorFramesCompatible(placedDoor, unplacedDoor);
                            if (!traceEnabled && (lockedIncoming || sizeMismatch || frameMismatch))
                            {
                                continue;
                            }

                            PlacedRoom room = CreateAttachedRoom(
                                unplacedNodeId,
                                unplacedTemplate,
                                placedDoorPoint,
                                placedDoorYaw,
                                unplacedDoor);
                            LevelGenerationPlacementTrace.TryCandidate(
                                unplacedNodeId,
                                unplacedTemplate.RoomId,
                                placedNodeId,
                                placedDoor.SocketId,
                                unplacedDoor.SocketId,
                                room);

                            if (lockedIncoming)
                            {
                                LevelGenerationPlacementTrace.Rejected(
                                    unplacedNodeId,
                                    unplacedTemplate.RoomId,
                                    $"locked incoming door: {unplacedDoor.SocketId}",
                                    room,
                                    placedNodeId,
                                    placedDoor.SocketId,
                                    unplacedDoor.SocketId);
                                continue;
                            }

                            if (sizeMismatch)
                            {
                                LevelGenerationPlacementTrace.Rejected(
                                    unplacedNodeId,
                                    unplacedTemplate.RoomId,
                                    $"door size mismatch: {placedDoor.Size} vs {unplacedDoor.Size}",
                                    room,
                                    placedNodeId,
                                    placedDoor.SocketId,
                                    unplacedDoor.SocketId);
                                continue;
                            }

                            if (frameMismatch)
                            {
                                LevelGenerationPlacementTrace.Rejected(
                                    unplacedNodeId,
                                    unplacedTemplate.RoomId,
                                    $"door frame mismatch: {placedDoor.FrameWidthMeters():0.###}m vs {unplacedDoor.FrameWidthMeters():0.###}m",
                                    room,
                                    placedNodeId,
                                    placedDoor.SocketId,
                                    unplacedDoor.SocketId);
                                continue;
                            }

                            ConsumeWork();
                            if (OverlapsAny(
                                    room,
                                    unplacedTemplate,
                                    placedNodeId,
                                    (placedDoorPoint.x, placedDoorPoint.z)))
                            {
                                _debugCounters?.IncrementOverlapRejects();
                                if (traceEnabled)
                                {
                                    LevelGenerationPlacementTrace.Rejected(
                                        unplacedNodeId,
                                        unplacedTemplate.RoomId,
                                        "overlap with placed room/walls",
                                        room,
                                        placedNodeId,
                                        placedDoor.SocketId,
                                        unplacedDoor.SocketId);
                                }

                                continue;
                            }

                            // Комната-лабиринт: позы, у которых не помещаются заглушки неверных ответов, — в конец.
                            int missingCaps = reservesMazeCaps ? CountUnreservableMazeCaps(room, unplacedTemplate) : 0;
                            _debugCounters?.IncrementCandidatesBuilt();
                            result.Add(new Candidate(
                                room,
                                BuildConnection(edge, placedNodeId, placedDoor.SocketId, unplacedNodeId,
                                    unplacedDoor.SocketId),
                                placedNodeId,
                                placedDoor.SocketId,
                                unplacedNodeId,
                                unplacedDoor.SocketId,
                                CandidatePreferenceScore(room, placedNodeId, p, u)
                                + Math.Min(missingCaps, MaxPenalizedMazeCapMisses) * MazeCapMissPenalty));
                        }
                    }
                }
                finally
                {
                    ReleaseDoorIndexList(placedDoorIndices);
                    ReleaseDoorIndexList(unplacedDoorIndices);
                }

                if (result.Count > 1)
                {
                    result.Sort(CompareCandidatesByPreference);

                    if (result.Count > MaxCandidatesPerFrontier)
                    {
                        result.RemoveRange(MaxCandidatesPerFrontier, result.Count - MaxCandidatesPerFrontier);
                    }
                }

                return result;
            }

            /// <summary>
            /// Сколько заглушек неверных ответов НЕ поместится, если поставить комнату-лабиринт в позу
            /// <paramref name="mazeRoom"/>: каждая заглушка проверяется у своей назначенной двери против уже
            /// поставленных комнат, самого лабиринта (со швом у двери) и уже зарезервированных заглушек этой позы.
            /// ГСЧ не потребляется — проверка не меняет исход поиска для поз, где всё помещается.
            /// </summary>
            private int CountUnreservableMazeCaps(PlacedRoom mazeRoom, IRoomTemplateDescriptor mazeTemplate)
            {
                if (!_run.CapEdgesByMazeNode.TryGetValue(mazeRoom.NodeId, out List<LevelGraphEdge> capEdges)
                    || capEdges.Count == 0)
                {
                    return 0;
                }

                RoomCollision mazeCollision = BuildCollision(mazeRoom, mazeTemplate);
                List<VirtualRoom> reserved = null;
                int missing = 0;
                for (int i = 0; i < capEdges.Count; i++)
                {
                    LevelGraphEdge capEdge = capEdges[i];
                    if (_placedByNodeId.ContainsKey(capEdge.ToNodeId))
                    {
                        continue;
                    }

                    if (TryFindVirtualCap(capEdge, mazeRoom, mazeTemplate, mazeCollision, reserved,
                            out VirtualRoom cap))
                    {
                        reserved ??= new List<VirtualRoom>(capEdges.Count);
                        reserved.Add(cap);
                    }
                    else
                    {
                        missing++;
                    }
                }

                return missing;
            }

            private bool TryFindVirtualCap(
                LevelGraphEdge capEdge,
                PlacedRoom mazeRoom,
                IRoomTemplateDescriptor mazeTemplate,
                RoomCollision mazeCollision,
                List<VirtualRoom> reserved,
                out VirtualRoom cap)
            {
                cap = default;
                string mazeNodeId = mazeRoom.NodeId;
                string capNodeId = capEdge.ToNodeId;
                if (mazeTemplate?.Doors == null
                    || !_selections.TryGetValue(capNodeId, out IRoomTemplateDescriptor capTemplate)
                    || capTemplate?.Doors == null)
                {
                    return false;
                }

                float mazeYaw = YawDegrees(mazeRoom);
                List<int> mazeDoorIndices = RentDoorIndexList();
                List<int> capDoorIndices = RentDoorIndexList();
                try
                {
                    FillOrderedDoorIndices(mazeTemplate, capEdge, mazeNodeId, mazeDoorIndices, mazeYaw);
                    FillOrderedDoorIndices(capTemplate, capEdge, capNodeId, capDoorIndices);
                    for (int p = 0; p < mazeDoorIndices.Count; p++)
                    {
                        IDoorSocketDescriptor mazeDoor = mazeTemplate.Doors[mazeDoorIndices[p]];
                        if (!CanUsePlacedDoorForCandidate(capEdge, mazeDoor))
                        {
                            continue;
                        }

                        (float x, float y, float z) mazeDoorPoint = DoorWorldPointMeters(mazeRoom, mazeDoor);
                        float mazeDoorYaw = NormalizeYaw(mazeYaw + mazeDoor.LocalYawDegrees);
                        (float x, float z) seam = (mazeDoorPoint.x, mazeDoorPoint.z);
                        for (int u = 0; u < capDoorIndices.Count; u++)
                        {
                            IDoorSocketDescriptor capDoor = capTemplate.Doors[capDoorIndices[u]];
                            if (!CanUseUnplacedDoorForCandidate(capEdge, capDoor)
                                || WouldUseLockedIncomingDoor(capEdge, mazeNodeId, mazeDoor, capNodeId, capDoor)
                                || mazeDoor.Size != capDoor.Size
                                || !DoorFramesCompatible(mazeDoor, capDoor))
                            {
                                continue;
                            }

                            PlacedRoom capRoom = CreateAttachedRoom(capNodeId, capTemplate, mazeDoorPoint, mazeDoorYaw,
                                capDoor);
                            ConsumeWork();

                            // Против уже поставленных комнат: шва с ними нет — заглушка касается только лабиринта,
                            // а его среди поставленных ещё нет.
                            if (OverlapsAny(capRoom, capTemplate, mazeNodeId, seam))
                            {
                                continue;
                            }

                            RoomCollision capCollision = BuildCollision(capRoom, capTemplate);
                            if (OverlapsRoom(capRoom, capTemplate, capCollision, mazeRoom, mazeTemplate, mazeCollision,
                                    seam)
                                || OverlapsAnyReserved(capRoom, capTemplate, capCollision, reserved))
                            {
                                continue;
                            }

                            cap = new VirtualRoom(capRoom, capTemplate, capCollision);
                            return true;
                        }
                    }
                }
                finally
                {
                    ReleaseDoorIndexList(mazeDoorIndices);
                    ReleaseDoorIndexList(capDoorIndices);
                }

                return false;
            }

            private static bool OverlapsAnyReserved(
                PlacedRoom capRoom,
                IRoomTemplateDescriptor capTemplate,
                RoomCollision capCollision,
                List<VirtualRoom> reserved)
            {
                if (reserved == null)
                {
                    return false;
                }

                for (int i = 0; i < reserved.Count; i++)
                {
                    VirtualRoom other = reserved[i];
                    if (OverlapsRoom(capRoom, capTemplate, capCollision, other.Room, other.Template, other.Collision,
                            null))
                    {
                        return true;
                    }
                }

                return false;
            }

            // Та же проверка пары, что в OverlapsAny, для комнат, которых ещё нет среди поставленных.
            private static bool OverlapsRoom(
                PlacedRoom a,
                IRoomTemplateDescriptor aTemplate,
                RoomCollision aCollision,
                PlacedRoom b,
                IRoomTemplateDescriptor bTemplate,
                RoomCollision bCollision,
                (float x, float z)? seam)
            {
                if (aCollision.HasWalls && bCollision.HasWalls)
                {
                    if (!aCollision.Bounds.Overlaps(bCollision.Bounds, BroadphaseMarginMeters))
                    {
                        return false;
                    }

                    if (RoomWallCollision.TryOverlaps(aCollision.Walls, bCollision.Walls, seam, out bool wallOverlaps))
                    {
                        return wallOverlaps;
                    }
                }

                return RoomPlacer.Overlaps(a.Pose, aTemplate, b.Pose, bTemplate);
            }

            private static int CompareCandidatesByPreference(Candidate a, Candidate b)
            {
                int scoreCompare = a.PreferenceScore.CompareTo(b.PreferenceScore);
                if (scoreCompare != 0)
                {
                    return scoreCompare;
                }

                int nodeCompare = string.CompareOrdinal(a.UnplacedNodeId, b.UnplacedNodeId);
                if (nodeCompare != 0)
                {
                    return nodeCompare;
                }

                return string.CompareOrdinal(a.UnplacedSocketId, b.UnplacedSocketId);
            }

            private int CandidatePreferenceScore(
                PlacedRoom room,
                string connectedNodeId,
                int placedDoorOrder,
                int unplacedDoorOrder)
            {
                (float x, float z) candidatePosition = RoomWorldPositionMeters(room);
                float nearestDistanceSquared = float.MaxValue;
                int nearbyRooms = 0;

                foreach (KeyValuePair<string, PlacedRoom> kv in _placedByNodeId)
                {
                    if (string.Equals(kv.Key, connectedNodeId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    (float x, float z) placedPosition = RoomWorldPositionMeters(kv.Value);
                    float dx = candidatePosition.x - placedPosition.x;
                    float dz = candidatePosition.z - placedPosition.z;
                    float distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared < nearestDistanceSquared)
                    {
                        nearestDistanceSquared = distanceSquared;
                    }

                    if (distanceSquared < 900f)
                    {
                        nearbyRooms++;
                    }
                }

                if (nearestDistanceSquared == float.MaxValue)
                {
                    return placedDoorOrder * 1000000
                           + unplacedDoorOrder * 10000
                           + _rng.NextInt(0, 128);
                }

                int crowdingPenalty = (int)Math.Round(100000f / Math.Max(25f, nearestDistanceSquared));
                int randomTieBreaker = _rng.NextInt(0, 128);
                return placedDoorOrder * 1000000
                       + unplacedDoorOrder * 10000
                       + crowdingPenalty
                       + nearbyRooms * 50
                       + randomTieBreaker;
            }

            private void ShuffleEqualPreferenceBuckets(List<Candidate> candidates)
            {
                if (candidates == null || candidates.Count <= 1)
                {
                    return;
                }

                int bucketStart = 0;
                while (bucketStart < candidates.Count)
                {
                    int bucketEnd = bucketStart + 1;
                    int preference = candidates[bucketStart].PreferenceScore;
                    while (bucketEnd < candidates.Count && candidates[bucketEnd].PreferenceScore == preference)
                    {
                        bucketEnd++;
                    }

                    for (int i = bucketEnd - 1; i > bucketStart; i--)
                    {
                        int swapIndex = _rng.NextInt(bucketStart, i + 1);
                        (candidates[i], candidates[swapIndex]) = (candidates[swapIndex], candidates[i]);
                    }

                    bucketStart = bucketEnd;
                }
            }

            // Поза комнаты, пристыкованной дверью unplacedDoor к двери с мировой точкой placedDoorPoint и углом
            // placedDoorYaw: двери смотрят навстречу, точки совпадают.
            private PlacedRoom CreateAttachedRoom(
                string nodeId,
                IRoomTemplateDescriptor template,
                (float x, float y, float z) placedDoorPoint,
                float placedDoorYaw,
                IDoorSocketDescriptor unplacedDoor)
            {
                float unplacedDoorYaw = NormalizeYaw(unplacedDoor.LocalYawDegrees);
                float unplacedYaw = NormalizeYaw(placedDoorYaw + 180f - unplacedDoorYaw);
                (float x, float z) unplacedLocalDoor = RotateMeters(
                    (unplacedDoor.ConnectionLocalXMeters(), unplacedDoor.ConnectionLocalZMeters()),
                    unplacedYaw);
                (float x, float z) origin = (
                    x: placedDoorPoint.x - unplacedLocalDoor.x,
                    z: placedDoorPoint.z - unplacedLocalDoor.z);
                float originY = placedDoorPoint.y - unplacedDoor.ConnectionLocalYMeters();
                return CreatePlacedRoom(nodeId, template, origin, originY, unplacedYaw);
            }

            private PlacedRoom CreatePlacedRoom(
                string nodeId,
                IRoomTemplateDescriptor template,
                (float x, float z) origin,
                float originY,
                float yawDegrees)
            {
                _run.NodesById.TryGetValue(nodeId, out RoomNode node);
                return new PlacedRoom(
                    nodeId,
                    template.RoomId,
                    new GridPose(origin.x / DoorSnapMeters, origin.z / DoorSnapMeters, RoundYaw(yawDegrees)),
                    _run.EmptyDoorStatesFor(template),
                    CreateWorldPose(origin.x, originY, origin.z, yawDegrees),
                    template.Footprint,
                    RoomFootprintShapeResolver.ShapeFor(template),
                    node?.ContentId);
            }

            private bool OverlapsAny(
                PlacedRoom candidate,
                IRoomTemplateDescriptor candidateTemplate,
                string connectedNodeId,
                (float x, float z) connectedSeamPointMeters)
            {
                // Стены кандидата — в переиспользуемый буфер: поза живёт только на время этой проверки.
                bool hasCandidateWalls = RoomWallCollision.TryTransformInto(
                    candidate,
                    candidateTemplate,
                    ref _candidateWallBuffer,
                    out RoomWallCollision.TransformedWallSegments candidateWalls);
                RoomWallCollision.Aabb candidateBounds = hasCandidateWalls
                    ? RoomWallCollision.BoundsOf(candidateWalls)
                    : RoomWallCollision.Aabb.Empty;

                foreach (KeyValuePair<string, PlacedRoom> kv in _placedByNodeId)
                {
                    PlacedRoom placed = kv.Value;
                    bool isSeamNeighbor = string.Equals(placed.NodeId, connectedNodeId, StringComparison.Ordinal);
                    RoomCollision placedCollision = GetCollision(placed);

                    if (hasCandidateWalls && placedCollision.HasWalls)
                    {
                        // Broadphase: rooms whose wall bounds cannot touch can never overlap, so skip the
                        // expensive SAT test. The result is identical to running it, which keeps placement
                        // deterministic. Seam neighbours share a door, so their bounds always overlap and the
                        // detailed seam-aware test still runs for them.
                        if (!candidateBounds.Overlaps(placedCollision.Bounds, BroadphaseMarginMeters))
                        {
                            _debugCounters?.IncrementBroadphaseSkips();
                            continue;
                        }

                        (float x, float z)? seam = isSeamNeighbor
                            ? connectedSeamPointMeters
                            : ((float x, float z)?)null;
                        _debugCounters?.IncrementOverlapChecks();
                        if (RoomWallCollision.TryOverlaps(
                                candidateWalls,
                                placedCollision.Walls,
                                seam,
                                out bool wallOverlaps))
                        {
                            if (wallOverlaps)
                            {
                                return true;
                            }

                            continue;
                        }
                    }

                    _selections.TryGetValue(placed.NodeId, out IRoomTemplateDescriptor placedTemplate);
                    if (RoomPlacer.Overlaps(candidate.Pose, candidateTemplate, placed.Pose, placedTemplate))
                    {
                        return true;
                    }
                }

                return false;
            }

            private void CacheCollision(PlacedRoom room, IRoomTemplateDescriptor template)
            {
                if (room == null || string.IsNullOrEmpty(room.NodeId))
                {
                    return;
                }

                SetCollision(room.NodeId, BuildCollision(room, template));
            }

            private RoomCollision GetCollision(PlacedRoom room)
            {
                if (_collisionByNodeId.TryGetValue(room.NodeId, out RoomCollision cached))
                {
                    return cached;
                }

                // Defensive fallback: a placed room should always be cached, but rebuild on demand so an
                // unexpected miss degrades to correct (if slower) behaviour rather than a wrong layout.
                _selections.TryGetValue(room.NodeId, out IRoomTemplateDescriptor template);
                RoomCollision collision = BuildCollision(room, template);
                SetCollision(room.NodeId, collision);
                return collision;
            }

            private void SetCollision(string nodeId, RoomCollision collision)
            {
                bool hadPrevious = _collisionByNodeId.TryGetValue(nodeId, out RoomCollision previous);
                _collisionMutations.Add(new CollisionMutation(nodeId, hadPrevious, previous));
                _collisionByNodeId[nodeId] = collision;
            }

            private static RoomCollision BuildCollision(PlacedRoom room, IRoomTemplateDescriptor template)
            {
                if (RoomWallCollision.TryTransform(room, template,
                        out RoomWallCollision.TransformedWallSegments walls))
                {
                    return new RoomCollision(true, walls, RoomWallCollision.BoundsOf(walls));
                }

                return new RoomCollision(false, default, RoomWallCollision.Aabb.Empty);
            }

            // Возвращает, сколько заглушек неверных ответов зарезервировано вместе с кандидатом.
            private int Apply(Candidate candidate, bool reserveMazeCaps)
            {
                ApplyCandidate(candidate);
                if (reserveMazeCaps
                    && _run.CapEdgesByMazeNode.TryGetValue(candidate.UnplacedNodeId, out List<LevelGraphEdge> capEdges))
                {
                    return ReserveMazeCaps(candidate.UnplacedNodeId, capEdges);
                }

                return 0;
            }

            private void ConsumeWork()
            {
                _run.Work.Consume();
                _debugCounters?.IncrementCandidateEvaluations();
            }

            // Заглушки неверных ответов ставятся сразу вместе с комнатой-лабиринтом: их место у дверей ответа
            // резервируется до того, как поиск поставит основной путь и соседей. Заглушки — часть того же шага
            // поиска, поэтому снимок/откат охватывает их вместе с лабиринтом. Если резерв мешает обязательным
            // комнатам (поддерево провалилось), поиск повторяет того же кандидата без резерва (см. Search).
            // Неподошедшая заглушка не ставится (никогда не с наложением) — см. PlaceOptionalMazeDeadEnds.
            private int ReserveMazeCaps(string mazeNodeId, List<LevelGraphEdge> capEdges)
            {
                int reserved = 0;
                for (int i = 0; i < capEdges.Count; i++)
                {
                    LevelGraphEdge capEdge = capEdges[i];
                    if (_placedByNodeId.ContainsKey(capEdge.ToNodeId))
                    {
                        continue;
                    }

                    List<Candidate> caps = BuildCandidates(capEdge, mazeNodeId, capEdge.ToNodeId);
                    if (caps.Count > 0)
                    {
                        ApplyCandidate(caps[0]);
                        reserved++;
                    }

                    ReleaseCandidateList(caps);
                }

                return reserved;
            }

            private void ApplyCandidate(Candidate candidate)
            {
                _placedByNodeId[candidate.Room.NodeId] = candidate.Room;
                _selections.TryGetValue(candidate.Room.NodeId, out IRoomTemplateDescriptor template);
                CacheCollision(candidate.Room, template);
                _placedOrder.Add(candidate.Room.NodeId);
                ReserveSocket(candidate.PlacedNodeId, candidate.PlacedSocketId);
                ReserveSocket(candidate.UnplacedNodeId, candidate.UnplacedSocketId);
                _connections.Add(candidate.Connection);
                LevelGenerationPlacementTrace.Accepted(
                    candidate.UnplacedNodeId,
                    candidate.Room.RoomTemplateId,
                    candidate.PlacedNodeId,
                    candidate.PlacedSocketId,
                    candidate.UnplacedSocketId,
                    candidate.Room);
                CaptureBestIfNeeded();
            }

            private void CaptureBestIfNeeded()
            {
                if (_placedByNodeId.Count <= _bestPlacedByNodeId.Count)
                {
                    return;
                }

                _bestPlacedByNodeId = new Dictionary<string, PlacedRoom>(_placedByNodeId, StringComparer.Ordinal);
                _bestConnections = new List<DoorConnection>(_connections);
            }

            private Snapshot CreateSnapshot()
            {
                return new Snapshot(
                    _placedOrder.Count,
                    _connections.Count,
                    _socketUseMutations.Count,
                    _collisionMutations.Count);
            }

            private void Restore(Snapshot snapshot)
            {
                // The rollback bookkeeping below only exists to emit trace steps, so skip it entirely when
                // tracing is off to avoid per-backtrack dictionary lookups in the hot search loop.
                if (LevelGenerationPlacementTrace.Enabled)
                {
                    for (int i = _placedOrder.Count - 1; i >= snapshot.PlacedOrderCount; i--)
                    {
                        string nodeId = _placedOrder[i];
                        if (_placedByNodeId.TryGetValue(nodeId, out PlacedRoom room))
                        {
                            LevelGenerationPlacementTrace.Backtracked(nodeId, room.RoomTemplateId);
                        }
                    }
                }

                for (int i = _socketUseMutations.Count - 1; i >= snapshot.SocketUseMutationCount; i--)
                {
                    SocketUseMutation mutation = _socketUseMutations[i];
                    if (mutation.CreatedSet)
                    {
                        _usedSocketsByNodeId.Remove(mutation.NodeId);
                    }
                    else if (mutation.AddedSocket
                             && _usedSocketsByNodeId.TryGetValue(mutation.NodeId, out HashSet<string> sockets))
                    {
                        sockets.Remove(mutation.SocketId);
                    }
                }

                if (_socketUseMutations.Count > snapshot.SocketUseMutationCount)
                {
                    _socketUseMutations.RemoveRange(
                        snapshot.SocketUseMutationCount,
                        _socketUseMutations.Count - snapshot.SocketUseMutationCount);
                }

                for (int i = _collisionMutations.Count - 1; i >= snapshot.CollisionMutationCount; i--)
                {
                    CollisionMutation mutation = _collisionMutations[i];
                    if (mutation.HadPrevious)
                    {
                        _collisionByNodeId[mutation.NodeId] = mutation.Previous;
                    }
                    else
                    {
                        _collisionByNodeId.Remove(mutation.NodeId);
                    }
                }

                if (_collisionMutations.Count > snapshot.CollisionMutationCount)
                {
                    _collisionMutations.RemoveRange(
                        snapshot.CollisionMutationCount,
                        _collisionMutations.Count - snapshot.CollisionMutationCount);
                }

                for (int i = _placedOrder.Count - 1; i >= snapshot.PlacedOrderCount; i--)
                {
                    _placedByNodeId.Remove(_placedOrder[i]);
                }

                if (_connections.Count > snapshot.ConnectionCount)
                {
                    _connections.RemoveRange(snapshot.ConnectionCount, _connections.Count - snapshot.ConnectionCount);
                }

                if (_placedOrder.Count > snapshot.PlacedOrderCount)
                {
                    _placedOrder.RemoveRange(snapshot.PlacedOrderCount, _placedOrder.Count - snapshot.PlacedOrderCount);
                }
            }

            private void FillOrderedDoorIndices(
                IRoomTemplateDescriptor template,
                LevelGraphEdge edge,
                string nodeId,
                List<int> result,
                float? roomYaw = null)
            {
                int count = template?.Doors?.Count ?? 0;
                result.Clear();
                if (TryFillAssignedMazeDoorIndex(edge, nodeId, result))
                {
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    result.Add(i);
                }

                _doorIndexComparer.Configure(template, edge, nodeId, roomYaw);
                result.Sort(_doorIndexComparer);
                _doorIndexComparer.Clear();
            }

            // Рёбра лабиринта используют назначенную дверь (см. MazeDoorAssignment): одну, а не перебор всех дверей.
            // Нет назначенной двери под это ребро — кандидатов нет.
            private bool TryFillAssignedMazeDoorIndex(LevelGraphEdge edge, string nodeId, List<int> result)
            {
                if (edge.BranchRole == EdgeBranchRole.None
                    || !IsMazeRoleEndpoint(edge, nodeId)
                    || !_run.TryGetAssignment(nodeId, _restartIndex, out MazeDoorAssignment assignment))
                {
                    return false;
                }

                int index = assignment.ResolveDoorIndex(edge);
                if (index >= 0)
                {
                    result.Add(index);
                }

                return true;
            }

            private bool IsSocketUsed(string nodeId, string socketId)
            {
                return _usedSocketsByNodeId.TryGetValue(nodeId, out HashSet<string> sockets) &&
                       sockets.Contains(socketId);
            }

            private void ReserveSocket(string nodeId, string socketId)
            {
                if (!_usedSocketsByNodeId.TryGetValue(nodeId, out HashSet<string> sockets))
                {
                    sockets = new HashSet<string>(StringComparer.Ordinal);
                    _usedSocketsByNodeId[nodeId] = sockets;
                    bool addedToNewSet = sockets.Add(socketId);
                    _socketUseMutations.Add(new SocketUseMutation(nodeId, socketId, true, addedToNewSet));
                    return;
                }

                bool added = sockets.Add(socketId);
                _socketUseMutations.Add(new SocketUseMutation(nodeId, socketId, false, added));
            }

            private List<Candidate> RentCandidateList() => _candidateListPool.Count > 0 ? _candidateListPool.Pop() : new List<Candidate>();

            private void ReleaseCandidateList(List<Candidate> candidates)
            {
                if (candidates == null)
                {
                    return;
                }

                candidates.Clear();
                _candidateListPool.Push(candidates);
            }

            private List<int> RentDoorIndexList() => _doorIndexListPool.Count > 0 ? _doorIndexListPool.Pop() : new List<int>();

            private void ReleaseDoorIndexList(List<int> indices)
            {
                if (indices == null)
                {
                    return;
                }

                indices.Clear();
                _doorIndexListPool.Push(indices);
            }

            private readonly struct SocketUseMutation
            {
                public readonly string NodeId;
                public readonly string SocketId;
                public readonly bool CreatedSet;
                public readonly bool AddedSocket;

                public SocketUseMutation(string nodeId, string socketId, bool createdSet, bool addedSocket)
                {
                    NodeId = nodeId;
                    SocketId = socketId;
                    CreatedSet = createdSet;
                    AddedSocket = addedSocket;
                }
            }

            private readonly struct CollisionMutation
            {
                public readonly string NodeId;
                public readonly bool HadPrevious;
                public readonly RoomCollision Previous;

                public CollisionMutation(string nodeId, bool hadPrevious, RoomCollision previous)
                {
                    NodeId = nodeId;
                    HadPrevious = hadPrevious;
                    Previous = previous;
                }
            }

            private readonly struct Snapshot
            {
                public readonly int PlacedOrderCount;
                public readonly int ConnectionCount;
                public readonly int SocketUseMutationCount;
                public readonly int CollisionMutationCount;

                public Snapshot(
                    int placedOrderCount,
                    int connectionCount,
                    int socketUseMutationCount,
                    int collisionMutationCount)
                {
                    PlacedOrderCount = placedOrderCount;
                    ConnectionCount = connectionCount;
                    SocketUseMutationCount = socketUseMutationCount;
                    CollisionMutationCount = collisionMutationCount;
                }
            }
        }

        private readonly struct RoomCollision
        {
            public readonly bool HasWalls;
            public readonly RoomWallCollision.TransformedWallSegments Walls;
            public readonly RoomWallCollision.Aabb Bounds;

            public RoomCollision(
                bool hasWalls,
                RoomWallCollision.TransformedWallSegments walls,
                RoomWallCollision.Aabb bounds)
            {
                HasWalls = hasWalls;
                Walls = walls;
                Bounds = bounds;
            }
        }

        // Заглушка, зарезервированная проверкой места под позой лабиринта (ещё не поставлена).
        private readonly struct VirtualRoom
        {
            public readonly PlacedRoom Room;
            public readonly IRoomTemplateDescriptor Template;
            public readonly RoomCollision Collision;

            public VirtualRoom(PlacedRoom room, IRoomTemplateDescriptor template, RoomCollision collision)
            {
                Room = room;
                Template = template;
                Collision = collision;
            }
        }

        private sealed class DoorIndexComparer : IComparer<int>
        {
            private IRoomTemplateDescriptor _template;
            private LevelGraphEdge _edge;
            private string _nodeId;
            private float? _roomYaw;

            public void Configure(
                IRoomTemplateDescriptor template,
                LevelGraphEdge edge,
                string nodeId,
                float? roomYaw)
            {
                _template = template;
                _edge = edge;
                _nodeId = nodeId;
                _roomYaw = roomYaw;
            }

            public void Clear()
            {
                _template = null;
                _nodeId = null;
                _roomYaw = null;
            }

            public int Compare(int a, int b)
            {
                int scoreA = DoorPreferenceScore(_template.Doors[a], _edge, _nodeId, _roomYaw);
                int scoreB = DoorPreferenceScore(_template.Doors[b], _edge, _nodeId, _roomYaw);
                if (scoreA != scoreB)
                {
                    return scoreA.CompareTo(scoreB);
                }

                return string.CompareOrdinal(_template.Doors[a]?.SocketId, _template.Doors[b]?.SocketId);
            }
        }

        private static IReadOnlyDictionary<string, RoomNode> BuildNodeLookup(LevelGraph graph)
        {
            Dictionary<string, RoomNode> result = new(graph.Nodes.Count, StringComparer.Ordinal);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                result[graph.Nodes[i].NodeId] = graph.Nodes[i];
            }

            return result;
        }

        private static bool IsMazeRoleEndpoint(LevelGraphEdge edge, string nodeId)
        {
            if (edge.BranchRole == EdgeBranchRole.MazeEntry)
            {
                return string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal);
            }

            return (edge.BranchRole == EdgeBranchRole.MazeCorrectExit
                    || edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd)
                   && string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal);
        }

        private static int DoorPreferenceScore(
            IDoorSocketDescriptor door,
            LevelGraphEdge edge,
            string nodeId,
            float? roomYaw)
        {
            if (door == null)
            {
                return int.MaxValue;
            }

            bool isFrom = string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal);
            bool isTo = string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal);
            bool locked = IsLockedDoor(door);
            int score = 0;
            if (isTo)
            {
                score += locked ? 10000 : -200;
                score += YawPreferenceScore(door.LocalYawDegrees, 180f);
            }
            else if (isFrom)
            {
                score += locked && edge.IsMainPath ? -200 : 0;
                float yaw = roomYaw.HasValue
                    ? NormalizeYaw(roomYaw.Value + door.LocalYawDegrees)
                    : door.LocalYawDegrees;
                score += edge.IsMainPath
                    ? YawPreferenceScore(yaw, 0f)
                    : Math.Min(YawPreferenceScore(yaw, 90f), YawPreferenceScore(yaw, 270f));
            }
            else
            {
                score += locked ? 100 : 0;
            }

            return score;
        }

        private static int YawPreferenceScore(float yaw, float preferredYaw)
        {
            float diff = Math.Abs(NormalizeYaw(yaw - preferredYaw));
            if (diff > 180f)
            {
                diff = 360f - diff;
            }

            return (int)Math.Round(diff);
        }

        private static DoorConnection BuildConnection(
            LevelGraphEdge edge,
            string placedNodeId,
            string placedSocketId,
            string unplacedNodeId,
            string unplacedSocketId)
        {
            if (string.Equals(edge.FromNodeId, placedNodeId, StringComparison.Ordinal)
                && string.Equals(edge.ToNodeId, unplacedNodeId, StringComparison.Ordinal))
            {
                return new DoorConnection(
                    placedNodeId,
                    placedSocketId,
                    unplacedNodeId,
                    unplacedSocketId,
                    edge.IsMainPath);
            }

            return new DoorConnection(
                unplacedNodeId,
                unplacedSocketId,
                placedNodeId,
                placedSocketId,
                edge.IsMainPath);
        }

        private static bool CanUseDoorForConnection(LevelGraphEdge edge, string nodeId, IDoorSocketDescriptor door)
        {
            if (!door.IsConnectable())
            {
                return false;
            }

            bool pathAllowed = edge.IsMainPath ? door.CanBeMainPath : door.CanBeSidePath;
            if (!pathAllowed)
            {
                return false;
            }

            if (string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal))
            {
                return door.CanBeExit();
            }

            if (string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal))
            {
                return door.CanBeEntry();
            }

            return false;
        }

        private static bool CanUsePlacedDoorForCandidate(LevelGraphEdge edge, IDoorSocketDescriptor door)
        {
            return door.IsConnectable()
                   && IsPathAllowed(edge, door)
                   && door.CanBeExit();
        }

        private static bool CanUseUnplacedDoorForCandidate(LevelGraphEdge edge, IDoorSocketDescriptor door)
        {
            return door.IsConnectable()
                   && IsPathAllowed(edge, door)
                   && door.CanBeEntry();
        }

        private static bool IsPathAllowed(LevelGraphEdge edge, IDoorSocketDescriptor door) => edge.IsMainPath ? door.CanBeMainPath : door.CanBeSidePath;

        private static bool WouldUseLockedIncomingDoor(
            LevelGraphEdge edge,
            string placedNodeId,
            IDoorSocketDescriptor placedDoor,
            string unplacedNodeId,
            IDoorSocketDescriptor unplacedDoor)
        {
            if (string.Equals(edge.ToNodeId, placedNodeId, StringComparison.Ordinal))
            {
                return IsLockedDoor(placedDoor);
            }

            if (string.Equals(edge.ToNodeId, unplacedNodeId, StringComparison.Ordinal))
            {
                return IsLockedDoor(unplacedDoor);
            }

            return false;
        }

        private static bool IsLockedDoor(IDoorSocketDescriptor door) => door is IDoorSocketInitialStateDescriptor state && state.InitialState == DoorState.Locked;

        private static bool DoorFramesCompatible(IDoorSocketDescriptor a, IDoorSocketDescriptor b)
        {
            const float toleranceMeters = 0.35f;
            return Math.Abs(a.FrameWidthMeters() - b.FrameWidthMeters()) <= toleranceMeters;
        }

        private static Dictionary<string, DoorState> EmptyDoorStates(IRoomTemplateDescriptor template)
        {
            if (template?.Doors == null || template.Doors.Count == 0)
            {
                return new Dictionary<string, DoorState>(0);
            }

            Dictionary<string, DoorState> result = new(template.Doors.Count, StringComparer.Ordinal);
            for (int i = 0; i < template.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = template.Doors[i];
                result[door.SocketId] = door.IsConnectable() ? InitialState(door) : DoorState.Disabled;
            }

            return result;
        }

        private static DoorState InitialState(IDoorSocketDescriptor door)
        {
            return door is IDoorSocketInitialStateDescriptor initialState
                ? initialState.InitialState
                : DoorState.Closed;
        }

        private static (float x, float y, float z) DoorWorldPointMeters(PlacedRoom room, IDoorSocketDescriptor door)
        {
            (float x, float y, float z) origin = room.WorldPose.HasValue
                ? (x: room.WorldPose.Value.X, y: room.WorldPose.Value.Y, z: room.WorldPose.Value.Z)
                : (x: room.Pose.CellX * DoorSnapMeters, y: 0f, z: room.Pose.CellY * DoorSnapMeters);
            float rotation = YawDegrees(room);
            (float x, float z) local =
                RotateMeters((door.ConnectionLocalXMeters(), door.ConnectionLocalZMeters()), rotation);
            return (origin.x + local.x, origin.y + door.ConnectionLocalYMeters(), origin.z + local.z);
        }

        private static (float x, float z) RoomWorldPositionMeters(PlacedRoom room)
        {
            if (room.WorldPose.HasValue)
            {
                return (room.WorldPose.Value.X, room.WorldPose.Value.Z);
            }

            return (room.Pose.CellX * DoorSnapMeters, room.Pose.CellY * DoorSnapMeters);
        }

        private static float YawDegrees(PlacedRoom room)
        {
            if (!room.WorldPose.HasValue)
            {
                return room.Pose.RotationDegrees;
            }

            RoomWorldPose pose = room.WorldPose.Value;
            float siny = 2f * (pose.RotationW * pose.RotationY + pose.RotationZ * pose.RotationX);
            float cosy = 1f - 2f * (pose.RotationY * pose.RotationY + pose.RotationX * pose.RotationX);
            return NormalizeYaw((float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI));
        }

        private static RoomWorldPose CreateWorldPose(float x, float y, float z, float yawDegrees)
        {
            double halfRadians = yawDegrees * Math.PI / 360.0;
            return new RoomWorldPose(
                x,
                y,
                z,
                0f,
                (float)Math.Sin(halfRadians),
                0f,
                (float)Math.Cos(halfRadians));
        }

        private static (float x, float z) RotateMeters((float x, float z) point, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private static int RoundYaw(float yaw) => (int)Math.Round(NormalizeYaw(yaw));

        private sealed class NullRng : IDeterministicRng
        {
            public int NextInt(int minInclusive, int maxExclusive) => minInclusive;

            public float NextFloat() => 0f;

            public void Shuffle<T>(IList<T> list)
            {
            }
        }

        private readonly struct Candidate
        {
            public readonly PlacedRoom Room;
            public readonly DoorConnection Connection;
            public readonly string PlacedNodeId;
            public readonly string PlacedSocketId;
            public readonly string UnplacedNodeId;
            public readonly string UnplacedSocketId;
            public readonly int PreferenceScore;

            public Candidate(
                PlacedRoom room,
                DoorConnection connection,
                string placedNodeId,
                string placedSocketId,
                string unplacedNodeId,
                string unplacedSocketId,
                int preferenceScore)
            {
                Room = room;
                Connection = connection;
                PlacedNodeId = placedNodeId;
                PlacedSocketId = placedSocketId;
                UnplacedNodeId = unplacedNodeId;
                UnplacedSocketId = unplacedSocketId;
                PreferenceScore = preferenceScore;
            }
        }
    }
}
