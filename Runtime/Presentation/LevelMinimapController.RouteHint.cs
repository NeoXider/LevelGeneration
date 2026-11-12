using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using Neoxider.LevelGeneration.Runtime.Application;
using Neoxider.LevelGeneration.Runtime.Infrastructure;
using UnityEngine;

namespace Neoxider.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Раскрытие комнат (вход и открытая дверь) и подсказка маршрута. Решения здесь не принимаются: когда
    /// раскрыть комнату, решает <see cref="LevelMinimapSession"/>, куда и когда вести игрока —
    /// <see cref="LevelRouteHintSession"/> (<see cref="DecideRouteHintUseCase"/>), какой линией —
    /// <see cref="PlanRouteHintPathUseCase"/>. Контроллер подаёт им события, время и состояние дверей и
    /// рисует результат одинаково на компактной и на большой карте.
    /// </summary>
    public sealed partial class LevelMinimapController
    {
        private const int RouteHintTextureSize = 32;

        /// <summary>
        /// Потолок точек пунктира на одну карту за кадр: длинный путь на большой карте при мелком шаге
        /// иначе превращался бы в сотни <c>DrawTexture</c>.
        /// </summary>
        private const int MaxRouteHintDots = 160;

        /// <summary>Доля радиуса точки, отданная тёмному канту, запечённому в ту же текстуру.</summary>
        private const float RouteHintDotRimFraction = 0.3f;

        [Header("Settings")]
        [SerializeField]
        [Tooltip("Настройки раскрытия комнат и подсказки маршрута. Пусто — берётся ассет "
                 + "Resources/Minimap/LevelMinimapSettings.")]
        private LevelMinimapSettings settings;

        private readonly LevelRouteHintSession _routeHint = new();
        private readonly RouteHintGeometry _routeHintGeometry = new();
        private readonly RouteHintTrail _routeHintTrail = new();
        private readonly List<Vector2> _routeHintPlanScratch = new();
        private readonly List<Vector2> _routeHintVisibleScratch = new();
        private int _routeHintPlanCount;
        private int _routeHintPlannedLockSignature;
        private float _nextRouteHintLockCheckTime;
        private float _routeHintDeviationMeters;
        private string _routeHintFailureText = string.Empty;
        private readonly List<Vector2> _routeHintScreenPath = new();
        private readonly List<float> _routeHintScreenDistances = new();
        private readonly List<RouteHintScreenWall> _routeHintScreenWalls = new();
        private readonly List<RouteHintNearWall> _routeHintNearWalls = new();
        private int _routeHintNearWallsRevision = int.MinValue;
        private int _routeHintNearWallsRevealVersion = int.MinValue;
        private int _routeHintNearWallsGeometryVersion = int.MinValue;
        private IReadOnlyList<MinimapRoomSnapshot> _routeHintNearWallsRooms;
        private int _routeHintNearWallsRoomCount = -1;
        private readonly List<DoorOpeningSubscription> _doorSubscriptions = new();
        private readonly Dictionary<string, IRoomTaskAnchor[]> _taskAnchorsByNodeId = new(StringComparer.Ordinal);
        private IRoomTaskAnchor _routeHintTaskAnchor;
        private CriticalPathDoor _routeHintTaskTarget;

        private LevelMinimapSettings _resolvedSettings;
        private bool _ownsResolvedSettings;
        private float _routeHintAlpha;
        private float _routeHintDrawOn;
        private float _nextRouteHintReplanTime;
        private int _plannedRouteHintTargetVersion = -1;
        private int _plannedRouteHintRevealVersion = -1;
        private bool _routeHintPlanned;
        private float _routeHintBlockedSeconds;
        private float _lastRouteHintDiagnosticLogTime = float.NegativeInfinity;

        /// <summary>
        /// Строка диагностики собирается лениво — когда её читают (чит-панель) или пишут в лог на смене
        /// состояния, — а не каждые полсекунды впустую: это крупная строка форматирования, мусор в WebGL.
        /// Прочитанная строка живёт <see cref="RouteHintDiagnosticRefreshSeconds"/>, как и прежде.
        /// </summary>
        public string RouteHintDiagnosticText
        {
            get
            {
                if (_routeHintDiagnosticGameplay == null)
                {
                    return _routeHintDiagnosticFallbackText;
                }

                float now = Time.unscaledTime;
                if (_routeHintDiagnosticText == null || now >= _routeHintDiagnosticTextExpiresAt)
                {
                    _routeHintDiagnosticText = BuildRouteHintDiagnosticText();
                    _routeHintDiagnosticTextExpiresAt = now + RouteHintDiagnosticRefreshSeconds;
                }

                return _routeHintDiagnosticText;
            }
        }

        private const float RouteHintDiagnosticRefreshSeconds = 0.5f;
        private string _routeHintDiagnosticFallbackText = "No active level";
        private string _routeHintDiagnosticText;
        private float _routeHintDiagnosticTextExpiresAt;

        /// <summary>Сколько раз собиралась строка диагностики подсказки (страж ленивой сборки).</summary>
        internal int RouteHintDiagnosticBuildCount { get; private set; }

        // Входы строки диагностики с последнего кадра: строка собирается из них по требованию.
        private IMinimapGameplayContext _routeHintDiagnosticGameplay;
        private RouteHintPresence _routeHintDiagnosticPresence;
        private string _routeHintDiagnosticBlocker = string.Empty;
        private bool _routeHintDiagnosticEnabled;
        private bool _routeHintDiagnosticLiveStartBlocked;
        private bool _plannedRouteHintTaskActive;
        private const int NoCriticalPathProblemCode = -1;
        private const string PathNotPlannedBlocker = "PathNotPlanned";

        private bool _hasReportedRouteHintProblem;
        private int _reportedRouteHintProblemCode;
        private int _reportedRouteHintProblemCause;
        private string _reportedRouteHintProblemFrom;
        private string _reportedRouteHintProblemTo;
        private Texture2D _routeHintDotTexture;
        private Texture2D _routeHintRingTexture;
        private Color _routeHintDotTextureFill;
        private Color _routeHintDotTextureRim;

        /// <summary>Доля проигранного проявления комнаты, которую сейчас рисуют; 1 — обычная отрисовка.</summary>
        private float _roomRevealProgress = 1f;

        /// <summary>Игрок в мире или его держит UI — по последнему Update (до первого кадра — в мире).</summary>
        private RouteHintPresence _playerPresence = RouteHintPresence.Exploring;

        /// <summary>Последний Repaint компактной карты не смог начать пунктир от живой позиции игрока.</summary>
        private bool _routeHintLiveStartBlocked;

        private float _routeHintLiveStartBlockedSince = -1f;
        private const float LiveStartBlockedReportSeconds = 2f;

        private bool _hasLoggedRouteHintState;
        private RouteHintPresence _loggedPresence;
        private RouteHintOutcome _loggedOutcome;
        private bool _loggedPlanned;
        private bool _loggedFullMapVisible;
        private bool _loggedFullMapOpen;
        private bool _loggedLiveStartBlocked;
        private RouteHintRevealPhase _loggedRevealPhase;
        private bool _fullMapDrawReportPending;
        private bool _reportedStaticMapCacheProblem;

        /// <summary>Состояние подсказки маршрута текущей миссии.</summary>
        public LevelRouteHintSession RouteHint => _routeHint;

        /// <summary>Текущая непрозрачность подсказки: 0 — скрыта, 1 — показана полностью.</summary>
        public float RouteHintAlpha => _routeHintAlpha;

        /// <summary>
        /// The one route-hint polyline (world XZ): the floor stars and the minimap dashes both draw exactly these
        /// points from the same visible start, and it is replaced only at a re-plan.
        /// </summary>
        public RouteHintTrail RouteHintTrail => _routeHintTrail;

        /// <summary>Последний проложенный путь подсказки в мировых координатах XZ (точки общей линии).</summary>
        public IReadOnlyList<Vector2> RouteHintWorldPath => _routeHintTrail.Points;

        /// <summary>How many times the path planner ran for the hint — re-plans are throttled, not per frame.</summary>
        internal int RouteHintPlanCount => _routeHintPlanCount;

        /// <summary>Test seam: stands in a hand-made level geometry after <c>Bind</c>.</summary>
        internal RouteHintGeometry RouteHintGeometryForTests => _routeHintGeometry;

        /// <summary>Where a world point lands on the given map projection (tests compare it with the drawn dashes).</summary>
        internal Vector2 ProjectRouteHintPoint(Vector2 world, MapProjection projection) =>
            ProjectWorld(world, projection);

        /// <summary>Исход последней прокладки пути подсказки (для диагностики и тестов).</summary>
        public RouteHintPathOutcome LastRouteHintPathOutcome { get; private set; } = RouteHintPathOutcome.NoTarget;

        /// <summary>
        /// Настройки, которые хозяин подставляет вместо ассета по умолчанию из <c>Resources/Minimap</c>: свой префаб
        /// цепочки подсказки на полу, свои цвета и тайминги. В пакете префаба подсказки нет — его даёт игра.
        /// </summary>
        public void SetSettings(LevelMinimapSettings value)
        {
            settings = value;
            // The collectible prefix may have changed: markers are re-read on the next scan.
            InvalidateFragmentCapsuleCache();
        }

        public LevelMinimapSettings Settings
        {
            get
            {
                if (settings != null)
                {
                    return settings;
                }

                if (_resolvedSettings != null)
                {
                    return _resolvedSettings;
                }

                _resolvedSettings = Resources.Load<LevelMinimapSettings>(LevelMinimapSettings.ResourcesPath);
                if (_resolvedSettings == null)
                {
                    // Изолированные тесты без ассета: те же значения по умолчанию, что и у ассета.
                    _resolvedSettings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
                    _resolvedSettings.hideFlags = HideFlags.HideAndDontSave;
                    _ownsResolvedSettings = true;
                }

                return _resolvedSettings;
            }
        }

        private float RoomRevealAnimationSeconds => Settings.RoomRevealAnimationSeconds;

        /// <summary>Чит: показать подсказку сразу, не дожидаясь простоя.</summary>
        public void SetRouteHintForced(bool forced) => _routeHint.SetForced(forced);

        /// <summary>
        /// Дверь открылась перед игроком: комната за ней проявляется на картах после задержки из настроек
        /// (владелец 2026-10-14: «при открытой двери комната не отрисовывается, только когда полностью
        /// зашёл» — это дефект). Посещённой для подсказки маршрута она от этого не становится, таймер
        /// простоя открытие двери не трогает: прогресс — только вход дальше по пути и задание цели.
        /// Живая дверь у связи одна (дверь From-комнаты; дубль в To-комнате игра выключает), и открывают её
        /// с обеих сторон — поэтому раскрывается комната по любую сторону двери
        /// (<see cref="LevelMinimapSession.ScheduleRevealAcrossDoor"/>), а не только «за» её владельцем.
        /// </summary>
        public void NotifyDoorOpened(string nodeId, string socketId)
        {
            if (!_session.HasActiveLevel || string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            float now = Time.unscaledTime;
            if (_session.ScheduleRevealAcrossDoor(nodeId, socketId, now + Settings.RoomRevealDelaySeconds) > 0)
            {
                UpdateRoomReveals(now);
            }
        }

        private void BindRouteHint(LevelLayout layout, bool preserveVisitedRooms)
        {
            _routeHint.Bind(layout, preserveVisitedRooms);
            if (!preserveVisitedRooms && !string.IsNullOrEmpty(_session.CurrentRoomId))
            {
                _routeHint.MarkRoomVisited(_session.CurrentRoomId);
            }
            RebuildRouteHintGeometry(layout);
            CollectTaskAnchors();
            _routeHintTrail.Clear();
            _routeHintAlpha = 0f;
            _routeHintDrawOn = 0f;
            _nextRouteHintReplanTime = 0f;
            _nextRouteHintLockCheckTime = 0f;
            _plannedRouteHintTargetVersion = -1;
            _plannedRouteHintRevealVersion = -1;
            _routeHintPlanned = false;
            _plannedRouteHintTaskActive = false;
            _routeHintTaskAnchor = null;
            _hasReportedRouteHintProblem = false;
            LastRouteHintPathOutcome = RouteHintPathOutcome.NoTarget;
            _routeHintBlockedSeconds = 0f;
            _lastRouteHintDiagnosticLogTime = float.NegativeInfinity;
            _hasLoggedRouteHintState = false;
            _routeHintLiveStartBlocked = false;
            _routeHintLiveStartBlockedSince = -1f;
            ResetRouteHintDiagnosticText("Waiting for first update");
            ResetRouteHintFloor();
        }

        private void ResetRouteHint()
        {
            UnsubscribeDoorOpenings();
            _routeHint.Reset();
            _routeHintTaskAnchor = null;
            _routeHintGeometry.Clear();
            _taskAnchorsByNodeId.Clear();
            _routeHintTrail.Clear();
            _routeHintAlpha = 0f;
            _routeHintDrawOn = 0f;
            _routeHintPlanned = false;
            _plannedRouteHintTaskActive = false;
            _hasReportedRouteHintProblem = false;
            _hasLoggedRouteHintState = false;
            _routeHintLiveStartBlocked = false;
            _routeHintLiveStartBlockedSince = -1f;
            ResetRouteHintDiagnosticText("No active level");
            ResetRouteHintFloor();
        }

        /// <summary>До первого кадра уровня строка диагностики — заглушка, а не собранная из старых входов.</summary>
        private void ResetRouteHintDiagnosticText(string fallback)
        {
            _routeHintDiagnosticFallbackText = fallback;
            _routeHintDiagnosticGameplay = null;
            _routeHintDiagnosticText = null;
            _routeHintDiagnosticTextExpiresAt = 0f;
        }

        /// <summary>
        /// Снимок состояния подсказки для чита и лога. В лог уходит ОДНА строка на смену состояния (игрок в
        /// мире или занят UI, исход решения, путь проложен, фаза пути на полу, большая карта, старт пунктира
        /// от игрока) и, как раньше, периодическая строка при долгой блокировке. Так WebGL-сборка сама
        /// показывает, на каком шаге подсказка не дошла до экрана (план b333 §2a).
        /// </summary>
        internal void UpdateRouteHintDiagnostics(IMinimapGameplayContext gameplay, RouteHintPresence presence,
            float deltaTime, float unscaledDeltaTime, float now)
        {
            bool playerInWorld = presence == RouteHintPresence.Exploring;
            bool enabled = Settings.RouteHintEnabled || _routeHint.IsForced;
            string blocker = RouteHintDiagnosticsUseCase.SelectBlocker(enabled, playerInWorld,
                "PlayerInputBlocked", _routeHint.Decision.Outcome, deltaTime > 0f, _routeHintPlanned);
            _routeHintBlockedSeconds = string.IsNullOrEmpty(blocker)
                ? 0f : _routeHintBlockedSeconds + unscaledDeltaTime;
            // A path that cannot be planned is explained once per change of cause (ReportRouteHintProblem);
            // repeating the state line every 15 s adds nothing but noise.
            bool log = blocker != PathNotPlannedBlocker
                       && RouteHintDiagnosticsUseCase.ShouldLog(_routeHintBlockedSeconds, now,
                           _lastRouteHintDiagnosticLogTime);
            // Пунктир у стены может мигать «не начать от игрока» на ходу; в лог — только устойчивое состояние.
            if (_routeHintLiveStartBlocked && _routeHintAlpha > 0f)
            {
                if (_routeHintLiveStartBlockedSince < 0f)
                {
                    _routeHintLiveStartBlockedSince = now;
                }
            }
            else
            {
                _routeHintLiveStartBlockedSince = -1f;
            }

            bool liveStartBlocked = _routeHintLiveStartBlockedSince >= 0f
                                    && now - _routeHintLiveStartBlockedSince >= LiveStartBlockedReportSeconds;
            bool changed = !_hasLoggedRouteHintState
                           || _loggedPresence != presence
                           || _loggedOutcome != _routeHint.Decision.Outcome
                           || _loggedPlanned != _routeHintPlanned
                           || _loggedRevealPhase != _routeHintReveal.Phase
                           || _loggedFullMapOpen != _session.IsFullMapOpen
                           || _loggedFullMapVisible != IsFullMapVisible
                           || _loggedLiveStartBlocked != liveStartBlocked;

            // Входы строки запоминаются каждый кадр (это присваивания, не форматирование); сама строка
            // собирается, только когда её пишут в лог или читают.
            _routeHintDiagnosticGameplay = gameplay;
            _routeHintDiagnosticPresence = presence;
            _routeHintDiagnosticBlocker = blocker;
            _routeHintDiagnosticEnabled = enabled;
            _routeHintDiagnosticLiveStartBlocked = liveStartBlocked;
            if (!log && !changed)
            {
                return;
            }

            string text = BuildRouteHintDiagnosticText();
            _routeHintDiagnosticText = text;
            _routeHintDiagnosticTextExpiresAt = now + RouteHintDiagnosticRefreshSeconds;
            if (changed)
            {
                LevelGenerationRuntimeLog.Info("Route hint state changed: " + text);
                _hasLoggedRouteHintState = true;
                _loggedPresence = presence;
                _loggedOutcome = _routeHint.Decision.Outcome;
                _loggedPlanned = _routeHintPlanned;
                _loggedRevealPhase = _routeHintReveal.Phase;
                _loggedFullMapOpen = _session.IsFullMapOpen;
                _loggedFullMapVisible = IsFullMapVisible;
                _loggedLiveStartBlocked = liveStartBlocked;
            }
            else if (log)
            {
                LevelGenerationRuntimeLog.Info(text);
            }

            if (log)
            {
                _lastRouteHintDiagnosticLogTime = now;
            }
        }

        /// <summary>Строка диагностики подсказки — один формат для лога и для чит-панели.</summary>
        private string BuildRouteHintDiagnosticText()
        {
            RouteHintDiagnosticBuildCount++;
            string control = _routeHintDiagnosticGameplay != null
                ? _routeHintDiagnosticGameplay.DescribeControl()
                : string.Empty;
            string fullMap = !_session.IsFullMapOpen ? "closed" : IsFullMapVisible ? "shown" : "hiddenByUi";
            RouteHintPresence presence = _routeHintDiagnosticPresence;
            bool liveStartBlocked = _routeHintDiagnosticLiveStartBlocked;
            bool enabled = _routeHintDiagnosticEnabled;
            string blocker = _routeHintDiagnosticBlocker;
            return FormattableString.Invariant(
                $"idle={_routeHint.IdleSeconds:0.0}/{_routeHint.IdleThresholdSeconds:0.0}s presence={presence} {control} outcome={_routeHint.Decision.Outcome} target={_routeHint.Decision.HasTarget} planned={_routeHintPlanned} path={LastRouteHintPathOutcome}{_routeHintFailureText} trailDev={_routeHintDeviationMeters:0.0}m floor={_routeHintReveal.Phase} alpha={_routeHintAlpha:0.00} liveStartBlocked={liveStartBlocked} fullMap={fullMap} enabled={enabled} forced={_routeHint.IsForced} blocker={blocker} blocked={_routeHintBlockedSeconds:0.0}s timeScale={Time.timeScale:0.00} focused={UnityEngine.Application.isFocused} cursor={Cursor.lockState} room={_session.CurrentRoomId}");
        }

        /// <summary>Logs idle resets caused by forward critical-path progress or completion of the target task.</summary>
        private void ReportRouteHintIdleRestart(string reason, float idleBefore)
        {
            if (idleBefore <= 0.5f)
            {
                return;
            }

            LevelGenerationRuntimeLog.Info(FormattableString.Invariant(
                $"Route hint idle timer restarted ({reason}) after {idleBefore:0.0}s of idle."));
        }

        private void ReportFullMapToggled(string source)
        {
            _fullMapDrawReportPending = _session.IsFullMapOpen;
            LevelGenerationRuntimeLog.Info(FormattableString.Invariant(
                $"Full map {(_session.IsFullMapOpen ? "opened" : "closed")} by {source}: presence={_playerPresence}, visibleRooms={_session.GetVisibleRooms().Count}, screen={Screen.width}x{Screen.height}."));
        }

        /// <summary>
        /// Первый Repaint большой карты после открытия пишет одну строку: построился ли кэш геометрии, сколько
        /// комнат рисовать и легла ли проекция. Пустая голубая рамка в браузере сразу видна в логе.
        /// </summary>
        private void ReportFullMapDrawOnce(bool cached, int roomCount, bool projected)
        {
            if (!_fullMapDrawReportPending || (Event.current != null && Event.current.type != EventType.Repaint))
            {
                return;
            }

            _fullMapDrawReportPending = false;
            StaticMapCacheStatus geometry = _fullMapCache.Status;
            bool textureCreated = _fullMapCache.Texture != null && _fullMapCache.Created;
            string problem = !cached ? "map rect too small, geometry cache skipped"
                : roomCount == 0 ? "no revealed rooms to draw"
                : geometry == StaticMapCacheStatus.MaterialUnavailable ? "GL material unavailable, walls are empty"
                : geometry == StaticMapCacheStatus.TextureNotCreated || !textureCreated
                    ? "geometry cache texture was not created"
                : !projected ? "projection failed, live layers and player are not drawn"
                : null;
            string message = FormattableString.Invariant(
                $"Full map first draw: geometry={geometry} textureCreated={textureCreated} rooms={roomCount} projection={projected} screen={Screen.width}x{Screen.height}{FullMapFitSummary()}");
            if (problem == null)
            {
                LevelGenerationRuntimeLog.Info(message + ".");
            }
            else
            {
                LevelGenerationRuntimeLog.Warn(message + ". The map looks empty: " + problem + ".");
            }
        }

        /// <summary>
        /// Вписанный прямоугольник показанных комнат и две самые дальние от его центра комнаты:
        /// сразу видно, что раздуло охват карты и сделало рисунок мелким. Раз на открытие карты.
        /// </summary>
        private string FullMapFitSummary()
        {
            IReadOnlyList<MinimapRoomSnapshot> rooms = RoomsForDrawing();
            if (!TryCalculateFullMapFit(rooms, ResolveLookYaw(), out Vector2 center, out float sizeMeters))
            {
                return " fit=none";
            }

            string firstId = "?";
            string secondId = "?";
            float firstDistance = -1f;
            float secondDistance = -1f;
            for (int i = 0; i < rooms.Count; i++)
            {
                Vector2[] corners = GetRoomGeometry(rooms[i]).Corners;
                float farthest = 0f;
                for (int c = 0; c < corners.Length; c++)
                {
                    farthest = Mathf.Max(farthest, Vector2.Distance(corners[c], center));
                }

                if (farthest > firstDistance)
                {
                    secondId = firstId;
                    secondDistance = firstDistance;
                    firstId = rooms[i].NodeId;
                    firstDistance = farthest;
                }
                else if (farthest > secondDistance)
                {
                    secondId = rooms[i].NodeId;
                    secondDistance = farthest;
                }
            }

            return FormattableString.Invariant($" fit=center({center.x:0.0},{center.y:0.0}) size={sizeMeters:0.0}m top={firstId}:{firstDistance:0.0}m,{secondId}:{secondDistance:0.0}m");
        }

        private void ReportStaticMapCacheProblemOnce(string reason)
        {
            if (_reportedStaticMapCacheProblem)
            {
                return;
            }

            _reportedStaticMapCacheProblem = true;
            LevelGenerationRuntimeLog.Warn($"Minimap geometry cache cannot be drawn: {reason}. Room walls stay empty; the route hint, "
                         + "player and markers are still drawn as live layers.");
        }

        private void SubscribeDoorOpenings()
        {
            if (_roomInstances == null)
            {
                return;
            }

            foreach (KeyValuePair<string, RoomInstance> kv in _roomInstances)
            {
                RoomInstance instance = kv.Value;
                if (instance == null || instance.Doors == null)
                {
                    continue;
                }

                string nodeId = kv.Key;
                for (int i = 0; i < instance.Doors.Count; i++)
                {
                    DoorSocket door = instance.Doors[i];
                    if (door == null)
                    {
                        continue;
                    }

                    Action<DoorSocket> handler = null;
                    handler = opened =>
                    {
                        if (this == null)
                        {
                            opened.OpenedForPlayer -= handler;
                            return;
                        }

                        NotifyDoorOpened(nodeId, opened.SocketId);
                    };
                    door.OpenedForPlayer += handler;
                    _doorSubscriptions.Add(new DoorOpeningSubscription(door, handler));
                }
            }
        }

        private void UnsubscribeDoorOpenings()
        {
            for (int i = 0; i < _doorSubscriptions.Count; i++)
            {
                DoorOpeningSubscription subscription = _doorSubscriptions[i];
                if (subscription.Door != null)
                {
                    subscription.Door.OpenedForPlayer -= subscription.Handler;
                }
            }

            _doorSubscriptions.Clear();
        }

        private void CollectTaskAnchors()
        {
            _taskAnchorsByNodeId.Clear();
            if (_roomInstances == null)
            {
                return;
            }

            foreach (KeyValuePair<string, RoomInstance> kv in _roomInstances)
            {
                if (kv.Value == null)
                {
                    continue;
                }

                IRoomTaskAnchor[] anchors = kv.Value.GetComponentsInChildren<IRoomTaskAnchor>(true);
                if (anchors.Length > 0)
                {
                    _taskAnchorsByNodeId[kv.Key] = anchors;
                }
            }
        }

        private bool TryGetIncompleteTaskPoint(string roomId, out Vector2 point)
        {
            point = default;
            if (!_taskAnchorsByNodeId.TryGetValue(roomId, out IRoomTaskAnchor[] anchors))
            {
                return false;
            }

            for (int i = 0; i < anchors.Length; i++)
            {
                IRoomTaskAnchor anchor = anchors[i];
                if (anchor == null || anchor.IsTaskCompleted || !anchor.TryGetTaskWorldPosition(out Vector3 world))
                {
                    continue;
                }

                point = new Vector2(world.x, world.z);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Якорь незавершённого задания для слежения за его завершением. Позиция не важна:
        /// выключенная панель не отдаёт позицию, но задание остаётся невыполненным.
        /// </summary>
        private bool TryGetIncompleteTaskAnchor(string roomId, out IRoomTaskAnchor anchor)
        {
            anchor = null;
            if (!_taskAnchorsByNodeId.TryGetValue(roomId, out IRoomTaskAnchor[] anchors))
            {
                return false;
            }

            for (int i = 0; i < anchors.Length; i++)
            {
                IRoomTaskAnchor candidate = anchors[i];
                if (candidate != null && !candidate.IsTaskCompleted)
                {
                    anchor = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Раскрывает комнаты с истёкшей задержкой и запекает в кэш геометрии комнаты, доигравшие
        /// проявление. Пока комната проявляется, она рисуется живым слоем поверх кэша, а сам кэш ради
        /// анимации не перестраивается.
        /// </summary>
        private void UpdateRoomReveals(float now)
        {
            if (_session.RevealDueRooms(now) > 0)
            {
                MarkStaticMapDirty();
            }

            if (_session.PruneFinishedRevealAnimations(now, RoomRevealAnimationSeconds) > 0)
            {
                MarkStaticMapDirty();
            }
        }

        /// <summary>
        /// Задание цели выполнено между кадрами: это прогресс — отсчёт простоя заново, подсказка
        /// прячется до нового простоя. Срабатывает только когда тот самый якорь, за которым следили,
        /// сообщает о завершении: выключенная панель (нет позиции) — не завершение, таймер идёт дальше.
        /// </summary>
        private void DetectRouteHintTaskCompletion()
        {
            if (!_routeHint.Decision.HasTarget)
            {
                _routeHintTaskAnchor = null;
                return;
            }

            CriticalPathDoor target = _routeHint.Decision.TargetDoor;
            if (!_routeHintTaskTarget.Equals(target))
            {
                _routeHintTaskAnchor = null;
                _routeHintTaskTarget = target;
            }

            if (_routeHintTaskAnchor != null)
            {
                if (_routeHintTaskAnchor.IsTaskCompleted)
                {
                    float idleBefore = _routeHint.IdleSeconds;
                    _routeHintTaskAnchor = null;
                    if (_routeHint.NoteTargetTaskCompleted(true) != RouteHintProgress.NoProgress)
                    {
                        ReportRouteHintIdleRestart($"target task in '{target.FromNodeId}' completed", idleBefore);
                    }
                }

                return;
            }

            if (TryGetIncompleteTaskAnchor(target.FromNodeId, out IRoomTaskAnchor anchor))
            {
                _routeHintTaskAnchor = anchor;
            }
        }

        /// <summary>
        /// Таймер простоя идёт по игровому времени и только пока игрок в мире
        /// (<see cref="RouteHintPresenceUseCase"/>: ни панель, ни свободная мышь его не держат; WebGL-ожидание
        /// захвата курсора браузером занятостью не считается). Пока игрока держит UI, подсказка гаснет: её
        /// место занимает панель, которой он сейчас занят. Сначала путь проявляется на полу, и только по
        /// окончании проявления включается пунктир на миникарте (<see cref="DecideRouteHintRevealUseCase"/>).
        /// Reveal and floor effects use scaled time; hiding an unwanted hint uses unscaled time.
        /// </summary>
        public void UpdateRouteHint(float deltaTime, float now, bool playerInControl,
            float? unscaledNow = null, float? unscaledDeltaTime = null)
        {
            LevelMinimapSettings config = Settings;
            _routeHint.IdleThresholdSeconds = config.RouteHintIdleSeconds;
            _routeHint.Tick(deltaTime, playerInControl);
            if (_routeHint.Decision.Outcome == RouteHintOutcome.NoCriticalPath)
            {
                ReportRouteHintProblem(NoCriticalPathProblemCode, _layout?.StartNodeId, null);
            }

            bool taskActive = false;
            if (_routeHint.Decision.HasTarget)
            {
                taskActive = TryGetIncompleteTaskPoint(_routeHint.Decision.TargetDoor.FromNodeId,
                    out Vector2 _);
            }

            DetectRouteHintTaskCompletion();

            bool visible = (config.RouteHintEnabled || _routeHint.IsForced)
                           && _routeHint.Decision.ShowHint
                           && playerInControl;
            if (visible)
            {
                visible = UpdateRouteHintTrail(config, now, taskActive);
            }

            // Сначала путь на полу, потом пунктир на миникарте: пока лента проявляется, карта ждёт.
            bool minimapVisible = AdvanceRouteHintFloor(visible, now, unscaledNow ?? now) && visible;
            float alphaStep = visible
                ? deltaTime / config.RouteHintFadeSeconds
                : (unscaledDeltaTime ?? deltaTime) / config.RouteHintFloorHideFadeSeconds;
            _routeHintAlpha = Mathf.MoveTowards(_routeHintAlpha, minimapVisible ? 1f : 0f, alphaStep);
            if (minimapVisible)
            {
                _routeHintDrawOn = Mathf.MoveTowards(_routeHintDrawOn, 1f,
                    deltaTime / config.RouteHintDrawOnSeconds);
            }
            else if (_routeHintAlpha <= 0f)
            {
                _routeHintDrawOn = 0f;
                if (!visible)
                {
                    _routeHintTrail.Clear();
                    _routeHintPlanned = false;
                    _nextRouteHintReplanTime = 0f;
                }
            }
        }

        /// <summary>
        /// Keeps the shared trail current. A player on the trail only drags its visible start along it (every
        /// frame, no allocation); the planner runs again when the world changed (target, revealed rooms, task,
        /// locked doors) or when he walked farther than the deviation limit from the trail, and then not more
        /// often than the throttle in the settings allows (<see cref="DecideRouteHintReplanUseCase"/>).
        /// Returns whether a trail is available to show.
        /// </summary>
        private bool UpdateRouteHintTrail(LevelMinimapSettings config, float now, bool taskActive)
        {
            bool worldChanged = _plannedRouteHintTargetVersion != _routeHint.TargetVersion
                                || _plannedRouteHintRevealVersion != _session.RevealVersion
                                || _plannedRouteHintTaskActive != taskActive;
            if (!worldChanged && _routeHintPlanned && now >= _nextRouteHintLockCheckTime)
            {
                _nextRouteHintLockCheckTime = now + config.RouteHintReplanIntervalSeconds;
                worldChanged = ComputeTransitLockSignature() != _routeHintPlannedLockSignature;
            }

            if (_routeHintPlanned && _routeHintTrail.IsBlending)
            {
                _routeHintTrail.AdvanceBlend(now);
            }

            RouteHintTrailFollow follow = default;
            Transform player = ResolvePlayerTransform();
            if (_routeHintPlanned && player != null)
            {
                follow = _routeHintTrail.Follow(new Vector2(player.position.x, player.position.z),
                    config.RouteHintPathStartOffsetMeters, config.RouteHintPathMinVisibleMeters,
                    config.RouteHintReplanDeviationMeters);
                _routeHintDeviationMeters = follow.DeviationMeters;
            }

            RouteHintReplanReason reason = DecideRouteHintReplanUseCase.Execute(new RouteHintReplanInput(
                _routeHintPlanned && follow.HasTrail, worldChanged, follow.HasTrail ? follow.DeviationMeters : 0f,
                config.RouteHintReplanDeviationMeters, now, _nextRouteHintReplanTime));
            if (reason == RouteHintReplanReason.None)
            {
                return _routeHintPlanned;
            }

            if (_plannedRouteHintTargetVersion != _routeHint.TargetVersion)
            {
                _hasReportedRouteHintProblem = false;
            }

            if (_plannedRouteHintTargetVersion != _routeHint.TargetVersion
                || _plannedRouteHintTaskActive != taskActive)
            {
                // A new target is a new floor reveal; a re-plan of the same target only refreshes the points.
                unchecked
                {
                    _routeHintFloorPathVersion++;
                }
            }

            _routeHintPlanned = ReplanRouteHint(reason == RouteHintReplanReason.PlayerLeftTrail ? now : (float?)null);
            _plannedRouteHintTaskActive = taskActive;
            _plannedRouteHintTargetVersion = _routeHint.TargetVersion;
            _plannedRouteHintRevealVersion = _session.RevealVersion;
            _routeHintPlannedLockSignature = ComputeTransitLockSignature();
            _nextRouteHintReplanTime = now + (_routeHintPlanned
                ? config.RouteHintReplanIntervalSeconds
                : Mathf.Max(2f, config.RouteHintReplanIntervalSeconds));
            return _routeHintPlanned;
        }

        /// <summary>Changes whenever a transit door becomes locked or unlocked, so the trail avoids closed ways.</summary>
        private int ComputeTransitLockSignature()
        {
            int signature = 17;
            IReadOnlyList<RouteHintDoor> doors = _routeHintGeometry.Doors;
            for (int i = 0; i < doors.Count; i++)
            {
                if (IsTransitDoorLocked(doors[i]))
                {
                    signature = unchecked(signature * 31 + i + 1);
                }
            }

            return signature;
        }

        /// <param name="blendNow">
        /// Set when only the player left the line (the world is the same): the new line melts into the shown one
        /// instead of replacing it at once. <c>null</c> — a plain replacement.
        /// </param>
        private bool ReplanRouteHint(float? blendNow = null)
        {
            Transform player = ResolvePlayerTransform();
            if (player == null)
            {
                _routeHintTrail.Clear();
                return false;
            }

            LevelMinimapSettings config = Settings;
            RouteHintDecision decision = _routeHint.Decision;
            Vector2 playerPosition = new(player.position.x, player.position.z);
            RouteHintPathOptions options = config.CreateRouteHintPathOptions();
            _routeHintPlanCount++;
            RouteHintPathOutcome outcome = TryGetIncompleteTaskPoint(decision.TargetDoor.FromNodeId,
                    out Vector2 taskPoint)
                ? PlanRouteHintPathUseCase.ExecuteToRoomTask(_routeHintGeometry, _session.IsRoomRevealed,
                    playerPosition, _session.CurrentRoomId, decision.TargetDoor.FromNodeId, taskPoint,
                    _routeHintPlanScratch, IsTransitDoorLocked, options)
                : PlanRouteHintPathUseCase.Execute(_routeHintGeometry, _session.IsRoomRevealed, playerPosition,
                    _session.CurrentRoomId, decision.TargetDoor, _routeHintPlanScratch, IsTransitDoorLocked,
                    options);
            LastRouteHintPathOutcome = outcome;
            _routeHintFailureText = string.Empty;
            if (outcome == RouteHintPathOutcome.MissingGeometry
                || outcome == RouteHintPathOutcome.Unreachable
                || outcome == RouteHintPathOutcome.PlayerRoomUnknown)
            {
                RouteHintFailure failure = PlanRouteHintPathUseCase.Diagnose(_routeHintGeometry,
                    _session.IsRoomRevealed, playerPosition, _session.CurrentRoomId, decision.TargetDoor,
                    IsTransitDoorLocked, outcome);
                _routeHintFailureText = " cause=" + failure.Describe();
                ReportRouteHintProblem((int)outcome, decision.TargetDoor.FromNodeId, decision.TargetDoor.ToNodeId,
                    failure.Key, failure.Describe());
            }

            if (outcome != RouteHintPathOutcome.Planned || _routeHintPlanScratch.Count < 2)
            {
                _routeHintTrail.Clear();
                return false;
            }

            RouteHintTrailBuilder.Build(_routeHintGeometry, _routeHintPlanScratch,
                config.CreateRouteHintSmoothingShape(), LevelMinimapSettings.RouteHintClearanceCapMeters,
                config.RouteHintPathStartOffsetMeters, config.RouteHintPathMinVisibleMeters, _routeHintTrail,
                blendNow ?? 0f, blendNow.HasValue ? config.RouteHintReplanBlendSeconds : 0f);
            _routeHintTrail.Follow(playerPosition, config.RouteHintPathStartOffsetMeters,
                config.RouteHintPathMinVisibleMeters, float.MaxValue);
            return true;
        }

        /// <summary>
        /// Закрыт ли проход связи. Проход держит ОДНА живая дверь — дверь From-комнаты (<c>NodeA</c>): игра
        /// оставляет её, а дубль в To-комнате выключает (<see cref="DoorState.Disabled"/>) и прячет. Раньше
        /// проверялись обе стороны, и выключенный дубль делал запертым каждый проход — из боковой комнаты или
        /// тупика подсказка не находила дороги назад. Сторона To закрывает проход, только если её дверь живая
        /// (включена, активна, не выключена) и заперта.
        /// </summary>
        private bool IsTransitDoorLocked(RouteHintDoor door) =>
            IsLiveSocketBlockedForTransit(door.NodeA, door.SocketA)
            || IsDuplicateSocketLockedForTransit(door.NodeB, door.SocketB);

        /// <summary>Живая дверь связи заперта или выключена (по состоянию сокета сцены, без сцены — раскладки).</summary>
        private bool IsLiveSocketBlockedForTransit(string nodeId, string socketId)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(socketId))
            {
                return false;
            }

            if (_roomInstances != null && _roomInstances.TryGetValue(nodeId, out RoomInstance instance)
                && instance != null)
            {
                DoorSocket socket = instance.FindSocket(socketId);
                return socket != null && (socket.State == DoorState.Locked || socket.State == DoorState.Disabled);
            }

            return _placedByNodeId.TryGetValue(nodeId, out PlacedRoom placed)
                   && placed.DoorStates != null
                   && placed.DoorStates.TryGetValue(socketId, out DoorState state)
                   && (state == DoorState.Locked || state == DoorState.Disabled);
        }

        /// <summary>
        /// Сторона To связи: выключенный или спрятанный дубль проход не закрывает — закрывает только живая
        /// запертая дверь. Без сцены «выключена» в раскладке значит то же: дубля, а не закрытого прохода.
        /// </summary>
        private bool IsDuplicateSocketLockedForTransit(string nodeId, string socketId)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(socketId))
            {
                return false;
            }

            if (_roomInstances != null && _roomInstances.TryGetValue(nodeId, out RoomInstance instance)
                && instance != null)
            {
                IReadOnlyList<DoorSocket> doors = instance.Doors;
                for (int i = 0; doors != null && i < doors.Count; i++)
                {
                    DoorSocket door = doors[i];
                    if (door != null
                        && string.Equals(door.SocketId, socketId, StringComparison.Ordinal)
                        && door.enabled
                        && door.gameObject.activeSelf
                        && door.State == DoorState.Locked)
                    {
                        return true;
                    }
                }

                return false;
            }

            return _placedByNodeId.TryGetValue(nodeId, out PlacedRoom placed)
                   && placed.DoorStates != null
                   && placed.DoorStates.TryGetValue(socketId, out DoorState state)
                   && state == DoorState.Locked;
        }

        /// <summary>
        /// Одно предупреждение на смену исхода и цели — не на каждый кадр и пересчёт пути. Повтор
        /// отсекается сравнением кода и идентификаторов ДО сборки строк: `NoCriticalPath` проверяется
        /// каждый кадр, и строка на кадр была бы постоянным мусором.
        /// </summary>
        private void ReportRouteHintProblem(int code, string fromNodeId, string toNodeId, int causeKey = 0,
            string cause = null)
        {
            if (_hasReportedRouteHintProblem
                && _reportedRouteHintProblemCode == code
                && _reportedRouteHintProblemCause == causeKey
                && string.Equals(_reportedRouteHintProblemFrom, fromNodeId, StringComparison.Ordinal)
                && string.Equals(_reportedRouteHintProblemTo, toNodeId, StringComparison.Ordinal))
            {
                return;
            }

            _hasReportedRouteHintProblem = true;
            _reportedRouteHintProblemCode = code;
            _reportedRouteHintProblemCause = causeKey;
            _reportedRouteHintProblemFrom = fromNodeId;
            _reportedRouteHintProblemTo = toNodeId;
            string outcome = code == NoCriticalPathProblemCode
                ? nameof(RouteHintOutcome.NoCriticalPath)
                : ((RouteHintPathOutcome)code).ToString();
            LevelGenerationRuntimeLog.Warn($"Route hint unavailable: {outcome}, target '{fromNodeId}' -> "
                         + $"'{toNodeId}', current room '{_session.CurrentRoomId}'"
                         + (string.IsNullOrEmpty(cause) ? "." : $"; cause: {cause}."));
        }

        private void RebuildRouteHintGeometry(LevelLayout layout)
        {
            _routeHintGeometry.Clear();
            IReadOnlyList<MinimapRoomSnapshot> rooms = _session.GetAllRooms();
            Dictionary<string, MinimapRoomSnapshot> roomsById = new(StringComparer.Ordinal);
            for (int i = 0; i < rooms.Count; i++)
            {
                MinimapRoomSnapshot room = rooms[i];
                RouteHintWall[] walls = Array.Empty<RouteHintWall>();
                if (_wallSegmentsByNodeId.TryGetValue(room.NodeId, out IReadOnlyList<MinimapWallSegment> segments))
                {
                    walls = new RouteHintWall[segments.Count];
                    for (int j = 0; j < segments.Count; j++)
                    {
                        walls[j] = new RouteHintWall(segments[j].A, segments[j].B, segments[j].WidthMeters);
                    }
                }

                _routeHintGeometry.AddRoom(room.NodeId, new Vector2(room.X, room.Y),
                    room.RotationDegrees, room.Cells, walls);
                roomsById[rooms[i].NodeId] = rooms[i];
            }

            for (int i = 0; i < layout.Connections.Count; i++)
            {
                DoorConnection connection = layout.Connections[i];
                if (TryResolveConnectionPoint(connection, roomsById, out Vector2 point))
                {
                    float openingWidth = DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(DoorSize.Medium);
                    if (_roomInstances != null
                        && _roomInstances.TryGetValue(connection.FromNodeId, out RoomInstance instance)
                        && instance != null)
                    {
                        DoorSocket socket = instance.FindSocket(connection.FromSocketId);
                        if (socket != null)
                        {
                            openingWidth = ResolveDoorFrameWidth(instance, socket);
                        }
                    }

                    _routeHintGeometry.AddDoor(connection.FromNodeId, connection.FromSocketId, connection.ToNodeId,
                        connection.ToSocketId, point, openingWidth);
                }
            }

            AddRouteHintObstacles(rooms);
        }

        /// <summary>
        /// Сплошные предметы на полу каждой комнаты сцены — в геометрию подсказки: путь обходит их контуры с тем же
        /// запасом, что и стены, и не режет стул или стол насквозь. Пол комнаты — высота её корня (комнаты
        /// ставятся генератором полом на сетку); стены миникарты (их корни у шаблона) уже стены и сюда не идут.
        /// Предметы, появившиеся после связывания уровня, в снимок не попадают.
        /// </summary>
        private void AddRouteHintObstacles(IReadOnlyList<MinimapRoomSnapshot> rooms)
        {
            if (_roomInstances == null)
            {
                return;
            }

            float stepMeters = Settings.RouteHintFloorMaxStepMeters;
            List<Transform> excludedRoots = new();
            List<RouteHintObstacle> obstacles = new();
            for (int i = 0; i < rooms.Count; i++)
            {
                MinimapRoomSnapshot room = rooms[i];
                if (!_roomInstances.TryGetValue(room.NodeId, out RoomInstance instance) || instance == null)
                {
                    continue;
                }

                CollectWallRoots(instance, excludedRoots);
                float floorArea = 0f;
                for (int c = 0; c < room.Cells.Count; c++)
                {
                    floorArea += room.Cells[c].Width * room.Cells[c].Depth;
                }

                RouteHintObstacleCollector.Collect(instance.transform, instance.transform.position.y, floorArea,
                    stepMeters, excludedRoots, obstacles);
                for (int o = 0; o < obstacles.Count; o++)
                {
                    _routeHintGeometry.AddObstacle(room.NodeId, obstacles[o]);
                }
            }
        }

        /// <summary>
        /// Корни стен миникарты шаблона, кроме корня самой комнаты: когда стены не выделены в отдельный корень,
        /// исключить весь корень значило бы исключить и мебель.
        /// </summary>
        private static void CollectWallRoots(RoomInstance instance, List<Transform> output)
        {
            output.Clear();
            RoomTemplate template = instance.Template;
            if (template == null)
            {
                return;
            }

            Transform roomRoot = instance.transform;
            if (template.MinimapWallParent != null && template.MinimapWallParent.transform != roomRoot)
            {
                output.Add(template.MinimapWallParent.transform);
            }

            IReadOnlyList<Transform> roots = template.MinimapWallRoots;
            for (int i = 0; roots != null && i < roots.Count; i++)
            {
                if (roots[i] != null && roots[i] != roomRoot)
                {
                    output.Add(roots[i]);
                }
            }
        }

        /// <summary>
        /// Точка двери связи: центр проёма из дескриптора живого сокета (та же точка, что у маркеров
        /// дверей), без сцены — по имени сокета. Неизвестный проём не заменяем прямой через стену.
        /// </summary>
        private bool TryResolveConnectionPoint(
            DoorConnection connection,
            IReadOnlyDictionary<string, MinimapRoomSnapshot> roomsById,
            out Vector2 point)
        {
            if (TryResolveSocketWorldCenter(connection.FromNodeId, connection.FromSocketId, out point)
                || TryResolveSocketWorldCenter(connection.ToNodeId, connection.ToSocketId, out point))
            {
                return true;
            }

            bool hasFrom = roomsById.TryGetValue(connection.FromNodeId ?? string.Empty, out MinimapRoomSnapshot from);
            bool hasTo = roomsById.TryGetValue(connection.ToNodeId ?? string.Empty, out MinimapRoomSnapshot to);
            if (hasFrom && TryBuildFallbackDoor(from, connection.FromSocketId, out point, out _))
            {
                return true;
            }

            if (hasTo && TryBuildFallbackDoor(to, connection.ToSocketId, out point, out _))
            {
                return true;
            }

            point = default;
            return false;
        }

        private bool TryResolveSocketWorldCenter(string nodeId, string socketId, out Vector2 point)
        {
            point = default;
            if (_roomInstances == null || string.IsNullOrEmpty(nodeId)
                || !_roomInstances.TryGetValue(nodeId, out RoomInstance instance) || instance == null)
            {
                return false;
            }

            DoorSocket socket = instance.FindSocket(socketId);
            if (socket == null)
            {
                return false;
            }

            point = ResolveDoorWorldCenter(instance, socket);
            return true;
        }

        /// <summary>
        /// Проявление только что раскрытой комнаты: цвет вспыхивает от белого к своему, прозрачность
        /// быстро набирается. Вне проявления (<see cref="_roomRevealProgress"/> = 1) цвет не меняется.
        /// </summary>
        private Color ApplyRoomRevealTint(Color color)
        {
            float t = _roomRevealProgress;
            if (t >= 1f)
            {
                return color;
            }

            float eased = t * t * (3f - 2f * t);
            return new Color(
                Mathf.Lerp(1f, color.r, eased),
                Mathf.Lerp(1f, color.g, eased),
                Mathf.Lerp(1f, color.b, eased),
                color.a * Mathf.Clamp01(t * 1.8f));
        }

        /// <summary>
        /// Всё, что живёт поверх кэша геометрии, с ОДНОЙ проекцией на кадр: проявляющиеся комнаты,
        /// подсказка маршрута, маркер выхода, колбы. Рисует только Repaint.
        /// </summary>
        private void DrawLiveLayers(IReadOnlyList<MinimapRoomSnapshot> rooms, MapProjection projection, bool fullMap,
            Rect mapRect)
        {
            if (Event.current != null && Event.current.type != EventType.Repaint)
            {
                return;
            }

            DrawRevealingRooms(rooms, projection, fullMap, mapRect);
            DrawRouteHint(rooms, projection, fullMap);
            DrawGoalMarker(rooms, projection, fullMap);
            DrawFragmentMarkers(projection, fullMap);
        }

        /// <summary>
        /// Комнаты, которые ещё проявляются, и их связи — отдельным слоем поверх кэша (см.
        /// <see cref="BakeAndDrawRevealLayer"/>). В кэш они попадают один раз, когда проявление доиграло
        /// (<see cref="UpdateRoomReveals"/>).
        /// </summary>
        private void DrawRevealingRooms(IReadOnlyList<MinimapRoomSnapshot> rooms, MapProjection projection, bool fullMap,
            Rect mapRect)
        {
            if (!_session.HasRevealAnimations)
            {
                return;
            }

            try
            {
                BakeAndDrawRevealLayer(rooms, projection, fullMap, mapRect);
            }
            finally
            {
                _roomRevealProgress = 1f;
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// Геометрия проявляющихся комнат в слой проявления (GL): у каждой комнаты свой цвет проявления
        /// (<see cref="ApplyRoomRevealTint"/>), поэтому прогресс выставляется перед каждой.
        /// </summary>
        private void DrawRevealingRoomGeometry(IReadOnlyList<MinimapRoomSnapshot> rooms, MapProjection projection,
            bool fullMap)
        {
            float now = Time.unscaledTime;
            float duration = RoomRevealAnimationSeconds;
            for (int i = 0; i < rooms.Count; i++)
            {
                MinimapRoomSnapshot room = rooms[i];
                if (!_session.IsRevealAnimating(room.NodeId))
                {
                    continue;
                }

                _roomRevealProgress = _session.GetRevealProgress(room.NodeId, now, duration);
                DrawRoomGeometry(room, projection, fullMap);
                if (drawRoomConnections && !simplifiedDrawing)
                {
                    DrawRevealingConnections(rooms, room, projection, fullMap);
                }
            }

            _roomRevealProgress = 1f;
        }

        private void DrawRevealingConnections(
            IReadOnlyList<MinimapRoomSnapshot> rooms,
            MinimapRoomSnapshot room,
            MapProjection projection,
            bool fullMap)
        {
            if (_layout?.Connections == null)
            {
                return;
            }

            Color shadow = new(0f, 0f, 0f, fullMap ? 0.56f : 0.46f);
            Color color = new(connectionColor.r, connectionColor.g, connectionColor.b,
                fullMap ? connectionColor.a : connectionColor.a * 0.82f);
            for (int i = 0; i < _layout.Connections.Count; i++)
            {
                DoorConnection connection = _layout.Connections[i];
                string otherId = string.Equals(connection.FromNodeId, room.NodeId, StringComparison.Ordinal)
                    ? connection.ToNodeId
                    : string.Equals(connection.ToNodeId, room.NodeId, StringComparison.Ordinal)
                        ? connection.FromNodeId
                        : null;
                if (otherId == null || !TryFindRoom(rooms, otherId, out MinimapRoomSnapshot other))
                {
                    continue;
                }

                Vector2 a = ProjectWorld(GetRoomGeometry(room).Center, projection);
                Vector2 b = ProjectWorld(GetRoomGeometry(other).Center, projection);
                DrawLine(a, b, 3.6f, shadow);
                DrawLine(a, b, 1.5f, color);
            }
        }

        private static bool TryFindRoom(IReadOnlyList<MinimapRoomSnapshot> rooms, string nodeId, out MinimapRoomSnapshot room)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (string.Equals(rooms[i].NodeId, nodeId, StringComparison.Ordinal))
                {
                    room = rooms[i];
                    return true;
                }
            }

            room = default;
            return false;
        }

        private void DrawRouteHint(IReadOnlyList<MinimapRoomSnapshot> rooms, MapProjection projection, bool fullMap)
        {
            if (_routeHintAlpha <= 0.001f || !_routeHintTrail.HasPoints)
            {
                return;
            }

            LevelMinimapSettings config = Settings;
            EnsureRouteHintTextures(config);
            float sizeScale = fullMap ? config.RouteHintFullMapSizeMultiplier : 1f;
            BuildRouteHintScreenWalls(rooms, projection, MaxRouteHintRequestedRadiusPixels(config, sizeScale));
            float totalLength = BuildRouteHintScreenPath(projection, fullMap);
            if (totalLength <= 1f)
            {
                return;
            }

            float drawOn = 1f - Mathf.Pow(1f - _routeHintDrawOn, 3f);
            float visibleLength = totalLength * drawOn;
            float startGap = PlayerMarkerPixelSize(projection.Scale) * 0.55f;
            float time = Time.time;

            if (config.RouteHintStyle == RouteHintStyle.Line)
            {
                DrawRouteHintLine(config, visibleLength, startGap, sizeScale, time);
            }
            else
            {
                DrawRouteHintDots(config, visibleLength, startGap, sizeScale, time);
            }

            float targetAlpha = Mathf.Clamp01((_routeHintDrawOn - 0.75f) / 0.25f);
            if (targetAlpha > 0f)
            {
                DrawRouteHintTarget(config, _routeHintScreenPath[_routeHintScreenPath.Count - 1], sizeScale, time,
                    targetAlpha);
            }

            GUI.color = Color.white;
        }

        /// <summary>
        /// Наибольший радиус, который пунктир (или линия) может попросить у стен на этой карте: точка с волной
        /// размера и кантом, ореол линии. Стены дальше этого радиуса (с толщиной штриха и зазором) не могут
        /// уменьшить ни одну точку, и проецировать их незачем.
        /// </summary>
        internal static float MaxRouteHintRequestedRadiusPixels(LevelMinimapSettings config, float sizeScale)
        {
            float dot = config.RouteHintDotRadiusPixels * sizeScale * (0.8f + 0.35f) / (1f - RouteHintDotRimFraction);
            float width = config.RouteHintLineWidthPixels * sizeScale;
            float line = Mathf.Max(width * 3.2f, width + 2f, width * 1.2f) * 0.5f;
            return Mathf.Max(dot, line);
        }

        /// <summary>
        /// Экранные стены, о которые подгоняется пунктир. Не все стены показанных комнат, а только ближние к
        /// линии подсказки: их отбор (<see cref="RefreshRouteHintNearWalls"/>) делается в мире один раз на смену
        /// линии или раскрытых комнат и отсортирован по расстоянию до охвата линии. Здесь берутся лишь те, что
        /// ближе досягаемости точки на этом масштабе, — остальные не влияют ни на один подогнанный радиус
        /// (минимум по стенам не меняется от стен, которые дальше запрошенного радиуса), поэтому результат тот же,
        /// что при проекции всех стен.
        /// </summary>
        internal void BuildRouteHintScreenWalls(IReadOnlyList<MinimapRoomSnapshot> rooms, MapProjection projection,
            float maxRequestedRadiusPixels)
        {
            _routeHintScreenWalls.Clear();
            RefreshRouteHintNearWalls(rooms);
            float reachMeters = (maxRequestedRadiusPixels + RouteHintScreenClearance.MaxWallReachPaddingPixels)
                                / Mathf.Max(0.0001f, projection.Scale);
            for (int i = 0; i < _routeHintNearWalls.Count; i++)
            {
                RouteHintNearWall near = _routeHintNearWalls[i];
                if (near.DistanceMeters > reachMeters)
                {
                    break;
                }

                RouteHintWall wall = near.Wall;
                float strokeRadius = RouteHintScreenClearance.WallStrokeRadiusPixels(wall.WidthMeters, projection.Scale);
                _routeHintScreenWalls.Add(new RouteHintScreenWall(
                    ProjectWorld(wall.A, projection), ProjectWorld(wall.B, projection), strokeRadius));
            }
        }

        /// <summary>Экранные стены последней сборки (тестам: сравнить с проекцией всех стен).</summary>
        internal IReadOnlyList<RouteHintScreenWall> RouteHintScreenWalls => _routeHintScreenWalls;

        /// <summary>
        /// Стены показанных комнат с расстоянием от каждой до охвата линии подсказки (мир, метры), по возрастанию.
        /// Пересобирается при смене точек линии, раскрытых комнат или геометрии подсказки — не каждый кадр и не
        /// на каждую карту. Линия и её видимая часть лежат внутри охвата своих точек, поэтому расстояние до охвата
        /// — нижняя граница расстояния от любой точки пунктира до стены.
        /// </summary>
        private void RefreshRouteHintNearWalls(IReadOnlyList<MinimapRoomSnapshot> rooms)
        {
            if (_routeHintNearWallsRevision == _routeHintTrail.Revision
                && _routeHintNearWallsRevealVersion == _session.RevealVersion
                && _routeHintNearWallsGeometryVersion == _routeHintGeometry.Version
                && ReferenceEquals(_routeHintNearWallsRooms, rooms)
                && _routeHintNearWallsRoomCount == rooms.Count)
            {
                return;
            }

            _routeHintNearWallsRevision = _routeHintTrail.Revision;
            _routeHintNearWallsRevealVersion = _session.RevealVersion;
            _routeHintNearWallsGeometryVersion = _routeHintGeometry.Version;
            _routeHintNearWallsRooms = rooms;
            _routeHintNearWallsRoomCount = rooms.Count;
            _routeHintNearWalls.Clear();
            IReadOnlyList<Vector2> points = _routeHintTrail.Points;
            if (points.Count == 0)
            {
                return;
            }

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                minX = Mathf.Min(minX, points[i].x);
                minY = Mathf.Min(minY, points[i].y);
                maxX = Mathf.Max(maxX, points[i].x);
                maxY = Mathf.Max(maxY, points[i].y);
            }

            Rect bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            for (int i = 0; i < rooms.Count; i++)
            {
                if (!_routeHintGeometry.TryGetRoom(rooms[i].NodeId, out RouteHintRoom room))
                {
                    continue;
                }

                IReadOnlyList<RouteHintWall> walls = room.Walls;
                for (int j = 0; j < walls.Count; j++)
                {
                    RouteHintWall wall = walls[j];
                    _routeHintNearWalls.Add(new RouteHintNearWall(wall,
                        RouteHintScreenClearance.SegmentDistanceToRect(wall.A, wall.B, bounds)));
                }
            }

            _routeHintNearWalls.Sort(RouteHintNearWallComparer.Instance);
        }

        private readonly struct RouteHintNearWall
        {
            public readonly RouteHintWall Wall;
            public readonly float DistanceMeters;

            public RouteHintNearWall(RouteHintWall wall, float distanceMeters)
            {
                Wall = wall;
                DistanceMeters = distanceMeters;
            }
        }

        /// <summary>Сортировка стен по расстоянию до линии; один экземпляр без состояния — без аллокаций на сортировку.</summary>
        private sealed class RouteHintNearWallComparer : IComparer<RouteHintNearWall>
        {
            public static readonly RouteHintNearWallComparer Instance = new();

            public int Compare(RouteHintNearWall x, RouteHintNearWall y) => x.DistanceMeters.CompareTo(y.DistanceMeters);
        }

        /// <summary>Screen points of the drawn trail: its visible start, then every shared point beyond it.</summary>
        internal IReadOnlyList<Vector2> RouteHintScreenPath => _routeHintScreenPath;

        internal float BuildRouteHintScreenPath(MapProjection projection, bool fullMap)
        {
            _routeHintScreenPath.Clear();
            _routeHintScreenDistances.Clear();
            if (!fullMap)
            {
                _routeHintLiveStartBlocked = false;
            }

            _routeHintTrail.FillVisible(_routeHintVisibleScratch);
            if (_routeHintVisibleScratch.Count < 2)
            {
                return 0f;
            }

            float total = 0f;
            for (int i = 0; i < _routeHintVisibleScratch.Count; i++)
            {
                Vector2 point = ProjectWorld(_routeHintVisibleScratch[i], projection);
                if (i > 0)
                {
                    total += Vector2.Distance(_routeHintScreenPath[_routeHintScreenPath.Count - 1], point);
                }

                _routeHintScreenPath.Add(point);
                _routeHintScreenDistances.Add(total);
            }

            return total;
        }

        private Vector2 RouteHintPointAt(float distance, ref int segment)
        {
            int last = _routeHintScreenPath.Count - 1;
            while (segment < last - 1 && _routeHintScreenDistances[segment + 1] < distance)
            {
                segment++;
            }

            float start = _routeHintScreenDistances[segment];
            float length = _routeHintScreenDistances[segment + 1] - start;
            float t = length > 0.0001f ? Mathf.Clamp01((distance - start) / length) : 0f;
            return Vector2.Lerp(_routeHintScreenPath[segment], _routeHintScreenPath[segment + 1], t);
        }

        /// <summary>
        /// Пунктир бежит от игрока к цели: точки сдвигаются со скоростью потока, а по ним катится волна
        /// размера — так направление читается даже на неподвижном кадре. Кант запечён в текстуру точки:
        /// один <c>DrawTexture</c> на точку.
        /// </summary>
        private void DrawRouteHintDots(
            LevelMinimapSettings config,
            float visibleLength,
            float startGap,
            float sizeScale,
            float time)
        {
            float spacing = config.RouteHintDotSpacingPixels * sizeScale;
            float radius = config.RouteHintDotRadiusPixels * sizeScale;
            float phase = config.RouteHintFlowSpeedPixelsPerSecond * sizeScale * time % spacing;
            int segment = 0;
            int drawn = 0;
            for (float distance = startGap + phase;
                 distance <= visibleLength && drawn < MaxRouteHintDots;
                 distance += spacing, drawn++)
            {
                Vector2 center = RouteHintPointAt(distance, ref segment);
                float wave = 0.5f + 0.5f * Mathf.Sin(distance / spacing * 0.9f - time * 6f);
                float headFade = Mathf.Clamp01((visibleLength - distance) / spacing + 0.35f);
                float outerRadius = radius * (0.8f + 0.35f * wave) / (1f - RouteHintDotRimFraction);
                outerRadius = RouteHintScreenClearance.FitDotRadius(center, outerRadius, _routeHintScreenWalls);
                if (outerRadius <= 0f)
                {
                    continue;
                }
                DrawRouteHintSprite(_routeHintDotTexture, center, outerRadius, Color.white,
                    _routeHintAlpha * headFade);
            }
        }

        /// <summary>Сплошная линия с ореолом и бегущим к цели бликом.</summary>
        private void DrawRouteHintLine(
            LevelMinimapSettings config,
            float visibleLength,
            float startGap,
            float sizeScale,
            float time)
        {
            float width = config.RouteHintLineWidthPixels * sizeScale;
            Color color = WithAlpha(config.RouteHintColor, _routeHintAlpha);
            Color outline = WithAlpha(config.RouteHintOutlineColor, _routeHintAlpha);
            Color glow = WithAlpha(config.RouteHintColor, config.RouteHintColor.a * 0.22f * _routeHintAlpha);
            DrawRouteHintSpan(startGap, visibleLength, width * 3.2f, glow);
            DrawRouteHintSpan(startGap, visibleLength, width + 2f, outline);
            DrawRouteHintSpan(startGap, visibleLength, width, color);

            float travel = Mathf.Max(1f, visibleLength - startGap);
            float highlightLength = Mathf.Min(travel, 18f * sizeScale);
            float head = startGap + config.RouteHintFlowSpeedPixelsPerSecond * 2f * sizeScale * time % travel;
            Color highlight = Color.Lerp(config.RouteHintColor, Color.white, 0.65f);
            DrawRouteHintSpan(head, Mathf.Min(visibleLength, head + highlightLength), width * 1.2f,
                WithAlpha(highlight, _routeHintAlpha));
        }

        private void DrawRouteHintSpan(float from, float to, float thickness, Color color)
        {
            if (to - from <= 0.5f)
            {
                return;
            }

            int segment = 0;
            Vector2 previous = RouteHintPointAt(from, ref segment);
            for (int i = segment + 1; i < _routeHintScreenPath.Count; i++)
            {
                float distance = _routeHintScreenDistances[i];
                if (distance >= to)
                {
                    break;
                }

                if (distance <= from)
                {
                    continue;
                }

                DrawSafeRouteHintLine(previous, _routeHintScreenPath[i], thickness, color);
                previous = _routeHintScreenPath[i];
            }

            DrawSafeRouteHintLine(previous, RouteHintPointAt(to, ref segment), thickness, color);
        }

        private void DrawSafeRouteHintLine(Vector2 from, Vector2 to, float thickness, Color color)
        {
            float safeThickness = RouteHintScreenClearance.FitLineThickness(from, to, thickness,
                _routeHintScreenWalls);
            if (safeThickness > 0f)
            {
                DrawLine(from, to, safeThickness, color);
            }
        }

        /// <summary>У цели — точка и расходящееся от неё кольцо: «сюда».</summary>
        private void DrawRouteHintTarget(
            LevelMinimapSettings config,
            Vector2 center,
            float sizeScale,
            float time,
            float appear)
        {
            float radius = config.RouteHintTargetMarkerRadiusPixels * sizeScale;
            float pulse = time / config.RouteHintTargetPulseSeconds % 1f;
            float alpha = _routeHintAlpha * appear;
            DrawRouteHintSprite(_routeHintRingTexture, center, radius * (1f + 0.9f * pulse), config.RouteHintColor,
                alpha * (1f - pulse));
            DrawRouteHintSprite(_routeHintDotTexture, center, radius * 0.62f / (1f - RouteHintDotRimFraction),
                Color.white, alpha);
        }

        private void DrawRouteHintSprite(Texture2D texture, Vector2 center, float radius, Color color, float alpha)
        {
            if (texture == null || radius <= 0.1f || alpha <= 0.001f)
            {
                return;
            }

            Rect rect = new(center.x - radius, center.y - radius, radius * 2f, radius * 2f);
            if (_hasActiveMapClipRect && !rect.Overlaps(_activeMapClipRect))
            {
                return;
            }

            GUI.color = WithAlpha(color, color.a * alpha);
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true);
        }

        private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

        /// <summary>
        /// Спрайты подсказки со сглаженным краем. Точка запекается сразу в цветах настроек — заливка и
        /// тёмный кант, — и перезапекается, только если дизайнер сменил цвета.
        /// </summary>
        private void EnsureRouteHintTextures(LevelMinimapSettings config)
        {
            if (_routeHintDotTexture == null
                || _routeHintDotTextureFill != config.RouteHintColor
                || _routeHintDotTextureRim != config.RouteHintOutlineColor)
            {
                DestroyOwnedObject(ref _routeHintDotTexture);
                _routeHintDotTextureFill = config.RouteHintColor;
                _routeHintDotTextureRim = config.RouteHintOutlineColor;
                _routeHintDotTexture = CreateDotTexture(_routeHintDotTextureFill, _routeHintDotTextureRim);
            }

            if (_routeHintRingTexture == null)
            {
                _routeHintRingTexture = CreateRingTexture();
            }
        }

        private static Texture2D CreateDotTexture(Color fill, Color rim)
        {
            float center = (RouteHintTextureSize - 1) * 0.5f;
            float outer = RouteHintTextureSize * 0.5f - 1f;
            float inner = outer * (1f - RouteHintDotRimFraction);
            return CreateCircleTexture(distance =>
            {
                float coverage = Mathf.Clamp01(outer - distance + 0.5f);
                float fillWeight = Mathf.Clamp01(inner - distance + 0.5f);
                Color color = Color.Lerp(rim, fill, fillWeight);
                color.a *= coverage;
                return color;
            }, center);
        }

        private static Texture2D CreateRingTexture()
        {
            float center = (RouteHintTextureSize - 1) * 0.5f;
            float outer = RouteHintTextureSize * 0.5f - 1f;
            const float ringThickness = 3.2f;
            return CreateCircleTexture(distance => new Color(1f, 1f, 1f,
                Mathf.Clamp01(1f - Mathf.Abs(distance - (outer - ringThickness * 0.5f)) / (ringThickness * 0.5f))), center);
        }

        private static Texture2D CreateCircleTexture(Func<float, Color> shade, float center)
        {
            Texture2D texture = new(RouteHintTextureSize, RouteHintTextureSize, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color32[] pixels = new Color32[RouteHintTextureSize * RouteHintTextureSize];
            for (int y = 0; y < RouteHintTextureSize; y++)
            {
                for (int x = 0; x < RouteHintTextureSize; x++)
                {
                    float distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                    pixels[y * RouteHintTextureSize + x] = shade(distance);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void DestroyRouteHintResources()
        {
            DestroyOwnedObject(ref _routeHintDotTexture);
            DestroyOwnedObject(ref _routeHintRingTexture);
            if (_ownsResolvedSettings)
            {
                DestroyOwnedObject(ref _resolvedSettings);
                _ownsResolvedSettings = false;
            }
        }

        private static void DestroyOwnedObject<T>(ref T target) where T : UnityEngine.Object
        {
            if (target == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (!UnityEngine.Application.isPlaying)
            {
                DestroyImmediate(target);
            }
            else
#endif
            {
                Destroy(target);
            }

            target = null;
        }

        private readonly struct DoorOpeningSubscription
        {
            public readonly DoorSocket Door;
            public readonly Action<DoorSocket> Handler;

            public DoorOpeningSubscription(DoorSocket door, Action<DoorSocket> handler)
            {
                Door = door;
                Handler = handler;
            }
        }

    }

    public readonly struct RouteHintScreenWall
    {
        public readonly Vector2 A;
        public readonly Vector2 B;
        public readonly float StrokeRadius;

        public RouteHintScreenWall(Vector2 a, Vector2 b, float strokeRadius)
        {
            A = a;
            B = b;
            StrokeRadius = strokeRadius;
        }
    }

    public static class RouteHintScreenClearance
    {
        private const float ScreenGapPixels = 0.75f;
        private const float MinimumVisibleRadiusPixels = 0.65f;

        /// <summary>Наибольший полуштрих стены миникарты: (4 + 2) / 2 пикселя.</summary>
        private const float MaxWallStrokeRadiusPixels = 3f;

        /// <summary>
        /// Сколько к запрошенному радиусу точки добавить, чтобы стена дальше этого расстояния точно не влияла на
        /// подгонку: полуштрих стены, зазор и пиксель запаса на округление проекции.
        /// </summary>
        public const float MaxWallReachPaddingPixels = MaxWallStrokeRadiusPixels + ScreenGapPixels + 1f;

        /// <summary>Полуштрих стены на экране — так же, как её рисует миникарта (тень на 2 px шире линии).</summary>
        public static float WallStrokeRadiusPixels(float wallWidthMeters, float pixelsPerMeter) =>
            (Mathf.Clamp(wallWidthMeters * pixelsPerMeter, 1.8f, 4f) + 2f) * 0.5f;

        /// <summary>Расстояние от отрезка до прямоугольника (0, если пересекаются или отрезок внутри).</summary>
        public static float SegmentDistanceToRect(Vector2 a, Vector2 b, Rect rect)
        {
            if (rect.Contains(a) || rect.Contains(b))
            {
                return 0f;
            }

            Vector2 c0 = new(rect.xMin, rect.yMin);
            Vector2 c1 = new(rect.xMax, rect.yMin);
            Vector2 c2 = new(rect.xMax, rect.yMax);
            Vector2 c3 = new(rect.xMin, rect.yMax);
            float best = Mathf.Min(
                Mathf.Min(SegmentDistanceSquared(a, b, c0, c1), SegmentDistanceSquared(a, b, c1, c2)),
                Mathf.Min(SegmentDistanceSquared(a, b, c2, c3), SegmentDistanceSquared(a, b, c3, c0)));
            return Mathf.Sqrt(best);
        }

        public static float AvailableRadius(Vector2 from, Vector2 to, IReadOnlyList<RouteHintScreenWall> walls)
        {
            float available = float.MaxValue;
            for (int i = 0; i < walls.Count; i++)
            {
                RouteHintScreenWall wall = walls[i];
                float distance = Mathf.Sqrt(SegmentDistanceSquared(from, to, wall.A, wall.B));
                available = Mathf.Min(available, distance - wall.StrokeRadius - ScreenGapPixels);
            }

            return available;
        }

        public static float FitDotRadius(Vector2 point, float requested,
            IReadOnlyList<RouteHintScreenWall> walls)
        {
            float fitted = Mathf.Min(requested, AvailableRadius(point, point, walls));
            return fitted >= MinimumVisibleRadiusPixels ? fitted : 0f;
        }

        public static float FitLineThickness(Vector2 from, Vector2 to, float requested,
            IReadOnlyList<RouteHintScreenWall> walls)
        {
            float fitted = Mathf.Min(requested, AvailableRadius(from, to, walls) * 2f);
            return fitted >= MinimumVisibleRadiusPixels ? fitted : 0f;
        }

        private static float SegmentDistanceSquared(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 ab = b - a;
            Vector2 cd = d - c;
            float cross = ab.x * cd.y - ab.y * cd.x;
            if (Mathf.Abs(cross) > 0.000001f)
            {
                Vector2 ca = c - a;
                float alongA = (ca.x * cd.y - ca.y * cd.x) / cross;
                float alongC = (ca.x * ab.y - ca.y * ab.x) / cross;
                if (alongA >= 0f && alongA <= 1f && alongC >= 0f && alongC <= 1f)
                {
                    return 0f;
                }
            }

            return Mathf.Min(
                Mathf.Min(PointDistanceSquared(a, c, d), PointDistanceSquared(b, c, d)),
                Mathf.Min(PointDistanceSquared(c, a, b), PointDistanceSquared(d, a, b)));
        }

        private static float PointDistanceSquared(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 span = b - a;
            float fraction = span.sqrMagnitude > 0.0001f
                ? Mathf.Clamp01(Vector2.Dot(point - a, span) / span.sqrMagnitude) : 0f;
            return (point - a - span * fraction).sqrMagnitude;
        }
    }
}
