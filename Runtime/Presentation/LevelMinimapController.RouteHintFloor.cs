using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Runtime.Application;
using RedoSchool.LevelGeneration.Runtime.Infrastructure;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Путь подсказки маршрута на полу (план b333 §2b). Контроллер только подаёт входы: нужна ли подсказка
    /// сейчас (<see cref="LevelRouteHintSession"/>), общая линия подсказки (<see cref="RouteHintTrail"/>,
    /// построенная из <see cref="PlanRouteHintPathUseCase"/>) и пол под её точками
    /// (<see cref="RouteHintFloorRaycastProbe"/>). Порядок «пол, потом миникарта» решает
    /// <see cref="DecideRouteHintRevealUseCase"/> (состояние — <see cref="RouteHintRevealTimeline"/>), рисует
    /// <see cref="RouteHintFloorView"/>. Звёзды на полу и пунктир миникарты читают ОДНИ И ТЕ ЖЕ точки.
    /// </summary>
    public sealed partial class LevelMinimapController
    {
        private readonly RouteHintRevealTimeline _routeHintReveal = new();
        private readonly RouteHintRibbonMeshData _routeHintRibbon = new();
        private readonly HashSet<RouteHintFloorProblem> _reportedRouteHintFloorProblems = new();
        private RouteHintFloorView _routeHintFloorView;
        private int _routeHintFloorPathVersion;
        private int _routeHintFloorFailedVersion = int.MinValue;
        private int _routeHintFloorBuiltRevision = -1;
        private RouteHintFloorRaycastProbe _routeHintFloorProbe;
        private RouteHintFloorProbe _routeHintFloorProbeCallback;
        private Transform _routeHintFloorProbePlayer;
        private int _routeHintFloorProbeLayers;
        private float _routeHintFloorProbeHeight;
        private readonly List<float> _routeHintFloorHeightsScratch = new();

        /// <summary>
        /// Сколько раз пол под подсказкой прощупывался лучами: один раз на перепланирование, а не на каждый кадр
        /// смешивания старой линии с новой (страж — EditMode-тест).
        /// </summary>
        internal int RouteHintFloorProbeBuildCount { get; private set; }

        private enum RouteHintFloorBuild
        {
            Built,

            /// <summary>Пути ещё нет — попробовать на следующем кадре, ленту не отключать.</summary>
            PathNotReady,

            /// <summary>Лента по этому пути не строится — до смены цели пунктир на миникарте сразу.</summary>
            Failed
        }

        private enum RouteHintFloorProblem
        {
            NoPrefab,
            NoMaterial,
            DegenerateRibbon,
            TooComplexRibbon,
            PathNotReady
        }

        /// <summary>Фаза показа подсказки: проявление на полу, удержание, угасание.</summary>
        public RouteHintRevealPhase RouteHintRevealPhase => _routeHintReveal.Phase;

        /// <summary>Экземпляр ленты на полу текущего уровня (создаётся при первом показе).</summary>
        public RouteHintFloorView RouteHintFloorView => _routeHintFloorView;

        /// <summary>
        /// Полилиния, по которой рисуются звёзды на полу (мировые XZ) — те же точки, что у пунктира миникарты;
        /// оставлено для диагностики и тестов.
        /// </summary>
        public IReadOnlyList<Vector2> RouteHintFloorPolyline => _routeHintTrail.Points;

        /// <summary>
        /// Привязывает вьюшку ленты напрямую. В игре экземпляр создаётся сам из
        /// <see cref="LevelMinimapSettings.RouteHintFloorViewPrefab"/> при первом показе; сеттер — типизированная
        /// граница для EditMode-тестов и стендов, где Play Mode нет.
        /// </summary>
        public void SetRouteHintFloorView(RouteHintFloorView view)
        {
            if (_routeHintFloorView != null && _routeHintFloorView != view)
            {
                _routeHintFloorView.Hide();
            }

            _routeHintFloorView = view;
            _routeHintReveal.Reset();
            _routeHintFloorFailedVersion = int.MinValue;
            _routeHintFloorBuiltRevision = -1;
        }

        private void OnDisable() => SuspendRouteHintFloor();

        /// <summary>
        /// Контроллер выключен (чит скрыл миникарту, уровень гаснет): лента на полу не должна застыть.
        /// </summary>
        private void SuspendRouteHintFloor()
        {
            _routeHintReveal.Reset();
            if (_routeHintFloorView != null)
            {
                _routeHintFloorView.Hide();
            }
        }

        /// <summary>
        /// Шаг показа ленты. Возвращает, можно ли уже показывать пунктир на миникарте: пока лента
        /// проявляется на полу — нельзя; ленты нет (выключена, нет префаба или материала, не построилась) —
        /// можно сразу. Открыт тестам (InternalsVisibleTo): ветка «пути ещё нет» через кадр карты недостижима,
        /// но обязана повторять показ, а не отключать ленту.
        /// </summary>
        internal bool AdvanceRouteHintFloor(bool hintVisible, float now, float? unscaledNow = null)
        {
            LevelMinimapSettings config = Settings;
            RouteHintRevealTiming timing = config.CreateRouteHintRevealTiming();
            bool floorAvailable = IsRouteHintFloorAvailable(config, hintVisible);
            float remaining = _routeHintTrail.RemainingMeters;
            RouteHintRevealFrame frame = _routeHintReveal.Advance(
                new RouteHintRevealInput(hintVisible, floorAvailable, _routeHintFloorPathVersion, remaining, now, unscaledNow),
                timing);
            if (frame.StartedReveal)
            {
                RouteHintFloorBuild build = RebuildRouteHintFloor(config, false);
                if (build == RouteHintFloorBuild.PathNotReady)
                {
                    // Пути нет — не отключаем ленту, а пробуем снова: следующий кадр начнёт проявление заново.
                    _routeHintReveal.Reset();
                    _nextRouteHintReplanTime = 0f;
                    if (_routeHintFloorView != null)
                    {
                        _routeHintFloorView.Hide();
                    }

                    return false;
                }

                if (build == RouteHintFloorBuild.Failed)
                {
                    _routeHintFloorFailedVersion = _routeHintFloorPathVersion;
                    frame = _routeHintReveal.Advance(
                        new RouteHintRevealInput(hintVisible, false, _routeHintFloorPathVersion, remaining, now, unscaledNow),
                        timing);
                }

                if (build == RouteHintFloorBuild.Built)
                {
                    ReportRouteHintFloorBuilt();
                }
            }
            else if (hintVisible && _routeHintFloorView != null && _routeHintFloorView.HasPath
                     && _routeHintFloorBuiltRevision != _routeHintTrail.Revision
                     && frame.Phase != RouteHintRevealPhase.Hidden && frame.Phase != RouteHintRevealPhase.FloorDone)
            {
                // The trail was re-planned (the student left it) or is melting into the new plan: the stars follow
                // the points without restarting the reveal. Floor heights are probed once per re-plan
                // (RouteHintTrail.PlanVersion); the frames of a blend only move the points in XZ.
                if (RebuildRouteHintFloor(config, true) == RouteHintFloorBuild.Failed)
                {
                    _routeHintFloorFailedVersion = _routeHintFloorPathVersion;
                    frame = _routeHintReveal.Advance(
                        new RouteHintRevealInput(hintVisible, false, _routeHintFloorPathVersion, remaining, now, unscaledNow),
                        timing);
                }
            }

            if (_routeHintFloorView != null)
            {
                if (frame.Phase == RouteHintRevealPhase.Hidden || frame.Phase == RouteHintRevealPhase.FloorDone)
                {
                    _routeHintFloorView.Hide();
                }
                else
                {
                    _routeHintFloorView.SetHead(_routeHintTrail.HeadDistance);
                    _routeHintFloorView.Render(frame.FloorReveal, frame.FloorAlpha, now, config);
                }
            }

            return frame.MinimapVisible;
        }

        private bool IsRouteHintFloorAvailable(LevelMinimapSettings config, bool hintVisible)
        {
            if (!config.RouteHintFloorEnabled || _routeHintFloorFailedVersion == _routeHintFloorPathVersion)
            {
                return false;
            }

            if (_routeHintFloorView == null)
            {
                if (!hintVisible)
                {
                    return false;
                }

                if (config.RouteHintFloorViewPrefab == null)
                {
                    // Без префаба лента не появится никогда — одной строкой на уровень, а не молча.
                    ReportRouteHintFloorProblemOnce(RouteHintFloorProblem.NoPrefab,
                        "LevelMinimapSettings has no RouteHintFloorViewPrefab");
                    return false;
                }

                // Экземпляр ленты создаётся только в игре и только когда подсказка действительно нужна:
                // редакторские тесты и стенды без Play Mode получают прежнее поведение — пунктир сразу.
                if (!UnityEngine.Application.isPlaying)
                {
                    return false;
                }

                _routeHintFloorView = Instantiate(config.RouteHintFloorViewPrefab, transform);
                _routeHintFloorView.name = "RouteHintFloorView";
            }

            if (_routeHintFloorView.HasMaterial)
            {
                return true;
            }

            // Проверка до форматирования: метод зовётся каждый кадр, пока подсказка видна.
            if (!_reportedRouteHintFloorProblems.Contains(RouteHintFloorProblem.NoMaterial))
            {
                ReportRouteHintFloorProblemOnce(RouteHintFloorProblem.NoMaterial,
                    $"ribbon '{_routeHintFloorView.name}' has no material");
            }

            return false;
        }

        /// <summary>
        /// Звёзды получают те же точки, что рисует миникарта: общую линию <see cref="RouteHintTrail"/> без
        /// пересглаживания и пересэмплирования. Здесь только добавляется высота пола под каждой точкой
        /// (<see cref="RouteHintFloorRaycastProbe"/>). Лучи к полу — один раз на план: на начале показа и после
        /// перепланирования; кадры смешивания линий (<see cref="RouteHintTrail.AdvanceBlend"/>) сдвигают точки
        /// только по XZ и берут высоты плана из линии.
        /// </summary>
        private RouteHintFloorBuild RebuildRouteHintFloor(LevelMinimapSettings config, bool refresh)
        {
            if (_routeHintFloorView == null)
            {
                return RouteHintFloorBuild.Failed;
            }

            if (!_routeHintTrail.HasPoints)
            {
                ReportRouteHintFloorProblemOnce(RouteHintFloorProblem.PathNotReady,
                    "no planned path at the start of the reveal, retrying");
                return RouteHintFloorBuild.PathNotReady;
            }

            bool built = BuildRouteHintRibbon(config, out RouteHintRibbonFailure failure);
            if (built && (refresh
                    ? _routeHintFloorView.UpdatePath(_routeHintRibbon, _routeHintTrail.Clearances)
                    : _routeHintFloorView.Show(_routeHintRibbon, _routeHintTrail.Clearances, config)))
            {
                _routeHintFloorBuiltRevision = _routeHintTrail.Revision;
                return RouteHintFloorBuild.Built;
            }

            RouteHintFloorProblem problem = failure == RouteHintRibbonFailure.TooComplex
                ? RouteHintFloorProblem.TooComplexRibbon
                : RouteHintFloorProblem.DegenerateRibbon;
            if (!_reportedRouteHintFloorProblems.Contains(problem))
            {
                ReportRouteHintFloorProblemOnce(problem,
                    $"ribbon not built from {_routeHintTrail.Points.Count} path points");
            }

            return RouteHintFloorBuild.Failed;
        }

        /// <summary>
        /// Лента под текущими точками линии. Высоты пола плана ещё не известны — лучи прощупывают пол под
        /// точками ПЛАНА (куда линия придёт, а не где она сейчас в середине смешивания), и высоты остаются в
        /// линии; дальше лента строится по ним без единого луча. Без смешивания точки плана и есть текущие, и
        /// результат совпадает с прежним построением по лучам.
        /// </summary>
        private bool BuildRouteHintRibbon(LevelMinimapSettings config, out RouteHintRibbonFailure failure)
        {
            RouteHintRibbonShape shape = config.CreateRouteHintRibbonShape();
            if (!_routeHintTrail.HasFloorHeights)
            {
                Transform player = ResolvePlayerTransform();
                EnsureRouteHintFloorProbe(config, player);
                IReadOnlyList<Vector2> planned = _routeHintTrail.PlannedPoints;
                float playerHeight = player != null ? player.position.y : 0f;
                float startFloor = _routeHintFloorProbe.Probe(planned[0], playerHeight, out float underStart)
                    ? underStart : playerHeight;
                RouteHintFloorProbeBuildCount++;
                if (!RouteHintRibbonMeshBuilder.BuildFromAxis(planned, shape, startFloor,
                        _routeHintFloorProbeCallback, _routeHintRibbon, out failure, _routeHintFloorHeightsScratch))
                {
                    return false;
                }

                _routeHintTrail.SetFloorHeights(_routeHintFloorHeightsScratch);
                if (!_routeHintTrail.IsBlending)
                {
                    return true;
                }
            }

            return RouteHintRibbonMeshBuilder.BuildFromAxisAtHeights(_routeHintTrail.Points,
                _routeHintTrail.FloorHeights, shape, _routeHintRibbon, out failure);
        }

        private void EnsureRouteHintFloorProbe(LevelMinimapSettings config, Transform player)
        {
            if (_routeHintFloorProbe != null && _routeHintFloorProbePlayer == player
                && _routeHintFloorProbeLayers == config.RouteHintFloorLayers.value
                && _routeHintFloorProbeHeight == config.RouteHintFloorProbeHeightMeters)
            {
                return;
            }

            _routeHintFloorProbe = new RouteHintFloorRaycastProbe(config.RouteHintFloorLayers,
                config.RouteHintFloorProbeHeightMeters, player);
            _routeHintFloorProbeCallback = _routeHintFloorProbe.Probe;
            _routeHintFloorProbePlayer = player;
            _routeHintFloorProbeLayers = config.RouteHintFloorLayers.value;
            _routeHintFloorProbeHeight = config.RouteHintFloorProbeHeightMeters;
        }

        /// <summary>
        /// Одна строка на начало каждого показа ленты: геометрия, длина и положение против игрока.
        /// Следующий «ленты не видно» репорт разбирается по ней, а не по фазам таймлайна.
        /// </summary>
        private void ReportRouteHintFloorBuilt()
        {
            Transform player = ResolvePlayerTransform();
            Vector3 playerPosition = player != null ? player.position : Vector3.zero;
            Vector3 center = Vector3.zero;
            if (_routeHintRibbon.Centerline.Count > 0)
            {
                for (int i = 0; i < _routeHintRibbon.Centerline.Count; i++)
                {
                    center += _routeHintRibbon.Centerline[i];
                }

                center /= _routeHintRibbon.Centerline.Count;
            }

            LevelGenerationRuntimeLog.Info(FormattableString.Invariant($"Route hint floor ribbon shown: points={_routeHintTrail.Points.Count} verts={_routeHintRibbon.Vertices.Count} tris={_routeHintRibbon.Triangles.Count / 3} length={_routeHintRibbon.LengthMeters:0.0}m head={_routeHintTrail.HeadDistance:0.0}m center=({center.x:0.0},{center.y:0.0},{center.z:0.0}) player=({playerPosition.x:0.0},{playerPosition.y:0.0},{playerPosition.z:0.0})."));
        }

        /// <summary>
        /// Одна строка на причину за уровень (нет префаба, нет материала, вырожденная лента, путь не готов):
        /// лента не пропадает молча.
        /// </summary>
        private void ReportRouteHintFloorProblemOnce(RouteHintFloorProblem problem, string details)
        {
            if (!_reportedRouteHintFloorProblems.Add(problem))
            {
                return;
            }

            string effect = problem == RouteHintFloorProblem.PathNotReady
                ? "the ribbon will be retried"
                : "the minimap shows the route right away";
            LevelGenerationRuntimeLog.Warn($"Route hint floor ribbon: {problem} ({details}); {effect}.");
        }

        private void ResetRouteHintFloor()
        {
            _routeHintReveal.Reset();
            _routeHintFloorFailedVersion = int.MinValue;
            _routeHintFloorBuiltRevision = -1;
            _routeHintFloorProbe = null;
            _routeHintFloorProbeCallback = null;
            _routeHintFloorProbePlayer = null;
            _reportedRouteHintFloorProblems.Clear();
            if (_routeHintFloorView != null)
            {
                _routeHintFloorView.Hide();
            }
        }
    }
}
