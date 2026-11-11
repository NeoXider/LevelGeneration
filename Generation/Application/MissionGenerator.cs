using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RedoSchool.LevelGeneration.Generation.Application.DependencyHints;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Application.V2.Facade;
using RedoSchool.LevelGeneration.Generation.Application.V2.Placement;
using RedoSchool.LevelGeneration.Generation.Application.V2.Policies;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Часы одного прогона генерации (<see cref="MissionGenerator.GenerationClockFactory"/>). Служат ТОЛЬКО для
    /// прерывания всего прогона по общему бюджету времени — на то, какая раскладка получится у сида, не влияют.
    /// </summary>
    public interface IGenerationClock
    {
        /// <summary>Сколько прошло с начала прогона.</summary>
        public TimeSpan Elapsed { get; }
    }

    /// <summary>
    /// Facade for the level-generation pipeline. Builds a deterministic <see cref="LevelLayout"/>
    /// from a mission config + seed. The host game owns async scheduling and event publishing.
    /// <para>
    /// Детерминизм: какая раскладка получится у сида, решают только сид, конфигурация и СЧЁТНЫЕ бюджеты
    /// размещения (<see cref="SocketBacktrackingRoomPlacer.CandidateEvaluationBudget"/>). Исчерпанная стадия
    /// переходит к следующей стадии лесенки того же сида, затем к следующему сиду. Часы
    /// (<see cref="GenerationTimeout"/> × <see cref="GenerationTimeBudgetMultiplier"/>) только прерывают весь прогон
    /// <see cref="TimeoutException"/> — на быстрой и медленной машине один сид даёт одну раскладку.
    /// </para>
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

        // Граф V2 одной стадии строится без собственного таймера V2: бюджетом всего прогона владеет этот генератор
        // и только прерывает прогон. Иначе часы V2 могли бы объявить стадию «провалившейся» и сдвинуть исход сида.
        private const float StageGraphTimeBudgetSeconds = 3600f;

        private static readonly TimeSpan DefaultGenerationTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Per-call generation time budget. Preview and tests may raise this.</summary>
        public TimeSpan GenerationTimeout { get; set; } = DefaultGenerationTimeout;

        /// <summary>
        /// Multiplier applied to the resolved generation time budget. Stays 1.0 for tests/preview (so they
        /// validate the authored budget), but the runtime computes it from the authored budget and its
        /// runtime cap (e.g. 5s authored -> x6 for a 30s runtime cap). Масштабирует только то, через сколько
        /// прогон сдаётся с <see cref="TimeoutException"/>; какая раскладка получится у сида, от него не зависит.
        /// </summary>
        public float GenerationTimeBudgetMultiplier { get; set; } = 1f;

        /// <summary>How many seed offsets to try before failing.</summary>
        public int MaxGenerationAttempts { get; set; } = DefaultMaxAttempts;

        /// <summary>
        /// Часы прогона. По умолчанию (<c>null</c>) — <see cref="System.Diagnostics.Stopwatch"/> плюс системный таймер
        /// отмены. Хост и тесты могут подставить свои часы: тогда прогон прерывается только по ним (системный таймер не
        /// взводится), что делает проверку бюджета детерминированной. Часы лишь прерывают прогон
        /// <see cref="TimeoutException"/> и никогда не меняют раскладку сида.
        /// </summary>
        public Func<IGenerationClock> GenerationClockFactory { get; set; }

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

        /// <summary>
        /// Предупреждения последнего прогона: пропущенный необязательный контент, заглушки неверных ответов
        /// лабиринта, которые не поместились без наложения (дверь запечатана), неподходящая настройка соединения
        /// дверей. Состояние экземпляра генератора, а не общее статическое: параллельные генераторы не смешивают
        /// предупреждения друг друга.
        /// </summary>
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
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic($"Async generation running cooperatively on WebGL. seed={seed}, mission='{config.MissionId}'.");
            }

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

                if (DiagnosticLogsEnabled)
                {
                    LogDiagnostic($"Async generation scheduled. seed={seed}, tileset='{configSnapshot.TilesetId}'.");
                }

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
            BeginRun(seed);
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic($"Generation started. seed={seed}, tileset='{config?.TilesetId}'.");
            }

            ThrowIfMissingInputs(config, tileset, roomDatabase, validator);
            GenerationRun run = PrepareRun(config, tileset, roomDatabase, validator, useLayoutPacingV2,
                layoutPacingConfig);

            LevelValidationResult lastValidation = null;
            int lastAttemptSeed = seed;
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TimeSpan generationTimeout = ResolveGenerationTimeout(run);
            IGenerationClock clock = StartRunClock(timeoutCts, generationTimeout);
            long budgetMs = (long)generationTimeout.TotalMilliseconds;
            CancellationToken generationCt = timeoutCts.Token;

            try
            {
                for (int attempt = 0; attempt < MaxGenerationAttempts; attempt++)
                {
                    EnforceWallClockBudget(clock, budgetMs, timeoutCts, ct);
                    generationCt.ThrowIfCancellationRequested();

                    int attemptSeed = seed + attempt;
                    lastAttemptSeed = attemptSeed;
                    for (int stageIndex = 0; stageIndex < run.RetryStages.Count; stageIndex++)
                    {
                        EnforceWallClockBudget(clock, budgetMs, timeoutCts, ct);
                        generationCt.ThrowIfCancellationRequested();
                        StageState stage = BeginStage(attempt, attemptSeed, run.RetryStages[stageIndex]);
                        if (!TryBuildStageGraph(run, stage, generationCt))
                        {
                            lastValidation = stage.Rejection;
                            CollectAfterRejectedStage();
                            continue;
                        }

                        SelectStageRooms(run, stage, generationCt);
                        if (!TryApplyStageDependencyHints(run, stage) || !TryCaptureStageSelections(run, stage))
                        {
                            lastValidation = stage.Rejection;
                            CollectAfterRejectedStage();
                            continue;
                        }

                        BeginPlacement(stage);
                        stage.Placed = _roomPlacer is IReportingRoomPlacer reportingRoomPlacer
                            ? reportingRoomPlacer.Place(stage.Graph, stage.GenerationSelections, stage.Rng,
                                stage.PlacementReport, generationCt)
                            : _roomPlacer.Place(stage.Graph, stage.GenerationSelections, stage.Rng, generationCt);

                        // Часы только прерывают прогон: перерасход бюджета — TimeoutException, а не другой сид.
                        EnforceWallClockBudget(clock, budgetMs, timeoutCts, ct);
                        generationCt.ThrowIfCancellationRequested();

                        if (!TryConnectStage(stage))
                        {
                            lastValidation = stage.Rejection;
                            CollectAfterRejectedStage();
                            continue;
                        }

                        AddStageDeadEnds(run, stage, generationCt);
                        if (!TryValidateStage(run, stage))
                        {
                            lastValidation = stage.Rejection;
                            CollectAfterRejectedStage();
                            continue;
                        }

                        return CompleteGeneration(stage, (long)clock.Elapsed.TotalMilliseconds);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (DiagnosticLogsEnabled)
                {
                    LogDiagnostic(
                        $"Generation cancelled by caller. Last seed={lastAttemptSeed}. Debug: {LastDebugCounters}");
                }

                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw CreateTimeoutException(generationTimeout, lastAttemptSeed, lastValidation);
            }

            throw CreateExhaustedException(lastAttemptSeed, lastValidation);
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
            BeginRun(seed);
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic($"Cooperative generation started. seed={seed}, tileset='{config?.TilesetId}'.");
            }

            ThrowIfMissingInputs(config, tileset, roomDatabase, validator);
            GenerationRun run = PrepareRun(config, tileset, roomDatabase, validator, useLayoutPacingV2,
                layoutPacingConfig);
            await YieldAsync(ct);

            LevelValidationResult lastValidation = null;
            int lastAttemptSeed = seed;
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TimeSpan generationTimeout = ResolveGenerationTimeout(run);

            // WebGL is single-threaded, so the System.Threading.Timer that backs CancelAfter is unreliable
            // there. Enforce the same wall-clock budget explicitly at the cooperative yield points so a
            // pathological seed cannot run past the budget even if the timer never fires. The check only
            // aborts the whole run; it never decides which layout a seed produces.
            IGenerationClock wallClock = StartRunClock(timeoutCts, generationTimeout);
            long budgetMs = (long)generationTimeout.TotalMilliseconds;
            CancellationToken generationCt = timeoutCts.Token;
            RunWallClockBudget placementBudget = new(wallClock, budgetMs, timeoutCts, generationCt, ct);

            try
            {
                for (int attempt = 0; attempt < MaxGenerationAttempts; attempt++)
                {
                    EnforceWallClockBudget(wallClock, budgetMs, timeoutCts, ct);
                    generationCt.ThrowIfCancellationRequested();

                    int attemptSeed = seed + attempt;
                    lastAttemptSeed = attemptSeed;
                    for (int stageIndex = 0; stageIndex < run.RetryStages.Count; stageIndex++)
                    {
                        EnforceWallClockBudget(wallClock, budgetMs, timeoutCts, ct);
                        await YieldAsync(generationCt);

                        StageState stage = BeginStage(attempt, attemptSeed, run.RetryStages[stageIndex]);
                        if (!TryBuildStageGraph(run, stage, generationCt))
                        {
                            lastValidation = stage.Rejection;
                            await YieldAfterRejectedStageAsync(generationCt);
                            continue;
                        }

                        await YieldAsync(generationCt);
                        SelectStageRooms(run, stage, generationCt);
                        await YieldAsync(generationCt);
                        if (!TryApplyStageDependencyHints(run, stage))
                        {
                            lastValidation = stage.Rejection;
                            await YieldAfterRejectedStageAsync(generationCt);
                            continue;
                        }

                        await YieldAsync(generationCt);
                        if (!TryCaptureStageSelections(run, stage))
                        {
                            lastValidation = stage.Rejection;
                            await YieldAfterRejectedStageAsync(generationCt);
                            continue;
                        }

                        BeginPlacement(stage);
                        stage.Placed = await PlaceCooperativelyAsync(stage, placementBudget, generationCt);
                        EnforceWallClockBudget(wallClock, budgetMs, timeoutCts, ct);
                        generationCt.ThrowIfCancellationRequested();
                        await YieldAsync(generationCt);

                        if (!TryConnectStage(stage))
                        {
                            lastValidation = stage.Rejection;
                            await YieldAfterRejectedStageAsync(generationCt);
                            continue;
                        }

                        await YieldAsync(generationCt);
                        AddStageDeadEnds(run, stage, generationCt);
                        await YieldAsync(generationCt);
                        if (!TryValidateStage(run, stage))
                        {
                            lastValidation = stage.Rejection;
                            await YieldAfterRejectedStageAsync(generationCt);
                            continue;
                        }

                        return CompleteGeneration(stage, (long)wallClock.Elapsed.TotalMilliseconds);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (DiagnosticLogsEnabled)
                {
                    LogDiagnostic(
                        $"Generation cancelled by caller. Last seed={lastAttemptSeed}. Debug: {LastDebugCounters}");
                }

                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw CreateTimeoutException(generationTimeout, lastAttemptSeed, lastValidation);
            }

            throw CreateExhaustedException(lastAttemptSeed, lastValidation);
        }

        private void BeginRun(int seed)
        {
            _lastWarnings.Clear();
            _lastGraph = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            _rejectedStagesSinceGc = 0;
#endif
            LastSuccessfulSeed = seed;
            LastDebugCounters.Reset();
            LastDebugCounters.SetStage("start");
        }

        private static void ThrowIfMissingInputs(
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase,
            ILevelValidator validator)
        {
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
        }

        // Инварианты прогона считаются один раз, а не на каждую попытку/стадию: конфиг с интерактивным контентом,
        // лесенка стадий, снимки шаблонов, пул тупиков, подсказки зависимостей, план контента V2.
        private GenerationRun PrepareRun(
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase roomDatabase,
            ILevelValidator validator,
            bool useLayoutPacingV2,
            LayoutPacingConfig layoutPacingConfig)
        {
            LastDebugCounters.SetStage("prepare-interactive-content");
            IMissionConfigView effectiveConfig = PrepareInteractiveContentConfig(config, tileset, roomDatabase);
            WarnAboutIncidentalDoorContacts();
            LayoutPacingConfig pacing = useLayoutPacingV2 ? layoutPacingConfig : null;
            IReadOnlyList<LayoutRetryStage> retryStages = pacing != null
                ? GetLayoutPacingRetryStages(pacing.RetryPolicy)
                : new[] { LayoutRetryStage.Full };
            return new GenerationRun(effectiveConfig, tileset, roomDatabase, validator, pacing, retryStages);
        }

        // Случайные касания дверей вне графа MissionGenerator всегда отвергает (ConnectionNotInGraph), поэтому
        // соединитель в режиме «соединять их» только сжигает сиды. Говорим об этом явно, а не молча ретраим.
        private void WarnAboutIncidentalDoorContacts()
        {
            if (_doorConnector is DoorConnector { ConnectIncidentalDoorContacts: true })
            {
                _lastWarnings.Add(
                    "DoorConnector connects incidental door contacts outside the level graph "
                    + "(IncidentalDoorContactMode.ConnectOutsideGraph / new DoorConnector(true)). Graph validation "
                    + "rejects such connections (ConnectionNotInGraph), so every seed with an accidental door contact "
                    + "is retried. Use new DoorConnector() (IncidentalDoorContactMode.Seal) for generated missions.");
            }
        }

        private TimeSpan ResolveGenerationTimeout(GenerationRun run)
        {
            return ApplyBudgetMultiplier(run.UsesLayoutPacing
                ? TimeSpan.FromSeconds(run.LayoutPacingConfig.GenerationTimeBudgetSeconds)
                : GenerationTimeout);
        }

        private StageState BeginStage(int attempt, int attemptSeed, LayoutRetryStage retryStage)
        {
            LastDebugCounters.BeginAttempt(attemptSeed);
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic(
                    $"Attempt {attempt + 1}/{MaxGenerationAttempts} started. seed={attemptSeed}, stage={retryStage}.");
            }

            return new StageState(attempt, attemptSeed, retryStage, _rngFactory.Create(attemptSeed));
        }

        private bool TryBuildStageGraph(GenerationRun run, StageState stage, CancellationToken ct)
        {
            if (!run.UsesLayoutPacing)
            {
                LastDebugCounters.SetStage("build-graph");
                stage.Graph = _graphBuilder.Build(run.EffectiveConfig, run.Tileset, stage.Rng);
                return true;
            }

            LastDebugCounters.SetStage("build-graph-v2-" + stage.RetryStage);
            try
            {
                stage.Graph = BuildLayoutPacingV2Graph(run, stage.RetryStage, stage.Seed, ct);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                stage.Rejection = new LevelValidationResult(new[]
                {
                    new LevelValidationIssue("GraphBuildFailed", ex.Message, LevelValidationSeverity.Error)
                });
                if (DiagnosticLogsEnabled)
                {
                    LogDiagnostic(
                        $"Attempt {stage.Attempt + 1}, stage {stage.RetryStage} rejected during graph build: {ex.Message}");
                }

                return false;
            }
        }

        private void SelectStageRooms(GenerationRun run, StageState stage, CancellationToken ct)
        {
            LastDebugCounters.SetStage("select-rooms");
            stage.Selections = SelectRooms(stage.Graph, run.EffectiveConfig, run.Tileset, run.RoomDatabase, stage.Rng,
                run.UsesLayoutPacing);
            ct.ThrowIfCancellationRequested();
        }

        // Замок без достижимого ключа — дефект раскладки, а не повод молча продолжить: стадия отклоняется до
        // размещения (и не зависит от того, какой валидатор подключён).
        private bool TryApplyStageDependencyHints(GenerationRun run, StageState stage)
        {
            LastDebugCounters.SetStage("dependency-hints");
            List<LevelValidationIssue> issues = ApplyDependencyHints(run, stage.Graph, stage.Selections);
            if (issues == null || issues.Count == 0)
            {
                return true;
            }

            stage.Rejection = new LevelValidationResult(issues);
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic(
                    $"Attempt {stage.Attempt + 1}, stage {stage.RetryStage} rejected by dependency hints: {FormatIssues(stage.Rejection)}");
            }

            return false;
        }

        private bool TryCaptureStageSelections(GenerationRun run, StageState stage)
        {
            LastDebugCounters.SetStage("snapshot-selections");
            stage.GenerationSelections = run.CaptureSelections(stage.Selections);
            LevelValidationResult selectionCoverage = ValidateSelectionCoverage(stage.GenerationSelections, stage.Graph);
            if (selectionCoverage.IsValid)
            {
                return true;
            }

            stage.Rejection = selectionCoverage;
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic(
                    $"Attempt {stage.Attempt + 1}, stage {stage.RetryStage} rejected before placement: {FormatIssues(selectionCoverage)}");
            }

            return false;
        }

        private void BeginPlacement(StageState stage)
        {
            stage.PlacementReport = new RoomPlacementReport();
            LastDebugCounters.SetStage("place-rooms");
            if (_roomPlacer is IRoomPlacementDebugCountersSink debugCountersSink)
            {
                debugCountersSink.SetDebugCounters(LastDebugCounters);
            }
        }

        private async Task<LevelLayout> PlaceCooperativelyAsync(
            StageState stage,
            IRoomPlacementBudget budget,
            CancellationToken generationCt)
        {
            if (_roomPlacer is IReportingRoomPlacer reportingRoomPlacer)
            {
                return await reportingRoomPlacer.PlaceAsync(
                    stage.Graph,
                    stage.GenerationSelections,
                    stage.Rng,
                    budget,
                    stage.PlacementReport,
                    generationCt);
            }

            if (_roomPlacer is IAsyncRoomPlacerWithBudget budgetedAsyncRoomPlacer)
            {
                return await budgetedAsyncRoomPlacer.PlaceAsync(
                    stage.Graph,
                    stage.GenerationSelections,
                    stage.Rng,
                    budget,
                    generationCt);
            }

            if (_roomPlacer is IAsyncRoomPlacer asyncRoomPlacer)
            {
                return await asyncRoomPlacer.PlaceAsync(
                    stage.Graph,
                    stage.GenerationSelections,
                    stage.Rng,
                    generationCt);
            }

            return _roomPlacer.Place(stage.Graph, stage.GenerationSelections, stage.Rng, generationCt);
        }

        // Покрытие размещения считается по тому же набору best-effort узлов, что и успех поиска размещения:
        // неразмещённая заглушка неверного ответа — не провал, она убирается из графа до соединения дверей
        // (дверь запечатается), а генератор запишет предупреждение.
        private bool TryConnectStage(StageState stage)
        {
            HashSet<string> bestEffortNodeIds = MazeWrongAnswerDeadEnds.CollectNodeIds(stage.Graph);
            LevelValidationResult placementCoverage =
                ValidatePlacementCoverage(stage.Placed, stage.Graph, bestEffortNodeIds);
            if (!placementCoverage.IsValid)
            {
                stage.Rejection = placementCoverage;
                if (DiagnosticLogsEnabled)
                {
                    LogDiagnostic(
                        $"Attempt {stage.Attempt + 1}, stage {stage.RetryStage} rejected after placement: {FormatIssues(placementCoverage)}. Debug: {LastDebugCounters}");
                }

                return false;
            }

            List<LevelGraphEdge> droppedMazeDeadEnds = new();
            stage.PlacedGraph = MazeWrongAnswerDeadEnds.WithoutUnplacedNodes(
                stage.Graph,
                stage.Placed,
                bestEffortNodeIds,
                droppedMazeDeadEnds);
            stage.DroppedMazeDeadEnds = droppedMazeDeadEnds;

            LastDebugCounters.SetStage("connect-doors");
            stage.Connected = _doorConnector.Connect(stage.Placed, stage.PlacedGraph, stage.GenerationSelections);
            return true;
        }

        private void AddStageDeadEnds(GenerationRun run, StageState stage, CancellationToken ct)
        {
            LastDebugCounters.SetStage("add-dead-end-rooms");
            DeadEndRoomPostProcessResult deadEnds = run.DeadEndProcessor.Process(
                stage.Connected,
                stage.PlacedGraph,
                stage.Rng,
                ct);
            stage.DeadEnds = deadEnds;
            stage.Connected = deadEnds.Layout;
            stage.ValidationGraph = deadEnds.Graph;
        }

        private bool TryValidateStage(GenerationRun run, StageState stage)
        {
            LastDebugCounters.SetStage("validate-layout");
            IMissionConfigView validationConfig = run.UsesLayoutPacing
                ? new LayoutPacingValidationConfigView(run.EffectiveConfig, stage.Connected.Rooms.Count)
                : new PostProcessedLayoutValidationConfigView(run.EffectiveConfig, stage.Connected.Rooms.Count);
            LevelValidationResult mazeBranchAnswerTopology =
                ValidateMazeBranchAnswerTopology(
                    stage.Connected,
                    stage.ValidationGraph,
                    stage.GenerationSelections,
                    run.EffectiveConfig);
            if (!mazeBranchAnswerTopology.IsValid && DiagnosticLogsEnabled)
            {
                // Non-fatal: a wrong-answer door whose cap did not fit without overlap is sealed (never
                // force-placed); the maze stays solvable. Diagnostic only, the seed is not rejected here.
                LogDiagnostic(
                    $"Attempt {stage.Attempt + 1}, stage {stage.RetryStage} maze dead-end warning (not rejected): {FormatIssues(mazeBranchAnswerTopology)}");
            }

            // Свободные disabled-двери — НЕ ошибка генерации. Dead-end-проход уже попытался закрыть каждую
            // свободную дверь тупиковой комнатой (перебрав все dead-end шаблоны), а ту, что закрыть не удалось,
            // запечатал в стену — допустимый best-effort. Сид по доле disabled-дверей не отклоняется (раньше
            // плотные миссии уходили в retry-loop до таймаута); только диагностируем.
            LogDisabledDoorDiagnostic(stage.Connected, stage.GenerationSelections, stage.DeadEnds, stage.Attempt + 1,
                stage.RetryStage);

            LevelValidationResult validation =
                ValidateLayout(run.Validator, stage.Connected, stage.ValidationGraph, validationConfig, run.Tileset);
            if (validation.IsValid)
            {
                return true;
            }

            stage.Rejection = validation;
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic(
                    $"Attempt {stage.Attempt + 1}, stage {stage.RetryStage} failed validation: {FormatIssues(validation)}");
            }

            return false;
        }

        private LevelLayout CompleteGeneration(StageState stage, long elapsedMilliseconds)
        {
            _lastGraph = stage.ValidationGraph;
            LastSuccessfulSeed = stage.Seed;
            if (DiagnosticLogsEnabled)
            {
                LogDiagnostic(
                    $"Generation completed in {elapsedMilliseconds} ms. seed={stage.Seed}, rooms={stage.Connected.Rooms.Count}, stage={stage.RetryStage}. Debug: {LastDebugCounters}");
            }

            AddMazeDeadEndWarnings(stage);
            return stage.Connected;
        }

        // Предупреждения о заглушках неверных ответов — только для принятой раскладки и только в состоянии этого
        // генератора (раньше — общий статический список размещения, который делили все прогоны и потоки).
        private void AddMazeDeadEndWarnings(StageState stage)
        {
            if (stage.DroppedMazeDeadEnds == null || stage.DroppedMazeDeadEnds.Count == 0)
            {
                return;
            }

            for (int i = 0; i < stage.DroppedMazeDeadEnds.Count; i++)
            {
                LevelGraphEdge edge = stage.DroppedMazeDeadEnds[i];
                string mazeSocketId = stage.PlacementReport != null
                                      && stage.PlacementReport.TryGetUnplacedMazeCap(edge.ToNodeId,
                                          out RoomPlacementReport.UnplacedMazeCap cap)
                    ? cap.MazeSocketId
                    : null;
                string warning =
                    $"Maze '{edge.FromNodeId}': wrong-answer dead-end '{edge.ToNodeId}' did not fit without "
                    + "overlapping other rooms, so it was not placed (dead-end caps are never force-placed). "
                    + DescribeCapDoorOutcome(stage.Connected, edge.FromNodeId, mazeSocketId);
                _lastWarnings.Add(warning);
                if (DiagnosticLogsEnabled)
                {
                    LogDiagnostic($"[MazeDeadEnd][WARNING] {warning}");
                }
            }
        }

        // Итог ровно той двери ответа, что была назначена этой заглушке (из отчёта размещения).
        private static string DescribeCapDoorOutcome(LevelLayout layout, string mazeNodeId, string mazeSocketId)
        {
            if (string.IsNullOrEmpty(mazeSocketId))
            {
                return "Its answer door is sealed into a wall or capped by the dead-end pass.";
            }

            PlacedRoom maze = null;
            if (layout?.Rooms != null)
            {
                for (int i = 0; i < layout.Rooms.Count; i++)
                {
                    if (string.Equals(layout.Rooms[i]?.NodeId, mazeNodeId, StringComparison.Ordinal))
                    {
                        maze = layout.Rooms[i];
                        break;
                    }
                }
            }

            bool sealedDoor = maze?.DoorStates != null
                              && maze.DoorStates.TryGetValue(mazeSocketId, out DoorState state)
                              && state == DoorState.Disabled;
            return sealedDoor
                ? $"Its answer door '{mazeSocketId}' is sealed into a wall."
                : $"Its answer door '{mazeSocketId}' was capped by the dead-end pass instead.";
        }

        private TimeoutException CreateTimeoutException(
            TimeSpan generationTimeout,
            int lastAttemptSeed,
            LevelValidationResult lastValidation)
        {
            LastDebugCounters.MarkTimeout();
            string timeoutReason =
                $"Generator timed out after {generationTimeout.TotalSeconds:0}s. Last seed={lastAttemptSeed}. Last issues: {FormatIssues(lastValidation)}. Debug: {LastDebugCounters}";
            LogDiagnostic(timeoutReason);
            return new TimeoutException(timeoutReason);
        }

        private InvalidOperationException CreateExhaustedException(
            int lastAttemptSeed,
            LevelValidationResult lastValidation)
        {
            string reason =
                $"Generator exhausted {MaxGenerationAttempts} attempts. Last seed={lastAttemptSeed}. Last issues: {FormatIssues(lastValidation)}. Debug: {LastDebugCounters}";
            LogDiagnostic(reason);
            return new InvalidOperationException(reason);
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
            IGenerationClock wallClock,
            long budgetMs,
            CancellationTokenSource timeoutCts,
            CancellationToken callerCt)
        {
            if (budgetMs > 0
                && wallClock.Elapsed.TotalMilliseconds >= budgetMs
                && !callerCt.IsCancellationRequested
                && !timeoutCts.IsCancellationRequested)
            {
                timeoutCts.Cancel();
            }
        }

        // Часы прогона: свои (детерминированно, без системного таймера) или Stopwatch + CancelAfter.
        private IGenerationClock StartRunClock(CancellationTokenSource timeoutCts, TimeSpan generationTimeout)
        {
            IGenerationClock injected = GenerationClockFactory?.Invoke();
            if (injected != null)
            {
                return injected;
            }

            timeoutCts.CancelAfter(generationTimeout);
            return new StopwatchGenerationClock();
        }

        private sealed class StopwatchGenerationClock : IGenerationClock
        {
            private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

            public TimeSpan Elapsed => _stopwatch.Elapsed;
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

        /// <summary>
        /// Бюджет всего прогона для кооперативного размещения: проверяет те же часы, что и цикл попыток, и только
        /// прерывает прогон (общий таймаут → <see cref="TimeoutException"/>). Отдельного лимита стадии размещения
        /// нет: он делал исход сида зависимым от скорости машины и множителя бюджета.
        /// </summary>
        private sealed class RunWallClockBudget : IRoomPlacementBudget
        {
            private readonly IGenerationClock _wallClock;
            private readonly long _budgetMs;
            private readonly CancellationTokenSource _timeoutCts;
            private readonly CancellationToken _generationCt;
            private readonly CancellationToken _callerCt;

            public RunWallClockBudget(
                IGenerationClock wallClock,
                long budgetMs,
                CancellationTokenSource timeoutCts,
                CancellationToken generationCt,
                CancellationToken callerCt)
            {
                _wallClock = wallClock;
                _budgetMs = budgetMs;
                _timeoutCts = timeoutCts;
                _generationCt = generationCt;
                _callerCt = callerCt;
            }

            public void ThrowIfExpired()
            {
                _callerCt.ThrowIfCancellationRequested();
                EnforceWallClockBudget(_wallClock, _budgetMs, _timeoutCts, _callerCt);
                _generationCt.ThrowIfCancellationRequested();
            }
        }

        /// <summary>Инварианты одного прогона BuildLayout: считаются один раз и переиспользуются всеми попытками.</summary>
        private sealed class GenerationRun
        {
            private readonly Dictionary<IRoomTemplateDescriptor, IRoomTemplateDescriptor> _snapshotsByTemplate =
                new(TemplateReferenceComparer.Instance);

            private DeadEndRoomPostProcessor _deadEndProcessor;

            public GenerationRun(
                IMissionConfigView effectiveConfig,
                ITilesetView tileset,
                IRoomDatabase roomDatabase,
                ILevelValidator validator,
                LayoutPacingConfig layoutPacingConfig,
                IReadOnlyList<LayoutRetryStage> retryStages)
            {
                EffectiveConfig = effectiveConfig;
                Tileset = tileset;
                RoomDatabase = roomDatabase;
                Validator = validator;
                LayoutPacingConfig = layoutPacingConfig;
                RetryStages = retryStages;
            }

            public IMissionConfigView EffectiveConfig { get; }
            public ITilesetView Tileset { get; }
            public IRoomDatabase RoomDatabase { get; }
            public ILevelValidator Validator { get; }
            public LayoutPacingConfig LayoutPacingConfig { get; }
            public bool UsesLayoutPacing => LayoutPacingConfig != null;
            public IReadOnlyList<LayoutRetryStage> RetryStages { get; }
            public MissionContentPlan ContentPlan { get; set; }
            public DependencyHintPlan HintPlan { get; set; }
            public bool HintPlanResolved { get; set; }

            public DeadEndRoomPostProcessor DeadEndProcessor =>
                _deadEndProcessor ??= new DeadEndRoomPostProcessor(BuildDeadEndRoomPool(RoomDatabase, Tileset));

            /// <summary>
            /// Снимки выбранных шаблонов: один неизменяемый снимок на шаблон за прогон, а не новый на каждый узел
            /// каждой попытки. Снимок неизменяем, поэтому делить его между узлами и попытками безопасно.
            /// </summary>
            public Dictionary<string, IRoomTemplateDescriptor> CaptureSelections(
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections)
            {
                Dictionary<string, IRoomTemplateDescriptor> result = new(selections.Count, StringComparer.Ordinal);
                foreach (KeyValuePair<string, IRoomTemplateDescriptor> kv in selections)
                {
                    if (kv.Value == null)
                    {
                        continue;
                    }

                    if (!_snapshotsByTemplate.TryGetValue(kv.Value, out IRoomTemplateDescriptor snapshot))
                    {
                        snapshot = new RoomTemplateDescriptorSnapshot(kv.Value);
                        _snapshotsByTemplate[kv.Value] = snapshot;
                    }

                    result[kv.Key] = snapshot;
                }

                return result;
            }
        }

        /// <summary>Подсказки зависимостей и их окружение — инварианты прогона (тайлсет и конфиг не меняются).</summary>
        private sealed class DependencyHintPlan
        {
            public DependencyHintPlan(
                IReadOnlyList<RoomDependencyHint> hints,
                IReadOnlyList<IRoomTemplateDescriptor> tilesetRooms,
                Dictionary<string, IRoomTemplateDescriptor> roomsById,
                EffectiveRoomWeightResolver weightResolver)
            {
                Hints = hints;
                TilesetRooms = tilesetRooms;
                RoomsById = roomsById;
                WeightResolver = weightResolver;
            }

            public IReadOnlyList<RoomDependencyHint> Hints { get; }
            public IReadOnlyList<IRoomTemplateDescriptor> TilesetRooms { get; }
            public Dictionary<string, IRoomTemplateDescriptor> RoomsById { get; }
            public EffectiveRoomWeightResolver WeightResolver { get; }
        }

        /// <summary>Состояние одной стадии одной попытки (сид + ступень лесенки).</summary>
        private sealed class StageState
        {
            public StageState(int attempt, int seed, LayoutRetryStage retryStage, IDeterministicRng rng)
            {
                Attempt = attempt;
                Seed = seed;
                RetryStage = retryStage;
                Rng = rng;
            }

            public int Attempt { get; }
            public int Seed { get; }
            public LayoutRetryStage RetryStage { get; }
            public IDeterministicRng Rng { get; }
            public LevelGraph Graph { get; set; }
            public Dictionary<string, IRoomTemplateDescriptor> Selections { get; set; }
            public Dictionary<string, IRoomTemplateDescriptor> GenerationSelections { get; set; }
            public LevelLayout Placed { get; set; }
            public LevelGraph PlacedGraph { get; set; }
            public IReadOnlyList<LevelGraphEdge> DroppedMazeDeadEnds { get; set; }
            public LevelLayout Connected { get; set; }
            public DeadEndRoomPostProcessResult DeadEnds { get; set; }
            public LevelGraph ValidationGraph { get; set; }
            public LevelValidationResult Rejection { get; set; }
            public RoomPlacementReport PlacementReport { get; set; }
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

        // Сообщения с интерполяцией вызывающие строят только под DiagnosticLogsEnabled; проверка здесь — страховка.
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

            HashSet<string> checkedTemplates = null;
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
                    continue;
                }

                // Шаблон с повторяющимся id подключаемого сокета склеил бы состояние двух дверей — отклоняем до
                // размещения явной ошибкой с именем комнаты и сокета (один раз на шаблон).
                checkedTemplates ??= new HashSet<string>(StringComparer.Ordinal);
                if (checkedTemplates.Add(template.RoomId ?? string.Empty)
                    && DoorSocketIdRules.TryFindDuplicateConnectableSocketId(template, out string socketId))
                {
                    issues.Add(DoorSocketIdRules.CreateDuplicateIssue(template.RoomId, socketId));
                }
            }

            return issues.Count == 0 ? LevelValidationResult.Empty : new LevelValidationResult(issues);
        }

        private static LevelValidationResult ValidatePlacementCoverage(
            LevelLayout layout,
            LevelGraph graph,
            ISet<string> bestEffortNodeIds)
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
                if (placedNodes.Contains(nodeId)
                    || (bestEffortNodeIds != null && bestEffortNodeIds.Contains(nodeId)))
                {
                    continue;
                }

                issues.Add(new LevelValidationIssue(
                    "GraphNodeNotPlaced",
                    $"Graph node '{nodeId}' was not placed by the room placer.",
                    LevelValidationSeverity.Error));
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
            GenerationRun run,
            LayoutRetryStage retryStage,
            int seed,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LayoutPacingMissionGenerator generator = new();
            LayoutPacingConfig stageConfig =
                CreateSingleStageConfig(new LayoutRetryPlanner().Apply(run.LayoutPacingConfig, retryStage));
            run.ContentPlan ??= CreateMissionContentPlan(run.EffectiveConfig);

            // Одна стадия (FailFast) без собственного таймера V2: исход стадии решают сид и конфиг, а бюджетом
            // прогона по часам владеет MissionGenerator (он только прерывает прогон TimeoutException).
            LayoutGenerationResult result = generator.Generate(new LayoutGenerationRequest(
                seed,
                stageConfig,
                run.ContentPlan,
                StageGraphTimeBudgetSeconds));

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
            // The ladder is identical across all platforms (no #if UNITY_WEBGL fork). Determinism of a seed
            // also requires that nothing time-based picks the stage: placement is bounded by counted budgets
            // (SocketBacktrackingRoomPlacer.CandidateEvaluationBudget, search steps), an exhausted stage moves to
            // the next stage of the SAME seed, and the wall-clock budget (GenerationTimeout × multiplier) only
            // aborts the whole run with TimeoutException. A slower machine can therefore time out, but it can
            // never produce a different layout for the same seed.
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

        // Возвращает ошибки KeyDependencyUnsatisfied для замков, которым не досталось достижимого ключа (null — всё
        // в порядке). Ключ ставится только туда, куда можно дойти от старта, не входя в его замок (KeyLockReachability,
        // тот же обход, что у LevelValidator), и никогда на best-effort узел (заглушку неверного ответа): её могут
        // убрать из графа, и ключ исчез бы вместе с ней.
        private List<LevelValidationIssue> ApplyDependencyHints(
            GenerationRun run,
            LevelGraph graph,
            Dictionary<string, IRoomTemplateDescriptor> selections)
        {
            if (_hintExtractors.Count == 0
                || graph?.Nodes == null
                || graph.Nodes.Count == 0
                || selections == null
                || run.RoomDatabase == null)
            {
                return null;
            }

            DependencyHintPlan plan = ResolveDependencyHintPlan(run);
            if (plan == null)
            {
                return null;
            }

            string startNodeId = graph.Nodes[0].NodeId;
            Dictionary<string, List<string>> adjacency = KeyLockReachability.BuildAdjacency(EnumerateGraphLinks(graph));
            HashSet<string> bestEffortNodeIds = MazeWrongAnswerDeadEnds.CollectNodeIds(graph);
            for (int i = 0; i < plan.Hints.Count; i++)
            {
                RoomDependencyHint hint = plan.Hints[i];
                if (!hint.IsValid
                    || !plan.RoomsById.TryGetValue(hint.SourceRoomTemplateId, out IRoomTemplateDescriptor sourceTemplate))
                {
                    continue;
                }

                foreach (string target in FindSelectedNodes(selections, hint.TargetRoomTemplateId))
                {
                    // Узел мог перестать быть замком: ранее в этом проходе на него поставили ключ другой подсказки.
                    if (!HoldsTemplate(selections, target, hint.TargetRoomTemplateId))
                    {
                        continue;
                    }

                    string sourceNodeId = FindBestSourceNode(graph, adjacency, startNodeId, target, sourceTemplate.Kind,
                        hint, bestEffortNodeIds);
                    if (string.IsNullOrEmpty(sourceNodeId)
                        || !TryFindNode(graph, sourceNodeId, out RoomNode sourceNode)
                        || !IsDependencySourceCompatible(
                            sourceNode,
                            sourceTemplate,
                            plan.TilesetRooms,
                            plan.WeightResolver))
                    {
                        continue;
                    }

                    selections[sourceNodeId] = sourceTemplate;
                }
            }

            return CollectLocksWithoutReachableKey(plan, selections, startNodeId, adjacency, bestEffortNodeIds);
        }

        private static List<LevelValidationIssue> CollectLocksWithoutReachableKey(
            DependencyHintPlan plan,
            Dictionary<string, IRoomTemplateDescriptor> selections,
            string startNodeId,
            IReadOnlyDictionary<string, List<string>> adjacency,
            ISet<string> bestEffortNodeIds)
        {
            List<LevelValidationIssue> issues = null;
            for (int i = 0; i < plan.Hints.Count; i++)
            {
                RoomDependencyHint hint = plan.Hints[i];
                if (!hint.IsValid)
                {
                    continue;
                }

                foreach (string lockNodeId in FindSelectedNodes(selections, hint.TargetRoomTemplateId))
                {
                    if (HasKeyBeforeLock(selections, startNodeId, lockNodeId, hint.SourceRoomTemplateId, adjacency,
                            bestEffortNodeIds))
                    {
                        continue;
                    }

                    issues ??= new List<LevelValidationIssue>();
                    issues.Add(new LevelValidationIssue(
                        "KeyDependencyUnsatisfied",
                        $"Lock room '{lockNodeId}' ('{hint.TargetRoomTemplateId}') has no key room '{hint.SourceRoomTemplateId}' reachable from Start before it (keys are never put behind their lock or on a wrong-answer dead-end).",
                        LevelValidationSeverity.Error));
                }
            }

            return issues;
        }

        private static bool HasKeyBeforeLock(
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            string startNodeId,
            string lockNodeId,
            string keyTemplateId,
            IReadOnlyDictionary<string, List<string>> adjacency,
            ISet<string> bestEffortNodeIds)
        {
            HashSet<string> beforeLock = KeyLockReachability.CollectReachableBeforeLock(startNodeId, lockNodeId, adjacency);
            foreach (string nodeId in beforeLock)
            {
                if (!bestEffortNodeIds.Contains(nodeId) && HoldsTemplate(selections, nodeId, keyTemplateId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HoldsTemplate(
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            string nodeId,
            string roomTemplateId)
        {
            return selections.TryGetValue(nodeId, out IRoomTemplateDescriptor template)
                   && template != null
                   && string.Equals(template.RoomId, roomTemplateId, StringComparison.Ordinal);
        }

        // Подсказки, комнаты тайлсета и веса — инварианты прогона: считаются один раз, а не на каждую попытку.
        private DependencyHintPlan ResolveDependencyHintPlan(GenerationRun run)
        {
            if (run.HintPlanResolved)
            {
                return run.HintPlan;
            }

            run.HintPlanResolved = true;
            IReadOnlyList<RoomDependencyHint> hints = ExtractHints(run.Tileset, run.RoomDatabase);
            if (hints.Count == 0)
            {
                run.HintPlan = null;
                return null;
            }

            IReadOnlyList<IRoomTemplateDescriptor> tilesetRooms =
                run.RoomDatabase.GetRoomsForTileset(run.Tileset.TilesetId);
            run.HintPlan = new DependencyHintPlan(
                hints,
                tilesetRooms,
                BuildRoomLookup(tilesetRooms),
                BuildWeightResolver(run.EffectiveConfig));
            return run.HintPlan;
        }

        private static IEnumerable<(string a, string b)> EnumerateGraphLinks(LevelGraph graph)
        {
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                yield return (graph.Edges[i].FromNodeId, graph.Edges[i].ToNodeId);
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
            IReadOnlyDictionary<string, List<string>> adjacency,
            string startNodeId,
            string targetNodeId,
            RoomKind sourceKind,
            RoomDependencyHint hint,
            ISet<string> bestEffortNodeIds)
        {
            Dictionary<string, int> distances = KeyLockReachability.Distances(targetNodeId, adjacency);
            HashSet<string> reachableBeforeLock =
                KeyLockReachability.CollectReachableBeforeLock(startNodeId, targetNodeId, adjacency);
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

                // Узел за замком (достижим от старта только через комнату-замок) ключом быть не может; заглушка
                // неверного ответа — тоже: она best-effort и может исчезнуть из раскладки вместе с ключом.
                if (!reachableBeforeLock.Contains(node.NodeId) || bestEffortNodeIds.Contains(node.NodeId))
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
