using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Facade;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Placement;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Policies;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Facade for the level-generation pipeline. Builds a deterministic <see cref="LevelLayout"/>
    /// from a mission config + seed. The host game owns async scheduling and event publishing.
    /// </summary>
    public sealed class MissionGenerator
    {
        private const int DefaultMaxAttempts = 48;
#if UNITY_WEBGL && !UNITY_EDITOR
        // A manual GC.Collect stalls the single-threaded WebGL frame, so it is a deliberate, rare pause.
        // The hot placement loop no longer allocates trace steps per candidate (see
        // LevelGenerationPlacementTrace), so per-attempt garbage is much lower and we can reclaim far less
        // often, leaning on incremental GC (gcIncremental) between these points.
        private const int WebGlGcEveryRejectedStages = 48;
#endif
        private static readonly TimeSpan DefaultGenerationTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Per-call generation time budget. Preview and tests may raise this.</summary>
        public TimeSpan GenerationTimeout { get; set; } = DefaultGenerationTimeout;

        /// <summary>
        /// Multiplier applied to the resolved generation time budget. Stays 1.0 for tests/preview (so they
        /// validate the authored budget), but the runtime computes it from the authored budget and its
        /// runtime cap (e.g. 5s authored -> x6 for a 30s runtime cap).
        /// </summary>
        public float GenerationTimeBudgetMultiplier { get; set; } = 1f;

        /// <summary>How many seed offsets to try before failing.</summary>
        public int MaxGenerationAttempts { get; set; } = DefaultMaxAttempts;

        private readonly IDeterministicRngFactory _rngFactory;
        private readonly IMissionGraphBuilder _graphBuilder;
        private readonly IRoomSelector _roomSelector;
        private readonly IRoomPlacer _roomPlacer;
        private readonly IDoorConnector _doorConnector;
        private readonly ILevelValidator _validator;
        private readonly IRoomDatabase _roomDatabase;
        private readonly ITilesetCatalog _tilesetCatalog;
        private readonly IInteractiveRoomKindRules _interactiveRoomKinds;
        private readonly IReadOnlyList<IRoomDependencyHintExtractor> _hintExtractors;
        private readonly List<string> _lastWarnings = new();
        private LevelGraph _lastGraph;
        private int _isGenerating;
#if UNITY_WEBGL && !UNITY_EDITOR
        private int _rejectedStagesSinceGc;
#endif

        public MissionGenerator(
            IDeterministicRngFactory rngFactory,
            IMissionGraphBuilder graphBuilder,
            IRoomSelector roomSelector,
            IRoomPlacer roomPlacer,
            IDoorConnector doorConnector,
            ILevelValidator validator,
            IRoomDatabase roomDatabase,
            ITilesetCatalog tilesetCatalog,
            IInteractiveRoomKindRules interactiveRoomKinds,
            IEnumerable<IRoomDependencyHintExtractor> hintExtractors = null)
        {
            _rngFactory = rngFactory;
            _graphBuilder = graphBuilder;
            _roomSelector = roomSelector;
            _roomPlacer = roomPlacer;
            _doorConnector = doorConnector;
            _validator = validator;
            _roomDatabase = roomDatabase;
            _tilesetCatalog = tilesetCatalog;
            _interactiveRoomKinds = interactiveRoomKinds
                                    ?? throw new ArgumentNullException(nameof(interactiveRoomKinds));
            _hintExtractors = hintExtractors != null
                ? new List<IRoomDependencyHintExtractor>(hintExtractors)
                : Array.Empty<IRoomDependencyHintExtractor>();
        }

        public IReadOnlyList<string> LastWarnings => _lastWarnings;
        public LevelGraph LastGraph => _lastGraph;
        public int LastSuccessfulSeed { get; private set; }
        public LevelGenerationDebugCounters LastDebugCounters { get; } = new();

        /// <summary>
        /// Enables lightweight generation diagnostics. Disabled by default, so production builds stay quiet.
        /// Assign <see cref="DiagnosticLogSink"/> to route messages into Unity Debug.Log, a file, or a custom overlay.
        /// </summary>
        public bool DiagnosticLogsEnabled { get; set; }

        public Action<string> DiagnosticLogSink { get; set; }

        public LevelLayout BuildLayout(IMissionConfigView config, int seed, CancellationToken ct = default)
        {
            EnterGeneration();
            try
            {
                if (config == null)
                {
                    throw new ArgumentNullException(nameof(config));
                }

                LastDebugCounters.SetStage("resolve-tileset");
                ITilesetView tileset = _tilesetCatalog.FindTileset(config.TilesetId);
                if (tileset == null)
                {
                    throw new InvalidOperationException($"Tileset '{config.TilesetId}' not found.");
                }

                bool useLayoutPacingV2 =
                    TryResolveLayoutPacingConfig(config, out LayoutPacingConfig layoutPacingConfig);
                return BuildLayoutCore(config, tileset, _roomDatabase, _validator, seed, ct, useLayoutPacingV2,
                    layoutPacingConfig);
            }
            finally
            {
                ExitGeneration();
            }
        }

        /// <summary>
        /// Runs generation asynchronously and observes <paramref name="ct"/> during every expensive stage.
        /// Standalone/Editor builds use a worker thread with Unity-object snapshots; WebGL uses a cooperative
        /// single-threaded path so the browser main loop can keep pumping between generator stages.
        /// </summary>
        public Task<LevelLayout> BuildLayoutAsync(IMissionConfigView config, int seed, CancellationToken ct = default)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            ct.ThrowIfCancellationRequested();

#if UNITY_WEBGL && !UNITY_EDITOR
            LogDiagnostic($"Async generation running cooperatively on WebGL. seed={seed}, mission='{config.MissionId}'.");
            return BuildLayoutCooperativeAsync(config, seed, ct);
#else
            EnterGeneration();

            try
            {
                LastDebugCounters.SetStage("snapshot-inputs");
                ITilesetView tileset = _tilesetCatalog.FindTileset(config.TilesetId);
                if (tileset == null)
                {
                    throw new InvalidOperationException($"Tileset '{config.TilesetId}' not found.");
                }

                bool useLayoutPacingV2 =
                    TryResolveLayoutPacingConfig(config, out LayoutPacingConfig layoutPacingConfig);
                IMissionConfigView configSnapshot = MissionConfigViewSnapshot.Capture(config);
                ITilesetView tilesetSnapshot = TilesetViewSnapshot.Capture(tileset);
                IRoomDatabase roomDatabaseSnapshot = RoomDatabaseSnapshot.Capture(_roomDatabase);
                ILevelValidator validator = _validator is LevelValidator
                    ? new LevelValidator(roomDatabaseSnapshot, _hintExtractors)
                    : _validator;

                LogDiagnostic($"Async generation scheduled. seed={seed}, tileset='{configSnapshot.TilesetId}'.");
                return Task.Run(() =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        return BuildLayoutCore(
                            configSnapshot,
                            tilesetSnapshot,
                            roomDatabaseSnapshot,
                            validator,
                            seed,
                            ct,
                            useLayoutPacingV2,
                            layoutPacingConfig);
                    }
                    finally
                    {
                        ExitGeneration();
                    }
                });
            }
            catch
            {
                ExitGeneration();
                throw;
            }
#endif
        }

        /// <summary>
        /// Builds a layout on the current thread while yielding between expensive stages.
        /// This is the WebGL runtime path and is explicit for tests.
        /// </summary>
        public async Task<LevelLayout> BuildLayoutCooperativeAsync(
            IMissionConfigView config,
            int seed,
            CancellationToken ct = default)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            ct.ThrowIfCancellationRequested();
            EnterGeneration();

            try
            {
                LastDebugCounters.SetStage("resolve-tileset");
                ITilesetView tileset = _tilesetCatalog.FindTileset(config.TilesetId);
                if (tileset == null)
                {
                    throw new InvalidOperationException($"Tileset '{config.TilesetId}' not found.");
                }

                bool useLayoutPacingV2 =
                    TryResolveLayoutPacingConfig(config, out LayoutPacingConfig layoutPacingConfig);
                await YieldAsync(ct);
                return await BuildLayoutCoreAsync(
                    config,
                    tileset,
                    _roomDatabase,
                    _validator,
                    seed,
                    ct,
                    useLayoutPacingV2,
                    layoutPacingConfig);
            }
            finally
            {
                ExitGeneration();
            }
        }

        private LevelLayout BuildLayoutCore(
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase,
            ILevelValidator validator,
            int seed,
            CancellationToken ct,
            bool useLayoutPacingV2 = false,
            LayoutPacingConfig layoutPacingConfig = null)
        {
            _lastWarnings.Clear();
            _lastGraph = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            _rejectedStagesSinceGc = 0;
#endif
            LastSuccessfulSeed = seed;
            LastDebugCounters.Reset();
            LastDebugCounters.SetStage("start");
            LogDiagnostic($"Generation started. seed={seed}, tileset='{config?.TilesetId}'.");
            System.Diagnostics.Stopwatch genClock = System.Diagnostics.Stopwatch.StartNew();

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (tileset == null)
            {
                throw new ArgumentNullException(nameof(tileset));
            }

            if (roomDatabase == null)
            {
                throw new ArgumentNullException(nameof(roomDatabase));
            }

            if (validator == null)
            {
                throw new ArgumentNullException(nameof(validator));
            }

            LastDebugCounters.SetStage("prepare-interactive-content");
            IMissionConfigView effectiveConfig = PrepareInteractiveContentConfig(config, tileset, roomDatabase);

            LevelValidationResult lastValidation = null;
            int lastAttemptSeed = seed;
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TimeSpan generationTimeout = ApplyBudgetMultiplier(useLayoutPacingV2 && layoutPacingConfig != null
                ? TimeSpan.FromSeconds(layoutPacingConfig.GenerationTimeBudgetSeconds)
                : GenerationTimeout);
            timeoutCts.CancelAfter(generationTimeout);
            CancellationToken generationCt = timeoutCts.Token;

            try
            {
                IReadOnlyList<LayoutRetryStage> retryStages = useLayoutPacingV2 && layoutPacingConfig != null
                    ? GetLayoutPacingRetryStages(layoutPacingConfig.RetryPolicy)
                    : new[] { LayoutRetryStage.Full };
                TimeSpan placementStageTimeout = ResolvePlacementStageTimeout(generationTimeout);

                for (int attempt = 0; attempt < MaxGenerationAttempts; attempt++)
                {
                    generationCt.ThrowIfCancellationRequested();

                    int attemptSeed = seed + attempt;
                    lastAttemptSeed = attemptSeed;
                    bool skipRemainingStagesForAttempt = false;
                    for (int stageIndex = 0; stageIndex < retryStages.Count; stageIndex++)
                    {
                        LayoutRetryStage retryStage = retryStages[stageIndex];
                        LastDebugCounters.BeginAttempt(attemptSeed);
                        LogDiagnostic(
                            $"Attempt {attempt + 1}/{MaxGenerationAttempts} started. seed={attemptSeed}, stage={retryStage}.");
                        IDeterministicRng rng = _rngFactory.Create(attemptSeed);

                        LevelGraph graph;
                        if (useLayoutPacingV2 && layoutPacingConfig != null)
                        {
                            LastDebugCounters.SetStage("build-graph-v2-" + retryStage);
                            try
                            {
                                graph = BuildLayoutPacingV2Graph(effectiveConfig, layoutPacingConfig, retryStage,
                                    attemptSeed, generationCt);
                            }
                            catch (InvalidOperationException ex)
                            {
                                LogDiagnostic(
                                    $"Attempt {attempt + 1}, stage {retryStage} rejected during graph build: {ex.Message}");
                                continue;
                            }
                        }
                        else
                        {
                            LastDebugCounters.SetStage("build-graph");
                            graph = _graphBuilder.Build(effectiveConfig, tileset, rng);
                        }

                        LastDebugCounters.SetStage("select-rooms");
                        Dictionary<string, IRoomTemplateDescriptor> selections =
                            SelectRooms(graph, effectiveConfig, tileset, roomDatabase, rng, useLayoutPacingV2);
                        generationCt.ThrowIfCancellationRequested();
                        LastDebugCounters.SetStage("dependency-hints");
                        ApplyDependencyHints(graph, selections, effectiveConfig, tileset, roomDatabase);
                        LastDebugCounters.SetStage("snapshot-selections");
                        Dictionary<string, IRoomTemplateDescriptor> generationSelections =
                            RoomTemplateDescriptorSnapshot.Capture(selections);
                        LevelValidationResult selectionCoverage =
                            ValidateSelectionCoverage(generationSelections, graph);
                        if (!selectionCoverage.IsValid)
                        {
                            lastValidation = selectionCoverage;
                            LogDiagnostic(
                                $"Attempt {attempt + 1}, stage {retryStage} rejected before placement: {FormatIssues(selectionCoverage)}");
                            CollectAfterRejectedStage();
                            continue;
                        }

                        LastDebugCounters.SetStage("place-rooms");
                        if (_roomPlacer is IRoomPlacementDebugCountersSink debugCountersSink)
                        {
                            debugCountersSink.SetDebugCounters(LastDebugCounters);
                        }

                        LevelLayout placed;
                        using (CancellationTokenSource placementStageCts =
                               CancellationTokenSource.CreateLinkedTokenSource(generationCt))
                        {
                            CancelAfterIfPositive(placementStageCts, placementStageTimeout);
                            try
                            {
                                placed = _roomPlacer.Place(
                                    graph,
                                    generationSelections,
                                    rng,
                                    placementStageCts.Token);
                            }
                            catch (OperationCanceledException)
                                when (IsPlacementStageTimeout(placementStageCts, generationCt, ct))
                            {
                                lastValidation = CreatePlacementStageTimeoutValidation(
                                    attemptSeed,
                                    retryStage,
                                    placementStageTimeout);
                                LogDiagnostic(
                                    $"Attempt {attempt + 1}, stage {retryStage} placement timed out after {placementStageTimeout.TotalMilliseconds:0}ms; trying next seed. Debug: {LastDebugCounters}");
                                CollectAfterRejectedStage();
                                skipRemainingStagesForAttempt = true;
                                break;
                            }
                        }

                        if (skipRemainingStagesForAttempt)
                        {
                            break;
                        }

                        generationCt.ThrowIfCancellationRequested();

                        LevelValidationResult placementCoverage = ValidatePlacementCoverage(placed, graph);
                        if (!placementCoverage.IsValid)
                        {
                            lastValidation = placementCoverage;
                            LogDiagnostic(
                                $"Attempt {attempt + 1}, stage {retryStage} rejected after placement: {FormatIssues(placementCoverage)}");
                            CollectAfterRejectedStage();
                            continue;
                        }

                        LastDebugCounters.SetStage("connect-doors");
                        LevelLayout connected = _doorConnector.Connect(placed, graph, generationSelections);
                        LevelGraph validationGraph = graph;
                        LastDebugCounters.SetStage("add-dead-end-rooms");
                        DeadEndRoomPostProcessResult deadEnds = new DeadEndRoomPostProcessor(
                            BuildDeadEndRoomPool(roomDatabase, tileset)).Process(
                            connected,
                            graph,
                            rng,
                            generationCt);
                        connected = deadEnds.Layout;
                        validationGraph = deadEnds.Graph;

                        LastDebugCounters.SetStage("validate-layout");
                        IMissionConfigView validationConfig = useLayoutPacingV2
                            ? new LayoutPacingValidationConfigView(effectiveConfig, connected.Rooms.Count)
                            : new PostProcessedLayoutValidationConfigView(effectiveConfig, connected.Rooms.Count);
                        LevelValidationResult mazeBranchAnswerTopology =
                            ValidateMazeBranchAnswerTopology(
                                connected,
                                validationGraph,
                                generationSelections,
                                effectiveConfig);
                        if (!mazeBranchAnswerTopology.IsValid)
                        {
                            // Non-fatal: maze wrong-answer dead-ends are best-effort. A door that could not be
                            // capped is sealed by the dead-end post-pass; warn but DO NOT reject the seed
                            // (rejecting caused dense maze missions to retry-loop into a generation timeout).
                            LogDiagnostic(
                                $"Attempt {attempt + 1}, stage {retryStage} maze dead-end warning (not rejected): {FormatIssues(mazeBranchAnswerTopology)}");
                        }

                        // Свободные disabled-двери — НЕ ошибка генерации. Dead-end-проход уже попытался закрыть
                        // каждую свободную дверь тупиковой комнатой (перебрав все dead-end шаблоны), а ту, что
                        // закрыть не удалось, запечатал в стену — допустимый best-effort (ср. maze-deadends-best-
                        // effort). Раньше сид ОТКЛОНЯЛСЯ по доле disabled-дверей, и плотные миссии (Procedural_
                        // Tunnels) уходили в retry-loop до 30s таймаута. Теперь только диагностируем, не отклоняем.
                        LogDisabledDoorDiagnostic(connected, generationSelections, deadEnds, attempt + 1, retryStage);

                        LevelValidationResult validation =
                            ValidateLayout(validator, connected, validationGraph, validationConfig, tileset);
                        lastValidation = validation;

                        if (validation.IsValid)
                        {
                            _lastGraph = validationGraph;
                            LastSuccessfulSeed = attemptSeed;
                            LogDiagnostic(
                                $"Generation completed in {genClock.ElapsedMilliseconds} ms. seed={attemptSeed}, rooms={connected.Rooms.Count}, stage={retryStage}. Debug: {LastDebugCounters}");
                            foreach (string mazeWarning in SocketBacktrackingRoomPlacer.ForcedMazeDeadEndWarnings)
                            {
                                LogDiagnostic($"[MazeDeadEnd][WARNING] {mazeWarning}");
                            }

                            return connected;
                        }

                        LogDiagnostic(
                            $"Attempt {attempt + 1}, stage {retryStage} failed validation: {FormatIssues(validation)}");
                        CollectAfterRejectedStage();
                    }

                    if (skipRemainingStagesForAttempt)
                    {
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LogDiagnostic(
                    $"Generation cancelled by caller. Last seed={lastAttemptSeed}. Debug: {LastDebugCounters}");
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                LastDebugCounters.MarkTimeout();
                string timeoutReason =
                    $"Generator timed out after {generationTimeout.TotalSeconds:0}s. Last seed={lastAttemptSeed}. Last issues: {FormatIssues(lastValidation)}. Debug: {LastDebugCounters}";
                LogDiagnostic(timeoutReason);
                throw new TimeoutException(timeoutReason);
            }

            string reason =
                $"Generator exhausted {MaxGenerationAttempts} attempts. Last seed={lastAttemptSeed}. Last issues: {FormatIssues(lastValidation)}. Debug: {LastDebugCounters}";
            LogDiagnostic(reason);
            throw new InvalidOperationException(reason);
        }

        private async Task<LevelLayout> BuildLayoutCoreAsync(
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase,
            ILevelValidator validator,
            int seed,
            CancellationToken ct,
            bool useLayoutPacingV2 = false,
            LayoutPacingConfig layoutPacingConfig = null)
        {
            _lastWarnings.Clear();
            _lastGraph = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            _rejectedStagesSinceGc = 0;
#endif
            LastSuccessfulSeed = seed;
            LastDebugCounters.Reset();
            LastDebugCounters.SetStage("start");
            LogDiagnostic($"Cooperative generation started. seed={seed}, tileset='{config?.TilesetId}'.");

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (tileset == null)
            {
                throw new ArgumentNullException(nameof(tileset));
            }

            if (roomDatabase == null)
            {
                throw new ArgumentNullException(nameof(roomDatabase));
            }

            if (validator == null)
            {
                throw new ArgumentNullException(nameof(validator));
            }

            LastDebugCounters.SetStage("prepare-interactive-content");
            IMissionConfigView effectiveConfig = PrepareInteractiveContentConfig(config, tileset, roomDatabase);
            await YieldAsync(ct);

            LevelValidationResult lastValidation = null;
            int lastAttemptSeed = seed;
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TimeSpan generationTimeout = ApplyBudgetMultiplier(useLayoutPacingV2 && layoutPacingConfig != null
                ? TimeSpan.FromSeconds(layoutPacingConfig.GenerationTimeBudgetSeconds)
                : GenerationTimeout);
            timeoutCts.CancelAfter(generationTimeout);
            CancellationToken generationCt = timeoutCts.Token;

            // WebGL is single-threaded, so the System.Threading.Timer that backs CancelAfter is unreliable
            // there. Enforce the same wall-clock budget explicitly at the cooperative yield points so a
            // pathological seed cannot run past the budget even if the timer never fires.
            System.Diagnostics.Stopwatch wallClock = System.Diagnostics.Stopwatch.StartNew();
            long budgetMs = (long)generationTimeout.TotalMilliseconds;

            try
            {
                IReadOnlyList<LayoutRetryStage> retryStages = useLayoutPacingV2 && layoutPacingConfig != null
                    ? GetLayoutPacingRetryStages(layoutPacingConfig.RetryPolicy)
                    : new[] { LayoutRetryStage.Full };
                TimeSpan placementStageTimeout = ResolvePlacementStageTimeout(generationTimeout);

                for (int attempt = 0; attempt < MaxGenerationAttempts; attempt++)
                {
                    EnforceWallClockBudget(wallClock, budgetMs, timeoutCts, ct);
                    generationCt.ThrowIfCancellationRequested();

                    int attemptSeed = seed + attempt;
                    lastAttemptSeed = attemptSeed;
                    bool skipRemainingStagesForAttempt = false;
                    for (int stageIndex = 0; stageIndex < retryStages.Count; stageIndex++)
                    {
                        EnforceWallClockBudget(wallClock, budgetMs, timeoutCts, ct);
                        await YieldAsync(generationCt);

                        LayoutRetryStage retryStage = retryStages[stageIndex];
                        LastDebugCounters.BeginAttempt(attemptSeed);
                        LogDiagnostic(
                            $"Attempt {attempt + 1}/{MaxGenerationAttempts} started. seed={attemptSeed}, stage={retryStage}.");
                        IDeterministicRng rng = _rngFactory.Create(attemptSeed);

                        LevelGraph graph;
                        if (useLayoutPacingV2 && layoutPacingConfig != null)
                        {
                            LastDebugCounters.SetStage("build-graph-v2-" + retryStage);
                            try
                            {
                                graph = BuildLayoutPacingV2Graph(effectiveConfig, layoutPacingConfig, retryStage,
                                    attemptSeed, generationCt);
                            }
                            catch (InvalidOperationException ex)
                            {
                                LogDiagnostic(
                                    $"Attempt {attempt + 1}, stage {retryStage} rejected during graph build: {ex.Message}");
                                await YieldAfterRejectedStageAsync(generationCt);
                                continue;
                            }
                        }
                        else
                        {
                            LastDebugCounters.SetStage("build-graph");
                            graph = _graphBuilder.Build(effectiveConfig, tileset, rng);
                        }

                        await YieldAsync(generationCt);

                        LastDebugCounters.SetStage("select-rooms");
                        Dictionary<string, IRoomTemplateDescriptor> selections =
                            SelectRooms(graph, effectiveConfig, tileset, roomDatabase, rng, useLayoutPacingV2);
                        generationCt.ThrowIfCancellationRequested();
                        await YieldAsync(generationCt);

                        LastDebugCounters.SetStage("dependency-hints");
                        ApplyDependencyHints(graph, selections, effectiveConfig, tileset, roomDatabase);
                        await YieldAsync(generationCt);

                        LastDebugCounters.SetStage("snapshot-selections");
                        Dictionary<string, IRoomTemplateDescriptor> generationSelections =
                            RoomTemplateDescriptorSnapshot.Capture(selections);
                        LevelValidationResult selectionCoverage =
                            ValidateSelectionCoverage(generationSelections, graph);
                        if (!selectionCoverage.IsValid)
                        {
                            lastValidation = selectionCoverage;
                            LogDiagnostic(
                                $"Attempt {attempt + 1}, stage {retryStage} rejected before placement: {FormatIssues(selectionCoverage)}");
                            await YieldAfterRejectedStageAsync(generationCt);
                            continue;
                        }

                        LastDebugCounters.SetStage("place-rooms");
                        if (_roomPlacer is IRoomPlacementDebugCountersSink debugCountersSink)
                        {
                            debugCountersSink.SetDebugCounters(LastDebugCounters);
                        }

                        LevelLayout placed;
                        using (CancellationTokenSource placementStageCts =
                               CancellationTokenSource.CreateLinkedTokenSource(generationCt))
                        {
                            CancelAfterIfPositive(placementStageCts, placementStageTimeout);
                            try
                            {
                                if (_roomPlacer is IAsyncRoomPlacerWithBudget budgetedAsyncRoomPlacer)
                                {
                                    IRoomPlacementBudget placementBudget = new StopwatchRoomPlacementBudget(
                                        System.Diagnostics.Stopwatch.StartNew(),
                                        placementStageTimeout,
                                        placementStageCts,
                                        generationCt,
                                        ct);
                                    placed = await budgetedAsyncRoomPlacer.PlaceAsync(
                                        graph,
                                        generationSelections,
                                        rng,
                                        placementBudget,
                                        placementStageCts.Token);
                                }
                                else if (_roomPlacer is IAsyncRoomPlacer asyncRoomPlacer)
                                {
                                    placed = await asyncRoomPlacer.PlaceAsync(
                                        graph,
                                        generationSelections,
                                        rng,
                                        placementStageCts.Token);
                                }
                                else
                                {
                                    placed = _roomPlacer.Place(
                                        graph,
                                        generationSelections,
                                        rng,
                                        placementStageCts.Token);
                                }
                            }
                            catch (OperationCanceledException)
                                when (IsPlacementStageTimeout(placementStageCts, generationCt, ct))
                            {
                                lastValidation = CreatePlacementStageTimeoutValidation(
                                    attemptSeed,
                                    retryStage,
                                    placementStageTimeout);
                                LogDiagnostic(
                                    $"Attempt {attempt + 1}, stage {retryStage} placement timed out after {placementStageTimeout.TotalMilliseconds:0}ms; trying next seed. Debug: {LastDebugCounters}");
                                await YieldAfterRejectedStageAsync(generationCt);
                                skipRemainingStagesForAttempt = true;
                                break;
                            }
                        }

                        if (skipRemainingStagesForAttempt)
                        {
                            break;
                        }

                        generationCt.ThrowIfCancellationRequested();
                        await YieldAsync(generationCt);

                        LevelValidationResult placementCoverage = ValidatePlacementCoverage(placed, graph);
                        if (!placementCoverage.IsValid)
                        {
                            lastValidation = placementCoverage;
                            LogDiagnostic(
                                $"Attempt {attempt + 1}, stage {retryStage} rejected after placement: {FormatIssues(placementCoverage)}");
                            await YieldAfterRejectedStageAsync(generationCt);
                            continue;
                        }

                        LastDebugCounters.SetStage("connect-doors");
                        LevelLayout connected = _doorConnector.Connect(placed, graph, generationSelections);
                        await YieldAsync(generationCt);

                        LevelGraph validationGraph = graph;
                        LastDebugCounters.SetStage("add-dead-end-rooms");
                        DeadEndRoomPostProcessResult deadEnds = new DeadEndRoomPostProcessor(
                            BuildDeadEndRoomPool(roomDatabase, tileset)).Process(
                            connected,
                            graph,
                            rng,
                            generationCt);
                        connected = deadEnds.Layout;
                        validationGraph = deadEnds.Graph;
                        await YieldAsync(generationCt);

                        LastDebugCounters.SetStage("validate-layout");
                        IMissionConfigView validationConfig = useLayoutPacingV2
                            ? new LayoutPacingValidationConfigView(effectiveConfig, connected.Rooms.Count)
                            : new PostProcessedLayoutValidationConfigView(effectiveConfig, connected.Rooms.Count);
                        LevelValidationResult mazeBranchAnswerTopology =
                            ValidateMazeBranchAnswerTopology(
                                connected,
                                validationGraph,
                                generationSelections,
                                effectiveConfig);
                        if (!mazeBranchAnswerTopology.IsValid)
                        {
                            // Non-fatal: maze wrong-answer dead-ends are best-effort. A door that could not be
                            // capped is sealed by the dead-end post-pass; warn but DO NOT reject the seed
                            // (rejecting caused dense maze missions to retry-loop into a generation timeout).
                            LogDiagnostic(
                                $"Attempt {attempt + 1}, stage {retryStage} maze dead-end warning (not rejected): {FormatIssues(mazeBranchAnswerTopology)}");
                        }

                        // Свободные disabled-двери — НЕ ошибка генерации (см. подробный комментарий в варианте
                        // без LayoutPacingV2 выше). Dead-end-проход закрыл что смог, остальное запечатал в стену;
                        // долю disabled-дверей только диагностируем, сид не отклоняем.
                        LogDisabledDoorDiagnostic(connected, generationSelections, deadEnds, attempt + 1, retryStage);

                        LevelValidationResult validation =
                            ValidateLayout(validator, connected, validationGraph, validationConfig, tileset);
                        lastValidation = validation;

                        if (validation.IsValid)
                        {
                            _lastGraph = validationGraph;
                            LastSuccessfulSeed = attemptSeed;
                            LogDiagnostic(
                                $"Generation completed in {wallClock.ElapsedMilliseconds} ms. seed={attemptSeed}, rooms={connected.Rooms.Count}, stage={retryStage}. Debug: {LastDebugCounters}");
                            foreach (string mazeWarning in SocketBacktrackingRoomPlacer.ForcedMazeDeadEndWarnings)
                            {
                                LogDiagnostic($"[MazeDeadEnd][WARNING] {mazeWarning}");
                            }

                            return connected;
                        }

                        LogDiagnostic(
                            $"Attempt {attempt + 1}, stage {retryStage} failed validation: {FormatIssues(validation)}");
                        await YieldAfterRejectedStageAsync(generationCt);
                    }

                    if (skipRemainingStagesForAttempt)
                    {
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                LogDiagnostic(
                    $"Generation cancelled by caller. Last seed={lastAttemptSeed}. Debug: {LastDebugCounters}");
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                LastDebugCounters.MarkTimeout();
                string timeoutReason =
                    $"Generator timed out after {generationTimeout.TotalSeconds:0}s. Last seed={lastAttemptSeed}. Last issues: {FormatIssues(lastValidation)}. Debug: {LastDebugCounters}";
                LogDiagnostic(timeoutReason);
                throw new TimeoutException(timeoutReason);
            }

            string reason =
                $"Generator exhausted {MaxGenerationAttempts} attempts. Last seed={lastAttemptSeed}. Last issues: {FormatIssues(lastValidation)}. Debug: {LastDebugCounters}";
            LogDiagnostic(reason);
            throw new InvalidOperationException(reason);
        }

        private static async Task YieldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }

        // Trips the timeout token once the wall-clock budget is spent. We cancel the (already linked)
        // timeout source rather than throwing directly so the existing timeout catch path runs unchanged
        // (MarkTimeout + TimeoutException). A zero/negative budget disables the check. Caller cancellation
        // is left to propagate on its own so it is reported as a cancel, not a timeout.
        private static void EnforceWallClockBudget(
            System.Diagnostics.Stopwatch wallClock,
            long budgetMs,
            CancellationTokenSource timeoutCts,
            CancellationToken callerCt)
        {
            if (budgetMs > 0
                && wallClock.ElapsedMilliseconds >= budgetMs
                && !callerCt.IsCancellationRequested
                && !timeoutCts.IsCancellationRequested)
            {
                timeoutCts.Cancel();
            }
        }

        private TimeSpan ApplyBudgetMultiplier(TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero)
            {
                return budget;
            }

            float multiplier = GenerationTimeBudgetMultiplier > 0f ? GenerationTimeBudgetMultiplier : 1f;
            return TimeSpan.FromMilliseconds(budget.TotalMilliseconds * multiplier);
        }

        private static TimeSpan ResolvePlacementStageTimeout(TimeSpan generationTimeout)
        {
            if (generationTimeout <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            // Per-seed placement cap: abandon a pathological seed quickly so the next one is tried within the
            // mission budget. 0.4 of the budget keeps ~2 retries: at the authored 5s budget this is ~2000ms
            // (the problem-seed test relies on the bad seed being dropped fast), while the runtime's 30s cap
            // gives weaker hardware proportionally more time per seed (~12000ms). No hard upper cap here so
            // it scales with the resolved budget rather than starving long-budget runs.
            double milliseconds = generationTimeout.TotalMilliseconds;
            double stageMilliseconds = Math.Max(500.0, milliseconds * 0.4);
            return TimeSpan.FromMilliseconds(stageMilliseconds);
        }

        private static void CancelAfterIfPositive(CancellationTokenSource cts, TimeSpan timeout)
        {
            if (cts != null && timeout > TimeSpan.Zero)
            {
                cts.CancelAfter(timeout);
            }
        }

        private static bool IsPlacementStageTimeout(
            CancellationTokenSource placementStageCts,
            CancellationToken generationCt,
            CancellationToken callerCt)
        {
            return placementStageCts != null
                   && placementStageCts.IsCancellationRequested
                   && !generationCt.IsCancellationRequested
                   && !callerCt.IsCancellationRequested;
        }

        private static LevelValidationResult CreatePlacementStageTimeoutValidation(
            int seed,
            LayoutRetryStage retryStage,
            TimeSpan timeout)
        {
            return new LevelValidationResult(new[]
            {
                new LevelValidationIssue(
                    "PlacementStageTimedOut",
                    $"Placement for seed {seed}, stage {retryStage} exceeded {timeout.TotalMilliseconds:0}ms and was retried with the next seed.",
                    LevelValidationSeverity.Warning)
            });
        }

        private sealed class StopwatchRoomPlacementBudget : IRoomPlacementBudget
        {
            private readonly System.Diagnostics.Stopwatch _stopwatch;
            private readonly TimeSpan _timeout;
            private readonly CancellationTokenSource _placementStageCts;
            private readonly CancellationToken _generationCt;
            private readonly CancellationToken _callerCt;

            public StopwatchRoomPlacementBudget(
                System.Diagnostics.Stopwatch stopwatch,
                TimeSpan timeout,
                CancellationTokenSource placementStageCts,
                CancellationToken generationCt,
                CancellationToken callerCt)
            {
                _stopwatch = stopwatch;
                _timeout = timeout;
                _placementStageCts = placementStageCts;
                _generationCt = generationCt;
                _callerCt = callerCt;
            }

            public void ThrowIfExpired()
            {
                _callerCt.ThrowIfCancellationRequested();
                _generationCt.ThrowIfCancellationRequested();
                if (_timeout > TimeSpan.Zero
                    && _stopwatch.Elapsed >= _timeout
                    && !_placementStageCts.IsCancellationRequested)
                {
                    _placementStageCts.Cancel();
                }

                _placementStageCts.Token.ThrowIfCancellationRequested();
            }
        }

        private async Task YieldAfterRejectedStageAsync(CancellationToken ct)
        {
            CollectAfterRejectedStage();
            await YieldAsync(ct);
        }

        private void EnterGeneration()
        {
            if (Interlocked.CompareExchange(ref _isGenerating, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "This MissionGenerator instance is already generating. Create a second instance for concurrent generation requests.");
            }
        }

        private void ExitGeneration() => Interlocked.Exchange(ref _isGenerating, 0);

        private void LogDiagnostic(string message)
        {
            if (!DiagnosticLogsEnabled || string.IsNullOrEmpty(message))
            {
                return;
            }

            DiagnosticLogSink?.Invoke($"[LevelGeneration] {message}");
        }

        private static LevelValidationResult ValidateLayout(
            ILevelValidator validator,
            LevelLayout layout,
            LevelGraph graph,
            IMissionConfigView config,
            ITilesetView tileset)
        {
            return validator is IGraphAwareLevelValidator graphAware
                ? graphAware.Validate(layout, graph, config, tileset)
                : validator.Validate(layout, config, tileset);
        }

        private static LevelValidationResult ValidateSelectionCoverage(
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            LevelGraph graph)
        {
            List<LevelValidationIssue> issues = new();
            if (graph?.Nodes == null)
            {
                return LevelValidationResult.Empty;
            }

            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                string nodeId = graph.Nodes[i].NodeId;
                if (selections == null
                    || !selections.TryGetValue(nodeId, out IRoomTemplateDescriptor template)
                    || template == null)
                {
                    issues.Add(new LevelValidationIssue(
                        "GraphNodeMissingSelection",
                        $"Graph node '{nodeId}' has no room template selected.",
                        LevelValidationSeverity.Error));
                }
            }

            return issues.Count == 0 ? LevelValidationResult.Empty : new LevelValidationResult(issues);
        }

        private static LevelValidationResult ValidatePlacementCoverage(LevelLayout layout, LevelGraph graph)
        {
            List<LevelValidationIssue> issues = new();
            if (layout == null)
            {
                issues.Add(new LevelValidationIssue(
                    "PlacementFailed",
                    "Room placer returned null layout.",
                    LevelValidationSeverity.Error));
                return new LevelValidationResult(issues);
            }

            if (graph == null)
            {
                return LevelValidationResult.Empty;
            }

            HashSet<string> placedNodes = new(StringComparer.Ordinal);
            if (layout.Rooms != null)
            {
                for (int i = 0; i < layout.Rooms.Count; i++)
                {
                    if (!string.IsNullOrEmpty(layout.Rooms[i]?.NodeId))
                    {
                        placedNodes.Add(layout.Rooms[i].NodeId);
                    }
                }
            }

            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                string nodeId = graph.Nodes[i].NodeId;
                if (!placedNodes.Contains(nodeId))
                {
                    issues.Add(new LevelValidationIssue(
                        "GraphNodeNotPlaced",
                        $"Graph node '{nodeId}' was not placed by the room placer.",
                        LevelValidationSeverity.Error));
                }
            }

            return issues.Count == 0 ? LevelValidationResult.Empty : new LevelValidationResult(issues);
        }

        private LevelValidationResult ValidateMazeBranchAnswerTopology(
            LevelLayout layout,
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IMissionConfigView config)
        {
            if (layout?.Rooms == null
                || layout.Connections == null
                || graph?.Nodes == null
                || selections == null
                || config == null)
            {
                return LevelValidationResult.Empty;
            }

            Dictionary<string, RoomNode> nodesById = new(graph.Nodes.Count, StringComparer.Ordinal);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                if (node != null && !string.IsNullOrEmpty(node.NodeId))
                {
                    nodesById[node.NodeId] = node;
                }
            }

            List<LevelValidationIssue> issues = new();
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (room == null
                    || !selections.TryGetValue(room.NodeId, out IRoomTemplateDescriptor template)
                    || template == null
                    || !InteractiveMissionContentMatcher.HasDoorPerOptionAnswers(_interactiveRoomKinds, template))
                {
                    continue;
                }

                string contentId = ResolvePlacedContentId(room, nodesById);
                if (!InteractiveMissionContentMatcher.TryFindContentSpec(config, contentId,
                        out IInteractiveMissionContentSpec content)
                    || !content.IsMandatory
                    || !InteractiveMissionContentMatcher.TryResolveDoorBranchOptionCount(
                        _interactiveRoomKinds, content, out _))
                {
                    continue;
                }

                List<string> answerDoorIds = CollectLockedAnswerDoorIds(template);
                if (answerDoorIds.Count != content.OptionCount)
                {
                    issues.Add(new LevelValidationIssue(
                        "MazeBranchAnswerDoorCountMismatch",
                        $"MazeBranch room '{room.NodeId}' has {answerDoorIds.Count} locked answer doors for {content.OptionCount} options.",
                        LevelValidationSeverity.Error));
                    continue;
                }

                int mainPathAnswerConnections = 0;
                for (int doorIndex = 0; doorIndex < answerDoorIds.Count; doorIndex++)
                {
                    string socketId = answerDoorIds[doorIndex];
                    if (!TryFindDoorConnection(layout.Connections, room.NodeId, socketId,
                            out DoorConnection connection, out string oppositeNodeId))
                    {
                        // A wrong-answer door that could not be capped with a dead-end room is sealed to
                        // Disabled by DeadEndRoomPostProcessor — an acceptable sealed wrong answer (a wall),
                        // best-effort и НЕ отклоняющее сид (доля disabled-дверей только диагностируется,
                        // см. LogDisabledDoorDiagnostic). Only a still-unsealed disconnected door is a real
                        // door-to-void defect. The mandatory correct path is still enforced below via the
                        // single main-path answer connection requirement.
                        bool sealedWall = room.DoorStates != null
                                          && room.DoorStates.TryGetValue(socketId, out DoorState doorState)
                                          && doorState == DoorState.Disabled;
                        if (!sealedWall)
                        {
                            issues.Add(new LevelValidationIssue(
                                "MazeBranchAnswerDoorDisconnected",
                                $"MazeBranch room '{room.NodeId}' answer door '{socketId}' is not connected and not sealed.",
                                LevelValidationSeverity.Error));
                        }

                        continue;
                    }

                    nodesById.TryGetValue(oppositeNodeId, out RoomNode oppositeNode);
                    if (connection.IsMainPath)
                    {
                        mainPathAnswerConnections++;
                        if (oppositeNode != null && oppositeNode.IsDeadEnd)
                        {
                            issues.Add(new LevelValidationIssue(
                                "MazeBranchCorrectAnswerDeadEnds",
                                $"MazeBranch room '{room.NodeId}' main-path answer door '{socketId}' leads to dead-end node '{oppositeNodeId}'.",
                                LevelValidationSeverity.Error));
                        }
                    }
                    else if (oppositeNode == null || !oppositeNode.IsDeadEnd)
                    {
                        issues.Add(new LevelValidationIssue(
                            "MazeBranchWrongAnswerNotDeadEnd",
                            $"MazeBranch room '{room.NodeId}' wrong answer door '{socketId}' leads to non-dead-end node '{oppositeNodeId}'.",
                            LevelValidationSeverity.Error));
                    }
                }

                if (mainPathAnswerConnections != 1)
                {
                    issues.Add(new LevelValidationIssue(
                        "MazeBranchCorrectAnswerPathMissing",
                        $"MazeBranch room '{room.NodeId}' must have exactly one main-path answer door; found {mainPathAnswerConnections}.",
                        LevelValidationSeverity.Error));
                }
            }

            return issues.Count == 0 ? LevelValidationResult.Empty : new LevelValidationResult(issues);
        }

        // Свободные disabled-двери — НЕ ошибка генерации. Dead-end-проход (DeadEndRoomPostProcessor) пытается
        // закрыть каждую свободную дверь тупиковой комнатой, перебирая все доступные dead-end шаблоны; дверь,
        // которую закрыть не удалось, запечатывается в стену (а не «дверь в пустоту»). Остаточные запечатанные
        // двери допустимы как best-effort (ср. maze-deadends-best-effort), поэтому сид по их доле НЕ отклоняется
        // — раньше отклонение загоняло плотные миссии (Procedural_Tunnels) в retry-loop до 30s таймаута.
        // Долю disabled-дверей только диагностируем.
        private void LogDisabledDoorDiagnostic(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            DeadEndRoomPostProcessResult deadEnds,
            int attemptNumber,
            LayoutRetryStage stage)
        {
            if (!DiagnosticLogsEnabled)
            {
                return;
            }

            CountDisabledDoors(layout, selections, out int connectable, out int disabled);
            double ratio = connectable > 0 ? (double)disabled / connectable : 0.0;
            LogDiagnostic(
                $"Attempt {attemptNumber}, stage {stage} dead-end capping (non-fatal): capped "
                + $"{deadEnds?.AddedRoomCount ?? 0} free door(s) with dead-end rooms, sealed "
                + $"{deadEnds?.SealedCount ?? 0} into walls; {disabled} of {connectable} connectable doors "
                + $"remain Disabled ({ratio:P0}).");
        }

        private static void CountDisabledDoors(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            out int connectable,
            out int disabled)
        {
            connectable = 0;
            disabled = 0;
            if (layout?.Rooms == null || layout.Rooms.Count == 0 || selections == null)
            {
                return;
            }

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (room == null
                    || !selections.TryGetValue(room.NodeId, out IRoomTemplateDescriptor template)
                    || template?.Doors == null)
                {
                    continue;
                }

                for (int d = 0; d < template.Doors.Count; d++)
                {
                    IDoorSocketDescriptor door = template.Doors[d];
                    // Only doors that participate in generation (connectable) count; decorative / non-connectable
                    // sockets never take part, so they are excluded from the ratio.
                    if (door == null || !door.IsConnectable())
                    {
                        continue;
                    }

                    connectable++;
                    if (room.DoorStates != null
                        && room.DoorStates.TryGetValue(door.SocketId, out DoorState state)
                        && state == DoorState.Disabled)
                    {
                        disabled++;
                    }
                }
            }
        }

        private static string ResolvePlacedContentId(
            PlacedRoom room,
            IReadOnlyDictionary<string, RoomNode> nodesById)
        {
            if (!string.IsNullOrWhiteSpace(room.ContentId))
            {
                return room.ContentId;
            }

            return nodesById != null
                   && nodesById.TryGetValue(room.NodeId, out RoomNode node)
                ? node.ContentId
                : null;
        }

        private static List<string> CollectLockedAnswerDoorIds(IRoomTemplateDescriptor template)
        {
            List<string> result = new();
            if (template?.Doors == null)
            {
                return result;
            }

            for (int i = 0; i < template.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = template.Doors[i];
                if (door != null
                    && door.IsConnectable()
                    && door is IDoorSocketInitialStateDescriptor state
                    && state.InitialState == DoorState.Locked)
                {
                    result.Add(door.SocketId);
                }
            }

            return result;
        }

        private static bool TryFindDoorConnection(
            IReadOnlyList<DoorConnection> connections,
            string nodeId,
            string socketId,
            out DoorConnection connection,
            out string oppositeNodeId)
        {
            for (int i = 0; i < connections.Count; i++)
            {
                DoorConnection candidate = connections[i];
                if (string.Equals(candidate.FromNodeId, nodeId, StringComparison.Ordinal)
                    && string.Equals(candidate.FromSocketId, socketId, StringComparison.Ordinal))
                {
                    connection = candidate;
                    oppositeNodeId = candidate.ToNodeId;
                    return true;
                }

                if (string.Equals(candidate.ToNodeId, nodeId, StringComparison.Ordinal)
                    && string.Equals(candidate.ToSocketId, socketId, StringComparison.Ordinal))
                {
                    connection = candidate;
                    oppositeNodeId = candidate.FromNodeId;
                    return true;
                }
            }

            connection = default;
            oppositeNodeId = null;
            return false;
        }

        private static bool TryResolveLayoutPacingConfig(
            IMissionConfigView config,
            out LayoutPacingConfig layoutPacingConfig)
        {
            layoutPacingConfig = null;
            if (config is not ILayoutPacingConfigProvider provider || !provider.UseLayoutPacingGeneratorV2)
            {
                return false;
            }

            layoutPacingConfig = provider.ResolveLayoutPacingConfig();
            return layoutPacingConfig != null;
        }

        private LevelGraph BuildLayoutPacingV2Graph(
            IMissionConfigView config,
            LayoutPacingConfig layoutPacingConfig,
            LayoutRetryStage retryStage,
            int seed,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LayoutPacingMissionGenerator generator = new();
            LayoutPacingConfig stageConfig =
                CreateSingleStageConfig(new LayoutRetryPlanner().Apply(layoutPacingConfig, retryStage));
            LayoutGenerationResult result = generator.Generate(new LayoutGenerationRequest(
                seed,
                stageConfig,
                CreateMissionContentPlan(config)));

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Layout Pacing V2 graph failed: {result.FailureReason}, stage={result.RetryStage}, elapsed={result.ElapsedMilliseconds}ms.");
            }

            ct.ThrowIfCancellationRequested();
            return PacingSocketPlacementRunner.ToLevelGraph(result.Graph);
        }

        private static IReadOnlyList<LayoutRetryStage> GetLayoutPacingRetryStages(LayoutRetryPolicy retryPolicy)
        {
            // Single source of truth for the retry-stage ladder, shared with LayoutPacingMissionGenerator.
            // The ladder is intentionally identical across all platforms (no #if UNITY_WEBGL fork) so a
            // given seed produces the same layout on WebGL, the editor and standalone. WebGL performance is
            // bounded by the generation time budget (GenerationTimeBudgetPolicy / CancelAfter), not by a
            // reduced stage set, so dropping the platform fork is intentional and keeps generation
            // deterministic cross-platform.
            return LayoutPacingMissionGenerator.ResolveRetryStages(retryPolicy);
        }

        private void CollectAfterRejectedStage()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // Forcing a synchronous GC.Collect on the single-threaded WebGL main loop stalls the frame.
            // Doing it after every rejected stage compounded into multi-second freezes under the tight
            // generation budget, so only reclaim periodically on long failing runs.
            if (++_rejectedStagesSinceGc >= WebGlGcEveryRejectedStages)
            {
                _rejectedStagesSinceGc = 0;
                GC.Collect();
            }
#endif
        }

        private static LayoutPacingConfig CreateSingleStageConfig(LayoutPacingConfig config)
        {
            return new LayoutPacingConfig(
                config.MainPathGapRooms,
                config.SideBranchCount,
                config.SideBranchLengthRooms,
                config.MaxOptionalInteractiveBranches,
                config.GenerationTimeBudgetSeconds,
                LayoutRetryPolicy.FailFast,
                config.OptionalFillerCap);
        }

        private MissionContentPlan CreateMissionContentPlan(IMissionConfigView config)
        {
            IReadOnlyList<IInteractiveMissionContentSpec> content =
                config is IPlannedInteractiveMissionContentProvider planned
                    ? planned.PlannedInteractiveContent
                    : config is IInteractiveMissionContentProvider provider
                        ? provider.InteractiveContent
                        : Array.Empty<IInteractiveMissionContentSpec>();

            List<PlannedInteractiveContent> mandatory = new();
            List<PlannedInteractiveContent> optional = new();
            for (int i = 0; i < content.Count; i++)
            {
                IInteractiveMissionContentSpec spec = content[i];
                if (spec == null || string.IsNullOrWhiteSpace(spec.ContentId))
                {
                    continue;
                }

                bool doorPerOption =
                    InteractiveMissionContentMatcher.TryResolveContentRule(
                        _interactiveRoomKinds, spec, out IInteractiveRoomKindRule rule)
                    && rule.AnswerLayout == InteractiveRoomAnswerLayout.DoorPerOption;
                PlannedInteractiveContent plannedContent = new(
                    spec.ContentId,
                    rule?.Kind ?? spec.InteractiveKind,
                    spec.OptionCount,
                    spec.CorrectAnswerCount,
                    spec.IsMandatory,
                    doorPerOption ? AnswerBranchPolicy.CorrectContinuesWrongTerminal : AnswerBranchPolicy.None);

                if (spec.IsMandatory)
                {
                    mandatory.Add(plannedContent);
                }
                else
                {
                    optional.Add(plannedContent);
                }
            }

            return new MissionContentPlan(mandatory, optional);
        }

        private Dictionary<string, IRoomTemplateDescriptor> SelectRooms(
            LevelGraph graph,
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase,
            IDeterministicRng rng,
            bool allowV2Fallback = false)
        {
            Dictionary<string, IRoomTemplateDescriptor> result = new(graph.Nodes.Count);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                IRoomTemplateDescriptor template = _roomSelector.Select(node, config, tileset, roomDatabase, rng);
                if (template == null && allowV2Fallback)
                {
                    template = SelectBestEffortV2Room(node, config, tileset, roomDatabase);
                }

                if (template != null)
                {
                    result[node.NodeId] = template;
                }
            }

            return result;
        }

        private IRoomTemplateDescriptor SelectBestEffortV2Room(
            RoomNode node,
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase)
        {
            IReadOnlyList<IRoomTemplateDescriptor> pool = roomDatabase.GetRoomsForTileset(tileset.TilesetId);
            IInteractiveMissionContentSpec contentSpec = null;
            bool requiresContent = !string.IsNullOrWhiteSpace(node.ContentId);
            if (requiresContent &&
                !InteractiveMissionContentMatcher.TryFindContentSpec(config, node.ContentId, out contentSpec))
            {
                return null;
            }

            IRoomTemplateDescriptor best = null;
            for (int i = 0; i < pool.Count; i++)
            {
                IRoomTemplateDescriptor room = pool[i];
                if (room == null || room.Kind != node.Kind)
                {
                    continue;
                }

                if (requiresContent
                    && !InteractiveMissionContentMatcher.IsCompatible(_interactiveRoomKinds, room, contentSpec))
                {
                    continue;
                }

                if (node.Kind == RoomKind.Start && !room.CanBeStart)
                {
                    continue;
                }

                if (node.Kind == RoomKind.Exit && !room.CanBeExit)
                {
                    continue;
                }

                if (CountConnectableDoors(room) < node.RequiredDoorCount)
                {
                    continue;
                }

                if (best == null || room.Weight > best.Weight)
                {
                    best = room;
                }
            }

            return best;
        }

        private static int CountConnectableDoors(IRoomTemplateDescriptor room)
        {
            if (room?.Doors == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < room.Doors.Count; i++)
            {
                if (room.Doors[i].IsConnectable())
                {
                    count++;
                }
            }

            return count;
        }

        private static IReadOnlyList<IRoomTemplateDescriptor> BuildDeadEndRoomPool(
            IRoomDatabase roomDatabase,
            ITilesetView tileset)
        {
            IReadOnlyList<IRoomTemplateDescriptor> source = roomDatabase?.GetRoomsForTileset(tileset.TilesetId)
                                                            ?? Array.Empty<IRoomTemplateDescriptor>();
            List<IRoomTemplateDescriptor> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(new RoomTemplateDescriptorSnapshot(source[i]));
                }
            }

            return result;
        }

        public static IMissionConfigView CreateInteractiveContentConfig(
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase,
            IInteractiveRoomKindRules interactiveRoomKinds,
            ICollection<string> warnings = null)
        {
            if (interactiveRoomKinds == null)
            {
                throw new ArgumentNullException(nameof(interactiveRoomKinds));
            }

            if (config is not IInteractiveMissionContentProvider provider
                || provider.InteractiveContent == null
                || provider.InteractiveContent.Count == 0)
            {
                return config;
            }

            IReadOnlyList<IRoomTemplateDescriptor> pool = ResolveMissionPool(
                config,
                roomDatabase.GetRoomsForTileset(tileset.TilesetId),
                roomDatabase.AllRooms);
            List<IInteractiveMissionContentSpec> supported = new(provider.InteractiveContent.Count);
            for (int i = 0; i < provider.InteractiveContent.Count; i++)
            {
                IInteractiveMissionContentSpec content = provider.InteractiveContent[i];
                if (content == null || string.IsNullOrWhiteSpace(content.ContentId))
                {
                    continue;
                }

                if (!InteractiveMissionContentMatcher.TryResolveContentRule(interactiveRoomKinds, content, out IInteractiveRoomKindRule rule))
                {
                    warnings?.Add(
                        $"Interactive content '{content.ContentId}' skipped: unknown interactive kind " +
                        $"'{content.InteractiveKind}' (no registered interactive room type).");
                    continue;
                }

                if (HasCompatibleInteractiveRoom(interactiveRoomKinds, pool, content))
                {
                    supported.Add(content);
                    continue;
                }

                string defect = $"Interactive content '{content.ContentId}' ({content.InteractiveKind}) has no compatible room template in tileset '{tileset.TilesetId}'.";
                if (content.IsMandatory || rule.RequiresTemplate)
                {
                    throw new InvalidOperationException(defect + " This content must not be skipped; add a room template of this registered kind to the mission pool.");
                }

                warnings?.Add(defect + " Optional content skipped.");
            }

            return new InteractiveContentConfigView(config, tileset, supported, interactiveRoomKinds);
        }

        private IMissionConfigView PrepareInteractiveContentConfig(
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase) =>
            CreateInteractiveContentConfig(config, tileset, roomDatabase, _interactiveRoomKinds, _lastWarnings);

        private static IReadOnlyList<IRoomTemplateDescriptor> ResolveMissionPool(
            IMissionConfigView config,
            IReadOnlyList<IRoomTemplateDescriptor> tilesetPool,
            IReadOnlyList<IRoomTemplateDescriptor> allRooms)
        {
            IReadOnlyList<IMissionRoomRule> rules = config?.MissionRoomRules;
            if (rules == null || rules.Count == 0)
            {
                return tilesetPool ?? Array.Empty<IRoomTemplateDescriptor>();
            }

            HashSet<string> allowed = new(StringComparer.Ordinal);
            for (int i = 0; i < rules.Count; i++)
            {
                if (!string.IsNullOrEmpty(rules[i]?.RoomId))
                {
                    allowed.Add(rules[i].RoomId);
                }
            }

            if (allowed.Count == 0)
            {
                return tilesetPool ?? Array.Empty<IRoomTemplateDescriptor>();
            }

            List<IRoomTemplateDescriptor> result = new(allowed.Count);
            AddAllowedRooms(result, tilesetPool, allowed);
            return result.Count > 0 ? result : tilesetPool ?? Array.Empty<IRoomTemplateDescriptor>();
        }

        private static void AddAllowedRooms(
            List<IRoomTemplateDescriptor> target,
            IReadOnlyList<IRoomTemplateDescriptor> source,
            ISet<string> allowed)
        {
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                IRoomTemplateDescriptor room = source[i];
                if (room != null && allowed.Contains(room.RoomId) && !ContainsRoom(target, room.RoomId))
                {
                    target.Add(room);
                }
            }
        }

        private static bool ContainsRoom(IReadOnlyList<IRoomTemplateDescriptor> rooms, string roomId)
        {
            if (rooms == null || string.IsNullOrWhiteSpace(roomId))
            {
                return false;
            }

            for (int i = 0; i < rooms.Count; i++)
            {
                if (string.Equals(rooms[i]?.RoomId, roomId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCompatibleInteractiveRoom(
            IInteractiveRoomKindRules interactiveRoomKinds,
            IReadOnlyList<IRoomTemplateDescriptor> rooms,
            IInteractiveMissionContentSpec content)
        {
            if (rooms == null)
            {
                return false;
            }

            for (int i = 0; i < rooms.Count; i++)
            {
                IRoomTemplateDescriptor room = rooms[i];
                if (room != null
                    && room.CanBeMainPath
                    && InteractiveMissionContentMatcher.IsCompatible(interactiveRoomKinds, room, content))
                {
                    return true;
                }
            }

            return false;
        }

        private void ApplyDependencyHints(
            LevelGraph graph,
            Dictionary<string, IRoomTemplateDescriptor> selections,
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase)
        {
            if (_hintExtractors.Count == 0 || graph == null || selections == null || roomDatabase == null)
            {
                return;
            }

            IReadOnlyList<RoomDependencyHint> hints = ExtractHints(tileset, roomDatabase);
            if (hints.Count == 0)
            {
                return;
            }

            IReadOnlyList<IRoomTemplateDescriptor> tilesetRooms = roomDatabase.GetRoomsForTileset(tileset.TilesetId);
            Dictionary<string, IRoomTemplateDescriptor> roomsById = BuildRoomLookup(tilesetRooms);
            EffectiveRoomWeightResolver weightResolver = BuildWeightResolver(config);

            for (int i = 0; i < hints.Count; i++)
            {
                RoomDependencyHint hint = hints[i];
                if (!hint.IsValid)
                {
                    continue;
                }

                if (!roomsById.TryGetValue(hint.SourceRoomTemplateId, out IRoomTemplateDescriptor sourceTemplate))
                {
                    continue;
                }

                foreach (string target in FindSelectedNodes(selections, hint.TargetRoomTemplateId))
                {
                    string sourceNodeId = FindBestSourceNode(graph, selections, target, sourceTemplate.Kind, hint);
                    if (string.IsNullOrEmpty(sourceNodeId)
                        || !TryFindNode(graph, sourceNodeId, out RoomNode sourceNode)
                        || !IsDependencySourceCompatible(
                            sourceNode,
                            sourceTemplate,
                            tilesetRooms,
                            weightResolver))
                    {
                        continue;
                    }

                    selections[sourceNodeId] = sourceTemplate;
                }
            }
        }

        private IReadOnlyList<RoomDependencyHint> ExtractHints(ITilesetView tileset, IRoomDatabase roomDatabase)
        {
            List<RoomDependencyHint> result = new();
            IReadOnlyList<IRoomTemplateDescriptor> rooms = roomDatabase?.AllRooms ??
                                                           Array.Empty<IRoomTemplateDescriptor>();
            for (int i = 0; i < _hintExtractors.Count; i++)
            {
                IReadOnlyList<RoomDependencyHint> extracted = _hintExtractors[i].Extract(rooms, tileset);
                if (extracted == null)
                {
                    continue;
                }

                for (int j = 0; j < extracted.Count; j++)
                {
                    result.Add(extracted[j]);
                }
            }

            return result;
        }

        private static Dictionary<string, IRoomTemplateDescriptor> BuildRoomLookup(
            IReadOnlyList<IRoomTemplateDescriptor> rooms)
        {
            Dictionary<string, IRoomTemplateDescriptor> result = new();
            if (rooms == null)
            {
                return result;
            }

            for (int i = 0; i < rooms.Count; i++)
            {
                IRoomTemplateDescriptor room = rooms[i];
                if (room == null || string.IsNullOrEmpty(room.RoomId))
                {
                    continue;
                }

                result[room.RoomId] = room;
            }

            return result;
        }

        private static List<string> FindSelectedNodes(
            Dictionary<string, IRoomTemplateDescriptor> selections,
            string roomTemplateId)
        {
            List<string> result = new();
            foreach (KeyValuePair<string, IRoomTemplateDescriptor> kv in selections)
            {
                if (kv.Value != null && kv.Value.RoomId == roomTemplateId)
                {
                    result.Add(kv.Key);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string FindBestSourceNode(
            LevelGraph graph,
            Dictionary<string, IRoomTemplateDescriptor> selections,
            string targetNodeId,
            RoomKind sourceKind,
            RoomDependencyHint hint)
        {
            Dictionary<string, int> distances = CalculateDistances(graph, targetNodeId);
            string bestInRange = null;
            int bestInRangeDistance = int.MaxValue;
            string bestShrunk = null;
            int bestShrunkDistance = -1;

            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                if (node.NodeId == targetNodeId || node.Kind != sourceKind)
                {
                    continue;
                }

                if (!distances.TryGetValue(node.NodeId, out int distance))
                {
                    continue;
                }

                if (distance > hint.MaxDistance)
                {
                    continue;
                }

                if (distance >= hint.MinDistance)
                {
                    if (distance < bestInRangeDistance
                        || (distance == bestInRangeDistance
                            && string.CompareOrdinal(node.NodeId, bestInRange) < 0))
                    {
                        bestInRange = node.NodeId;
                        bestInRangeDistance = distance;
                    }
                }
                else if (distance > bestShrunkDistance
                         || (distance == bestShrunkDistance
                             && string.CompareOrdinal(node.NodeId, bestShrunk) < 0))
                {
                    bestShrunk = node.NodeId;
                    bestShrunkDistance = distance;
                }
            }

            return bestInRange ?? bestShrunk;
        }

        private static Dictionary<string, int> CalculateDistances(LevelGraph graph, string startNodeId)
        {
            Dictionary<string, List<string>> adjacency = new();
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                AddEdge(adjacency, edge.FromNodeId, edge.ToNodeId);
                AddEdge(adjacency, edge.ToNodeId, edge.FromNodeId);
            }

            Dictionary<string, int> distances = new() { [startNodeId] = 0 };
            Queue<string> queue = new();
            queue.Enqueue(startNodeId);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out List<string> neighbors))
                {
                    continue;
                }

                for (int i = 0; i < neighbors.Count; i++)
                {
                    string next = neighbors[i];
                    if (distances.ContainsKey(next))
                    {
                        continue;
                    }

                    distances[next] = distances[current] + 1;
                    queue.Enqueue(next);
                }
            }

            return distances;
        }

        private static void AddEdge(Dictionary<string, List<string>> adjacency, string a, string b)
        {
            if (!adjacency.TryGetValue(a, out List<string> list))
            {
                list = new List<string>();
                adjacency[a] = list;
            }

            list.Add(b);
        }

        private static bool TryFindNode(LevelGraph graph, string nodeId, out RoomNode node)
        {
            if (graph?.Nodes != null)
            {
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    if (string.Equals(graph.Nodes[i].NodeId, nodeId, StringComparison.Ordinal))
                    {
                        node = graph.Nodes[i];
                        return true;
                    }
                }
            }

            node = null;
            return false;
        }

        private static bool IsDependencySourceCompatible(
            RoomNode node,
            IRoomTemplateDescriptor template,
            IReadOnlyList<IRoomTemplateDescriptor> tilesetRooms,
            EffectiveRoomWeightResolver weightResolver)
        {
            if (node == null || template == null)
            {
                return false;
            }

            if (!ContainsRoom(tilesetRooms, template.RoomId))
            {
                return false;
            }

            if (template.Kind != node.Kind)
            {
                return false;
            }

            if (node.IsDeadEnd && !template.CanBeDeadEnd)
            {
                return false;
            }

            if (node.IsOnMainPath && !template.CanBeMainPath)
            {
                return false;
            }

            if (!node.IsOnMainPath && !template.CanBeSideBranch)
            {
                return false;
            }

            if (!HasEnoughConnectableDoors(template, node.RequiredDoorCount))
            {
                return false;
            }

            return weightResolver == null || weightResolver.Resolve(template) > 0;
        }

        private static bool HasEnoughConnectableDoors(IRoomTemplateDescriptor room, int requiredDoorCount)
        {
            if (requiredDoorCount <= 0)
            {
                return true;
            }

            if (room?.Doors == null || room.Doors.Count == 0)
            {
                return false;
            }

            int count = 0;
            for (int i = 0; i < room.Doors.Count; i++)
            {
                if (room.Doors[i] != null && room.Doors[i].IsConnectable())
                {
                    count++;
                }
            }

            return count >= requiredDoorCount;
        }

        private static EffectiveRoomWeightResolver BuildWeightResolver(IMissionConfigView config)
        {
            if (config == null)
            {
                return new EffectiveRoomWeightResolver();
            }

            Dictionary<string, int> missionRoomOverrides = null;
            if (config.MissionRoomRules != null && config.MissionRoomRules.Count > 0)
            {
                missionRoomOverrides =
                    new Dictionary<string, int>(config.MissionRoomRules.Count, StringComparer.Ordinal);
                for (int i = 0; i < config.MissionRoomRules.Count; i++)
                {
                    IMissionRoomRule rule = config.MissionRoomRules[i];
                    if (rule == null || string.IsNullOrEmpty(rule.RoomId))
                    {
                        continue;
                    }

                    missionRoomOverrides[rule.RoomId] = rule.OverrideWeight ? rule.Weight : 100;
                }
            }

            if (config.RoomWeightOverrides != null && config.RoomWeightOverrides.Count > 0)
            {
                missionRoomOverrides ??=
                    new Dictionary<string, int>(config.RoomWeightOverrides.Count, StringComparer.Ordinal);
                for (int i = 0; i < config.RoomWeightOverrides.Count; i++)
                {
                    IRoomWeightOverride o = config.RoomWeightOverrides[i];
                    if (o == null || string.IsNullOrEmpty(o.RoomId))
                    {
                        continue;
                    }

                    missionRoomOverrides[o.RoomId] = o.Weight;
                }
            }

            Dictionary<RoomKind, int> missionKindOverrides = null;
            if (config.RoomKindWeightOverrides != null && config.RoomKindWeightOverrides.Count > 0)
            {
                missionKindOverrides = new Dictionary<RoomKind, int>(config.RoomKindWeightOverrides.Count);
                for (int i = 0; i < config.RoomKindWeightOverrides.Count; i++)
                {
                    IRoomKindWeightOverride o = config.RoomKindWeightOverrides[i];
                    if (o == null)
                    {
                        continue;
                    }

                    missionKindOverrides[o.Kind] = o.Weight;
                }
            }

            return new EffectiveRoomWeightResolver(missionRoomOverrides, missionKindOverrides);
        }

        public sealed class InteractiveContentConfigView : IMissionConfigView, IInteractiveMissionContentProvider,
            IPlannedInteractiveMissionContentProvider, IQuizBranchMissionConfigView
        {
            private const InteractiveRoomCountingMode CountingMode =
                InteractiveRoomCountingMode.BaseRoomsPlusInteractiveContent;

            private readonly IMissionConfigView _inner;
            private readonly ITilesetView _tileset;
            private readonly IInteractiveRoomKindRules _interactiveRoomKinds;

            public InteractiveContentConfigView(
                IMissionConfigView inner,
                ITilesetView tileset,
                IReadOnlyList<IInteractiveMissionContentSpec> interactiveContent,
                IInteractiveRoomKindRules interactiveRoomKinds)
            {
                _inner = inner;
                _tileset = tileset;
                _interactiveRoomKinds = interactiveRoomKinds ?? InteractiveRoomKindRules.Empty;
                InteractiveContent = interactiveContent ?? Array.Empty<IInteractiveMissionContentSpec>();
            }

            public IReadOnlyList<IInteractiveMissionContentSpec> InteractiveContent { get; }
            public IReadOnlyList<IInteractiveMissionContentSpec> PlannedInteractiveContent => InteractiveContent;

            public int QuizBranchAnswerCount => _inner is IQuizBranchMissionConfigView quiz
                ? quiz.QuizBranchAnswerCount
                : FirstDoorBranchOptionCount();

            public string MissionId => _inner.MissionId;
            public string TilesetId => _inner.TilesetId;
            public MissionArchetypeKind ArchetypeKind => _inner.ArchetypeKind;
            public int Difficulty => _inner.Difficulty;
            public OptionalIntRange TotalRoomsRange => ResolveTotalRoomsRange();
            public OptionalIntRange MainPathRoomsRange => _inner.MainPathRoomsRange;
            public OptionalIntRange BranchingRange => _inner.BranchingRange;
            public OptionalIntRange SideBranchesRange => _inner.SideBranchesRange;
            public OptionalIntRange DeadEndsRange => _inner.DeadEndsRange;
            public bool AllowLoops => _inner.AllowLoops;
            public OptionalFloat BonusRoomChance => _inner.BonusRoomChance;
            public OptionalFloat SecretRoomChance => _inner.SecretRoomChance;
            public OptionalFloat LogicRoomShare => _inner.LogicRoomShare;
            public ExtractionMode ExtractionMode => _inner.ExtractionMode;
            public int ExtractionRoomsCount => _inner.ExtractionRoomsCount;
            public ExtractionAvailability ExtractionAvailability => _inner.ExtractionAvailability;
            public float ExtractionUnlockDelaySeconds => _inner.ExtractionUnlockDelaySeconds;
            public IReadOnlyList<RoomKind> RequiredRoomKinds => _inner.RequiredRoomKinds;
            public IReadOnlyList<RoomKind> OptionalRoomKinds => _inner.OptionalRoomKinds;
            public IReadOnlyList<IMissionRoomRule> MissionRoomRules => _inner.MissionRoomRules;
            public IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => _inner.RoomKindSpawnRules;
            public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => _inner.RoomWeightOverrides;
            public IReadOnlyList<IRoomKindWeightOverride> RoomKindWeightOverrides => _inner.RoomKindWeightOverrides;
            public LevelGenerationMode GenerationMode => _inner.GenerationMode;
            public IReadOnlyList<IHybridSequenceStep> HybridSequence => _inner.HybridSequence;

            private int FirstDoorBranchOptionCount()
            {
                for (int i = 0; i < InteractiveContent.Count; i++)
                {
                    IInteractiveMissionContentSpec content = InteractiveContent[i];
                    if (content != null
                        && InteractiveMissionContentMatcher.TryResolveContentRule(
                            _interactiveRoomKinds, content, out IInteractiveRoomKindRule rule)
                        && rule.AnswerLayout == InteractiveRoomAnswerLayout.DoorPerOption
                        && content.OptionCount > 0)
                    {
                        return content.OptionCount;
                    }
                }

                return 0;
            }

            private OptionalIntRange ResolveTotalRoomsRange()
            {
                OptionalIntRange range = _inner.TotalRoomsRange;
                if (InteractiveContent == null
                    || InteractiveContent.Count == 0
                    || CountingMode == InteractiveRoomCountingMode.TotalRoomsIncludesInteractiveContent)
                {
                    return range;
                }

                int contentRoomReserve = InteractiveContent.Count;
                if (!range.IsSet)
                {
                    if (_tileset == null)
                    {
                        return range;
                    }

                    return new OptionalIntRange(
                        _tileset.TotalRoomsRange.Min + contentRoomReserve,
                        _tileset.TotalRoomsRange.Max + contentRoomReserve);
                }

                return new OptionalIntRange(
                    range.Min + contentRoomReserve,
                    range.Max + contentRoomReserve);
            }

            private enum InteractiveRoomCountingMode
            {
                BaseRoomsPlusInteractiveContent,

                // TODO: expose this on MissionConfigSO if designers later want TotalRoomsRange to include
                // JSON interactive rooms instead of adding them on top of the base room count.
                TotalRoomsIncludesInteractiveContent
            }
        }

        private sealed class LayoutPacingValidationConfigView : IMissionConfigView
        {
            private readonly IMissionConfigView inner;
            private readonly OptionalIntRange totalRoomsRange;

            public LayoutPacingValidationConfigView(IMissionConfigView inner, int roomCount)
            {
                this.inner = inner;
                totalRoomsRange = new OptionalIntRange(roomCount, roomCount);
            }

            public string MissionId => inner.MissionId;
            public string TilesetId => inner.TilesetId;
            public MissionArchetypeKind ArchetypeKind => inner.ArchetypeKind;
            public int Difficulty => inner.Difficulty;
            public OptionalIntRange TotalRoomsRange => totalRoomsRange;
            public OptionalIntRange MainPathRoomsRange => inner.MainPathRoomsRange;
            public OptionalIntRange BranchingRange => inner.BranchingRange;
            public OptionalIntRange SideBranchesRange => inner.SideBranchesRange;
            public OptionalIntRange DeadEndsRange => inner.DeadEndsRange;
            public bool AllowLoops => inner.AllowLoops;
            public OptionalFloat BonusRoomChance => inner.BonusRoomChance;
            public OptionalFloat SecretRoomChance => inner.SecretRoomChance;
            public OptionalFloat LogicRoomShare => inner.LogicRoomShare;
            public ExtractionMode ExtractionMode => inner.ExtractionMode;
            public int ExtractionRoomsCount => inner.ExtractionRoomsCount;
            public ExtractionAvailability ExtractionAvailability => inner.ExtractionAvailability;
            public float ExtractionUnlockDelaySeconds => inner.ExtractionUnlockDelaySeconds;
            public IReadOnlyList<RoomKind> RequiredRoomKinds => inner.RequiredRoomKinds;
            public IReadOnlyList<RoomKind> OptionalRoomKinds => inner.OptionalRoomKinds;
            public IReadOnlyList<IMissionRoomRule> MissionRoomRules => inner.MissionRoomRules;
            public IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => inner.RoomKindSpawnRules;
            public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => inner.RoomWeightOverrides;
            public IReadOnlyList<IRoomKindWeightOverride> RoomKindWeightOverrides => inner.RoomKindWeightOverrides;
            public LevelGenerationMode GenerationMode => inner.GenerationMode;
            public IReadOnlyList<IHybridSequenceStep> HybridSequence => inner.HybridSequence;
        }

        private sealed class PostProcessedLayoutValidationConfigView : IMissionConfigView
        {
            private readonly IMissionConfigView inner;
            private readonly OptionalIntRange totalRoomsRange;

            public PostProcessedLayoutValidationConfigView(IMissionConfigView inner, int roomCount)
            {
                this.inner = inner;
                OptionalIntRange range = inner.TotalRoomsRange;
                totalRoomsRange = range.IsSet
                    ? new OptionalIntRange(range.Min, Math.Max(range.Max, roomCount))
                    : range;
            }

            public string MissionId => inner.MissionId;
            public string TilesetId => inner.TilesetId;
            public MissionArchetypeKind ArchetypeKind => inner.ArchetypeKind;
            public int Difficulty => inner.Difficulty;
            public OptionalIntRange TotalRoomsRange => totalRoomsRange;
            public OptionalIntRange MainPathRoomsRange => inner.MainPathRoomsRange;
            public OptionalIntRange BranchingRange => inner.BranchingRange;
            public OptionalIntRange SideBranchesRange => inner.SideBranchesRange;
            public OptionalIntRange DeadEndsRange => inner.DeadEndsRange;
            public bool AllowLoops => inner.AllowLoops;
            public OptionalFloat BonusRoomChance => inner.BonusRoomChance;
            public OptionalFloat SecretRoomChance => inner.SecretRoomChance;
            public OptionalFloat LogicRoomShare => inner.LogicRoomShare;
            public ExtractionMode ExtractionMode => inner.ExtractionMode;
            public int ExtractionRoomsCount => inner.ExtractionRoomsCount;
            public ExtractionAvailability ExtractionAvailability => inner.ExtractionAvailability;
            public float ExtractionUnlockDelaySeconds => inner.ExtractionUnlockDelaySeconds;
            public IReadOnlyList<RoomKind> RequiredRoomKinds => inner.RequiredRoomKinds;
            public IReadOnlyList<RoomKind> OptionalRoomKinds => inner.OptionalRoomKinds;
            public IReadOnlyList<IMissionRoomRule> MissionRoomRules => inner.MissionRoomRules;
            public IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => inner.RoomKindSpawnRules;
            public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => inner.RoomWeightOverrides;
            public IReadOnlyList<IRoomKindWeightOverride> RoomKindWeightOverrides => inner.RoomKindWeightOverrides;
            public LevelGenerationMode GenerationMode => inner.GenerationMode;
            public IReadOnlyList<IHybridSequenceStep> HybridSequence => inner.HybridSequence;
        }

        private static string FormatIssues(LevelValidationResult validation)
        {
            if (validation?.Issues == null || validation.Issues.Count == 0)
            {
                return "none";
            }

            string[] parts = new string[validation.Issues.Count];
            for (int i = 0; i < validation.Issues.Count; i++)
            {
                LevelValidationIssue issue = validation.Issues[i];
                parts[i] = $"{issue.Severity}:{issue.Code}:{issue.Message}";
            }

            return string.Join("; ", parts);
        }
    }
}
