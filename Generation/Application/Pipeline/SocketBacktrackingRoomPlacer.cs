using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
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
    /// Constraint placer for authored room prefabs.
    /// Places rooms by matching door socket centers, supports arbitrary yaw angles, and backtracks
    /// when a local placement blocks later graph edges.
    /// </summary>
    public sealed class SocketBacktrackingRoomPlacer : IRoomPlacer, IAsyncRoomPlacerWithBudget,
        IRoomPlacementDebugCountersSink
    {
        // Warnings collected when a maze wrong-answer dead-end had to be FORCE-placed (its cap could not fit
        // without overlapping already-placed geometry). The dead-end IS still placed (the maze contract is
        // honoured), but the overlap is flagged here so MissionGenerator can log it (-> console/stacktrace) and
        // the preview can surface why the layout is imperfect. Reset at the start of every Place/PlaceAsync.
        internal static readonly List<string> ForcedMazeDeadEndWarnings = new();

        private const int MaxRestarts = 3;
        private const int MinSearchStepsPerRestart = 2000;
        private const int SearchStepsPerGraphNode = 140;
        private const int SearchStepsPerGraphEdge = 30;
        private const int MaxCandidatesPerFrontier = 16;
        private const int MaxBacktracksPerRestart = 8000;
        private const int CancellationCheckBacktrackInterval = 64;

        // Кооперативная уступка меряется ВРЕМЕНЕМ, а не числом шагов. Смысл уступки — не занимать поток
        // хоста дольше этого бюджета; сколько шагов поиска в него уложится, зависит от машины и графа.
        // Шаговый счётчик давал сотни Task.Yield за одно размещение, а каждая уступка стоит планировщика
        // (в браузере — кадр, в редакторе — оборот update-цикла). Из-за этого ОДИН И ТОТ ЖЕ сид
        // укладывался в лимит стадии синхронно (2.0 с) и не укладывался кооперативно (4.0 с) — WebGL
        // отбрасывал сиды, которые в редакторе проходили, хотя лесенка ретраев обязана быть одинаковой
        // на всех платформах.
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

        public void SetDebugCounters(LevelGenerationDebugCounters debugCounters) => _debugCounters = debugCounters;

        public LevelLayout Place(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default)
        {
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
            ForcedMazeDeadEndWarnings.Clear();
            LevelLayout best = null;
            for (int attempt = 0; attempt < MaxRestarts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                PlacementState greedyState = new(graph, selections, rng, ct, _debugCounters, _interactiveRoomKinds);
                if (greedyState.TryPlaceMainThenSideLeaves())
                {
                    return greedyState.ToLayout();
                }

                PlacementState state = new(graph, selections, rng, ct, _debugCounters, _interactiveRoomKinds);
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
            return best ?? new PlacementState(graph, selections, rng, ct, _debugCounters, _interactiveRoomKinds).ToLayout();
        }

        public async Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default) =>
            await PlaceAsync(graph, selections, rng, null, ct);

        public async Task<LevelLayout> PlaceAsync(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            IRoomPlacementBudget budget,
            CancellationToken ct = default)
        {
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
            ForcedMazeDeadEndWarnings.Clear();
            LevelLayout best = null;
            for (int attempt = 0; attempt < MaxRestarts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await YieldAsync(ct, budget);

                PlacementState greedyState = new(graph, selections, rng, ct, _debugCounters, _interactiveRoomKinds, budget);
                if (await greedyState.TryPlaceMainThenSideLeavesAsync())
                {
                    return greedyState.ToLayout();
                }

                await YieldAsync(ct, budget);

                PlacementState state = new(graph, selections, rng, ct, _debugCounters, _interactiveRoomKinds, budget);
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
            return best ?? new PlacementState(graph, selections, rng, ct, _debugCounters, _interactiveRoomKinds).ToLayout();
        }

        private static async Task YieldAsync(CancellationToken ct) => await YieldAsync(ct, null);

        private static async Task YieldAsync(CancellationToken ct, IRoomPlacementBudget budget)
        {
            ct.ThrowIfCancellationRequested();
            budget?.ThrowIfExpired();
            await Task.Yield();
            budget?.ThrowIfExpired();
            ct.ThrowIfCancellationRequested();
        }

        private sealed class PlacementState
        {
            private readonly LevelGraph _graph;
            private readonly IReadOnlyDictionary<string, IRoomTemplateDescriptor> _selections;
            private readonly IDeterministicRng _rng;
            private readonly CancellationToken _ct;
            private readonly LevelGenerationDebugCounters _debugCounters;
            private readonly IRoomPlacementBudget _budget;
            private readonly IInteractiveRoomKindRules _interactiveRoomKinds;
            private readonly IReadOnlyDictionary<string, RoomNode> _nodesById;

            // Часы кооперативной уступки: сколько прошло с прошлой отдачи управления хосту.
            private readonly System.Diagnostics.Stopwatch _yieldClock = System.Diagnostics.Stopwatch.StartNew();

            // Maze wrong-answer dead-ends are reserved as graph nodes (so they are attempted), but their
            // placement is BEST-EFFORT, not mandatory: the backtracking search ignores them entirely (so one
            // dead-end that cannot fit never forces a global rollback / seed retry / timeout), and they are
            // placed opportunistically in ToLayout(). A door that still cannot host its cap is left for the
            // dead-end post-pass to seal, with a non-fatal warning from the validator. Empty for non-maze
            // missions, so their search + completion criteria are byte-identical to before.
            private readonly HashSet<string> _optionalNodeIds;

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
            private readonly int _maxSearchSteps;
            private int _searchSteps;
            private int _backtracks;

            public PlacementState(
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
                IDeterministicRng rng,
                CancellationToken ct,
                LevelGenerationDebugCounters debugCounters,
                IInteractiveRoomKindRules interactiveRoomKinds,
                IRoomPlacementBudget budget = null)
            {
                _interactiveRoomKinds = interactiveRoomKinds ?? InteractiveRoomKindRules.Empty;
                _graph = graph;
                _selections = selections;
                _rng = rng;
                _ct = ct;
                _debugCounters = debugCounters;
                _budget = budget;
                _nodesById = BuildNodeLookup(graph);
                _optionalNodeIds = BuildOptionalNodeIds(graph);
                int edgeCount = graph.Edges?.Count ?? 0;
                _maxSearchSteps = Math.Max(
                    MinSearchStepsPerRestart,
                    graph.Nodes.Count * SearchStepsPerGraphNode + edgeCount * SearchStepsPerGraphEdge);
                PlaceStart();
            }

            public bool Search()
            {
                _ct.ThrowIfCancellationRequested();
                if (AllRequiredNodesPlaced())
                {
                    return true;
                }

                if (++_searchSteps > _maxSearchSteps)
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
                    _ct.ThrowIfCancellationRequested();
                    Snapshot snapshot = CreateSnapshot();
                    Apply(candidates[i]);
                    if (Search())
                    {
                        ReleaseCandidateList(candidates);
                        return true;
                    }

                    _backtracks++;
                    _debugCounters?.IncrementBacktracks();
                    if (_backtracks > MaxBacktracksPerRestart
                        || (_backtracks % CancellationCheckBacktrackInterval == 0 && ShouldAbortSearch()))
                    {
                        ReleaseCandidateList(candidates);
                        return false;
                    }

                    Restore(snapshot);
                }

                ReleaseCandidateList(candidates);
                return false;
            }

            /// <summary>
            /// Отдаёт управление хосту, только если с прошлой уступки прошёл кадровый бюджет.
            /// <para>
            /// Отмена и бюджет проверяются на КАЖДОМ чекпойнте, даже когда уступки не происходит:
            /// иначе более редкие уступки сделали бы более редкой и реакцию на таймаут стадии.
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

                if (++_searchSteps > _maxSearchSteps)
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
                    _ct.ThrowIfCancellationRequested();
                    Snapshot snapshot = CreateSnapshot();
                    Apply(candidates[i]);
                    if (await SearchAsync())
                    {
                        ReleaseCandidateList(candidates);
                        return true;
                    }

                    _backtracks++;
                    _debugCounters?.IncrementBacktracks();
                    if (_backtracks > MaxBacktracksPerRestart
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

                Apply(candidates[0]);
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
                return BuildLayout(_placedByNodeId, _connections);
            }

            private static HashSet<string> BuildOptionalNodeIds(LevelGraph graph)
            {
                HashSet<string> optional = new(StringComparer.Ordinal);
                if (graph?.Edges == null)
                {
                    return optional;
                }

                for (int i = 0; i < graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = graph.Edges[i];
                    if (edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd)
                    {
                        optional.Add(edge.ToNodeId);
                    }
                }

                return optional;
            }

            // Completion criterion: every REQUIRED node placed. Maze wrong-answer dead-ends are optional, so a
            // layout that places the whole main path + correct tunnels is a success even if some dead-end caps
            // could not fit (those doors are sealed by the post-pass). For non-maze missions _optionalNodeIds is
            // empty, so this is identical to the previous "all nodes placed" check.
            private bool AllRequiredNodesPlaced()
            {
                if (_optionalNodeIds.Count == 0)
                {
                    return _placedByNodeId.Count == _graph.Nodes.Count;
                }

                for (int i = 0; i < _graph.Nodes.Count; i++)
                {
                    string nodeId = _graph.Nodes[i].NodeId;
                    if (!_optionalNodeIds.Contains(nodeId) && !_placedByNodeId.ContainsKey(nodeId))
                    {
                        return false;
                    }
                }

                return true;
            }

            // Best-effort, non-backtracking pass: attach each reserved maze wrong-answer dead-end to its assigned
            // maze exit door if it physically fits. A cap that cannot fit is simply skipped (the door is left for
            // the dead-end post-pass to seal); we never roll back or fail here. Deterministic (applies the first
            // pinned candidate, no RNG), and a no-op for non-maze missions.
            private void PlaceOptionalMazeDeadEnds()
            {
                if (_optionalNodeIds.Count == 0)
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
                    bool forced = false;
                    if (candidates.Count == 0)
                    {
                        // No clean (non-overlapping) pose for this wrong-answer cap. The maze contract requires a
                        // real dead-end room here, so FORCE-place it (allow overlap) and warn instead of leaving
                        // the door open. The overlap is recorded for the validator/preview to surface.
                        ReleaseCandidateList(candidates);
                        candidates = BuildCandidates(edge, edge.FromNodeId, edge.ToNodeId, true);
                        forced = true;
                    }

                    if (candidates.Count > 0)
                    {
                        Apply(candidates[0]);
                        if (forced)
                        {
                            ForcedMazeDeadEndWarnings.Add(
                                $"Maze dead-end force-placed WITH OVERLAP: maze='{edge.FromNodeId}', deadEnd='{edge.ToNodeId}'. "
                                + "The cap could not fit without overlapping already-placed rooms/walls (the dead-end "
                                + "template is likely too wide for the gap between this room's answer doors). Placed "
                                + "anyway to honour the maze contract; review geometry in the preview.");
                        }
                    }
                    else
                    {
                        ForcedMazeDeadEndWarnings.Add(
                            $"Maze dead-end could NOT be placed at all: maze='{edge.FromNodeId}', deadEnd='{edge.ToNodeId}' "
                            + "(no compatible door pose). Door left open.");
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
                    EmptyDoorStates(template),
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
                    // Optional maze wrong-answer dead-ends are not expanded by the search; they are placed
                    // best-effort in ToLayout(). Skipping them keeps an unfittable cap from failing the whole
                    // frontier (and triggering a global rollback / timeout).
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
                string unplacedNodeId,
                bool allowOverlap = false)
            {
                // Candidate construction (door pairing + overlap tests) is the heaviest unit of work in the
                // search, so honour cancellation here to keep the wall-clock time budget from overshooting by
                // more than a single frontier evaluation.
                _ct.ThrowIfCancellationRequested();
                List<Candidate> result = RentCandidateList();
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
                            LevelGenerationPlacementTrace.Rejected(
                                unplacedNodeId,
                                unplacedTemplate.RoomId,
                                $"placed door is not an exit/both socket: {placedNodeId}.{placedDoor.SocketId}");
                            continue;
                        }

                        if (IsSocketUsed(placedNodeId, placedDoor.SocketId))
                        {
                            LevelGenerationPlacementTrace.Rejected(
                                unplacedNodeId,
                                unplacedTemplate.RoomId,
                                $"placed door already used: {placedNodeId}.{placedDoor.SocketId}");
                            continue;
                        }

                        (float x, float y, float z) placedDoorPoint = DoorWorldPointMeters(placedRoom, placedDoor);
                        float placedDoorYaw = NormalizeYaw(placedYaw + placedDoor.LocalYawDegrees);
                        const int placedYawVariantCount = 1;

                        for (int u = 0; u < unplacedDoorIndices.Count; u++)
                        {
                            IDoorSocketDescriptor unplacedDoor = unplacedTemplate.Doors[unplacedDoorIndices[u]];
                            if (!CanUseUnplacedDoorForCandidate(edge, unplacedDoor))
                            {
                                continue;
                            }

                            const int unplacedYawVariantCount = 1;
                            for (int pv = 0; pv < placedYawVariantCount; pv++)
                            {
                                float placedYawVariant = NormalizeYaw(placedDoorYaw + pv * 180f);
                                for (int uv = 0; uv < unplacedYawVariantCount; uv++)
                                {
                                    float unplacedDoorYawVariant =
                                        NormalizeYaw(unplacedDoor.LocalYawDegrees + uv * 180f);
                                    float unplacedYaw =
                                        NormalizeYaw(placedYawVariant + 180f - unplacedDoorYawVariant);
                                    (float x, float z) unplacedLocalDoor = RotateMeters(
                                        (unplacedDoor.ConnectionLocalXMeters(), unplacedDoor.ConnectionLocalZMeters()),
                                        unplacedYaw);
                                    (float x, float z) origin = (
                                        x: placedDoorPoint.x - unplacedLocalDoor.x,
                                        z: placedDoorPoint.z - unplacedLocalDoor.z);
                                    float originY = placedDoorPoint.y - unplacedDoor.ConnectionLocalYMeters();
                                    PlacedRoom room = CreatePlacedRoom(
                                        unplacedNodeId,
                                        unplacedTemplate,
                                        origin,
                                        originY,
                                        unplacedYaw);
                                    LevelGenerationPlacementTrace.TryCandidate(
                                        unplacedNodeId,
                                        unplacedTemplate.RoomId,
                                        placedNodeId,
                                        placedDoor.SocketId,
                                        unplacedDoor.SocketId,
                                        room);

                                    if (WouldUseLockedIncomingDoor(edge, placedNodeId, placedDoor, unplacedNodeId,
                                            unplacedDoor))
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

                                    if (placedDoor.Size != unplacedDoor.Size)
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

                                    if (!DoorFramesCompatible(placedDoor, unplacedDoor))
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

                                    if (!allowOverlap && OverlapsAny(
                                            room,
                                            unplacedTemplate,
                                            placedNodeId,
                                            (placedDoorPoint.x, placedDoorPoint.z)))
                                    {
                                        _debugCounters?.IncrementOverlapRejects();
                                        LevelGenerationPlacementTrace.Rejected(
                                            unplacedNodeId,
                                            unplacedTemplate.RoomId,
                                            "overlap with placed room/walls",
                                            room,
                                            placedNodeId,
                                            placedDoor.SocketId,
                                            unplacedDoor.SocketId);
                                        continue;
                                    }

                                    _debugCounters?.IncrementCandidatesBuilt();
                                    result.Add(new Candidate(
                                        room,
                                        BuildConnection(edge, placedNodeId, placedDoor.SocketId, unplacedNodeId,
                                            unplacedDoor.SocketId),
                                        placedNodeId,
                                        placedDoor.SocketId,
                                        unplacedNodeId,
                                        unplacedDoor.SocketId,
                                        CandidatePreferenceScore(room, placedNodeId, p, u, pv, uv)));
                                }
                            }
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
                int unplacedDoorOrder,
                int placedYawVariantOrder,
                int unplacedYawVariantOrder)
            {
                (float x, float z) candidatePosition = RoomWorldPositionMeters(room);
                float nearestDistanceSquared = float.MaxValue;
                int nearbyRooms = 0;
                int yawVariantPenalty = placedYawVariantOrder * 250000 + unplacedYawVariantOrder * 125000;

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
                           + yawVariantPenalty
                           + _rng.NextInt(0, 128);
                }

                int crowdingPenalty = (int)Math.Round(100000f / Math.Max(25f, nearestDistanceSquared));
                int randomTieBreaker = _rng.NextInt(0, 128);
                return placedDoorOrder * 1000000
                       + unplacedDoorOrder * 10000
                       + yawVariantPenalty
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

            private PlacedRoom CreatePlacedRoom(
                string nodeId,
                IRoomTemplateDescriptor template,
                (float x, float z) origin,
                float originY,
                float yawDegrees)
            {
                _nodesById.TryGetValue(nodeId, out RoomNode node);
                return new PlacedRoom(
                    nodeId,
                    template.RoomId,
                    new GridPose(origin.x / DoorSnapMeters, origin.z / DoorSnapMeters, RoundYaw(yawDegrees)),
                    EmptyDoorStates(template),
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
                bool hasCandidateWalls = RoomWallCollision.TryTransform(
                    candidate,
                    candidateTemplate,
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

            private void Apply(Candidate candidate)
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
                if (TryFillAssignedMazeDoorIndex(template, edge, nodeId, result, roomYaw))
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

            private bool TryFillAssignedMazeDoorIndex(
                IRoomTemplateDescriptor template,
                LevelGraphEdge edge,
                string nodeId,
                List<int> result,
                float? roomYaw)
            {
                if (edge.BranchRole == EdgeBranchRole.None
                    || !IsMazeBranchTemplate(template)
                    || !IsMazeRoleEndpoint(edge, nodeId))
                {
                    return false;
                }

                if (TryResolveAssignedMazeDoorIndex(template, edge, nodeId, roomYaw, out int index))
                {
                    result.Add(index);
                }

                return true;
            }

            private bool TryResolveAssignedMazeDoorIndex(
                IRoomTemplateDescriptor template,
                LevelGraphEdge edge,
                string nodeId,
                float? roomYaw,
                out int index)
            {
                index = -1;
                if (edge.BranchRole == EdgeBranchRole.MazeEntry)
                {
                    return TryResolveMazeEntryDoorIndex(template, edge, nodeId, roomYaw, out index);
                }

                if (edge.BranchRole == EdgeBranchRole.MazeCorrectExit
                    || edge.BranchRole == EdgeBranchRole.MazeWrongDeadEnd)
                {
                    return TryResolveMazeExitDoorIndex(template, edge, nodeId, roomYaw, out index);
                }

                return false;
            }

            private bool TryResolveMazeEntryDoorIndex(
                IRoomTemplateDescriptor template,
                LevelGraphEdge entryEdge,
                string mazeNodeId,
                float? roomYaw,
                out int index)
            {
                index = -1;
                List<int> ordered = SortedDoorIndices(template, entryEdge, mazeNodeId, roomYaw);
                try
                {
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        IDoorSocketDescriptor door = template.Doors[ordered[i]];
                        if (CanUseDoorForConnection(entryEdge, mazeNodeId, door) && !IsLockedDoor(door))
                        {
                            index = ordered[i];
                            return true;
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseDoorIndexList(ordered);
                }
            }

            private bool TryResolveMazeExitDoorIndex(
                IRoomTemplateDescriptor template,
                LevelGraphEdge edge,
                string mazeNodeId,
                float? roomYaw,
                out int index)
            {
                index = -1;
                TryFindMazeEntryEdge(mazeNodeId, out LevelGraphEdge entryEdge);
                int entryIndex = -1;
                if (entryEdge.BranchRole == EdgeBranchRole.MazeEntry)
                {
                    TryResolveMazeEntryDoorIndex(template, entryEdge, mazeNodeId, roomYaw, out entryIndex);
                }

                LevelGraphEdge orderEdge = edge;
                if (TryFindMazeCorrectEdge(mazeNodeId, out LevelGraphEdge correctEdge))
                {
                    orderEdge = correctEdge;
                }

                List<int> answerOrder = SortedDoorIndices(template, orderEdge, mazeNodeId, roomYaw);
                try
                {
                    List<int> usableExits = RentDoorIndexList();
                    try
                    {
                        for (int i = 0; i < answerOrder.Count; i++)
                        {
                            int candidateIndex = answerOrder[i];
                            if (candidateIndex == entryIndex)
                            {
                                continue;
                            }

                            IDoorSocketDescriptor door = template.Doors[candidateIndex];
                            if (CanUseDoorForConnection(orderEdge, mazeNodeId, door))
                            {
                                usableExits.Add(candidateIndex);
                            }
                        }

                        if (usableExits.Count == 0)
                        {
                            return false;
                        }

                        if (edge.BranchRole == EdgeBranchRole.MazeCorrectExit)
                        {
                            index = usableExits[0];
                            return true;
                        }

                        int wrongOrdinal = MazeWrongEdgeOrdinal(edge, mazeNodeId);
                        int wrongIndex = wrongOrdinal + 1;
                        if (wrongIndex >= usableExits.Count)
                        {
                            return false;
                        }

                        index = usableExits[wrongIndex];
                        return true;
                    }
                    finally
                    {
                        ReleaseDoorIndexList(usableExits);
                    }
                }
                finally
                {
                    ReleaseDoorIndexList(answerOrder);
                }
            }

            private List<int> SortedDoorIndices(
                IRoomTemplateDescriptor template,
                LevelGraphEdge edge,
                string nodeId,
                float? roomYaw)
            {
                int count = template?.Doors?.Count ?? 0;
                List<int> ordered = RentDoorIndexList();
                for (int i = 0; i < count; i++)
                {
                    ordered.Add(i);
                }

                _doorIndexComparer.Configure(template, edge, nodeId, roomYaw);
                ordered.Sort(_doorIndexComparer);
                _doorIndexComparer.Clear();
                return ordered;
            }

            private bool TryFindMazeEntryEdge(string mazeNodeId, out LevelGraphEdge entryEdge)
            {
                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (edge.BranchRole == EdgeBranchRole.MazeEntry
                        && string.Equals(edge.ToNodeId, mazeNodeId, StringComparison.Ordinal))
                    {
                        entryEdge = edge;
                        return true;
                    }
                }

                entryEdge = default;
                return false;
            }

            private bool TryFindMazeCorrectEdge(string mazeNodeId, out LevelGraphEdge correctEdge)
            {
                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (edge.BranchRole == EdgeBranchRole.MazeCorrectExit
                        && string.Equals(edge.FromNodeId, mazeNodeId, StringComparison.Ordinal))
                    {
                        correctEdge = edge;
                        return true;
                    }
                }

                correctEdge = default;
                return false;
            }

            private int MazeWrongEdgeOrdinal(LevelGraphEdge targetEdge, string mazeNodeId)
            {
                int ordinal = 0;
                for (int i = 0; i < _graph.Edges.Count; i++)
                {
                    LevelGraphEdge edge = _graph.Edges[i];
                    if (edge.BranchRole != EdgeBranchRole.MazeWrongDeadEnd
                        || !string.Equals(edge.FromNodeId, mazeNodeId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.Equals(edge.ToNodeId, targetEdge.ToNodeId, StringComparison.Ordinal))
                    {
                        return ordinal;
                    }

                    ordinal++;
                }

                return ordinal;
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

            private bool IsMazeBranchTemplate(IRoomTemplateDescriptor template) =>
                InteractiveMissionContentMatcher.HasDoorPerOptionAnswers(_interactiveRoomKinds, template);

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

            private static IReadOnlyDictionary<string, RoomNode> BuildNodeLookup(LevelGraph graph)
            {
                Dictionary<string, RoomNode> result = new(graph.Nodes.Count, StringComparer.Ordinal);
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    result[graph.Nodes[i].NodeId] = graph.Nodes[i];
                }

                return result;
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

        private static IReadOnlyDictionary<string, DoorState> EmptyDoorStates(IRoomTemplateDescriptor template)
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
