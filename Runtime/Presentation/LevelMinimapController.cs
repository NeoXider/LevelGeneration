using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using RedoSchool.Features.LevelGeneration.Runtime.Application;
using RedoSchool.Features.LevelGeneration.Runtime.Domain.Events;
using UnityEngine;

// Кадр карты без устройств ввода (TickFrame) — типизированная граница для EditMode-тестов, без reflection.
[assembly: InternalsVisibleTo("RedoSchool.LevelGeneration.Tests.EditMode")]

namespace RedoSchool.Features.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Runtime minimap overlay. The compact map follows the player; the full map fits room bounds.
    /// Both maps project the player marker from the actual world position. Unvisited rooms are intentionally hidden by <see cref="LevelMinimapSession"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Обе карты показывают одно и то же — только пройденные комнаты.</b> Большая карта
    /// раньше рисовала уровень целиком: открыв её, ученик сразу видел, где выход, где тупики и
    /// сколько ещё идти. Исследовать после этого было нечего, а разница между картами оказывалась
    /// не в честности, а в масштабе.</para>
    ///
    /// <para>Правило распространяется на ВСЁ содержимое: комнаты, связи между ними, маркер выхода и
    /// колбы-фрагменты. Достаточно оставить непройденной одну из этих сущностей — и она выдаст
    /// комнату, которой на карте нет: коридор ведёт в пустоту, а колба висит в темноте.</para>
    ///
    /// <para>Большая карта не стала бесполезной: она не вращается вслед за игроком и вписывает
    /// показанный (пройденный) маршрут целиком с полями, тогда как угловая держит игрока в
    /// центре и показывает ближайшее окружение.</para>
    /// </remarks>
    public sealed partial class LevelMinimapController : MonoBehaviour
    {
        /// <summary>Базовая сторона маркера игрока в пикселях до authored-масштаба содержимого.</summary>
        public const float PlayerMarkerPixels = 22f;

        /// <summary>
        /// Во сколько раз содержимое БОЛЬШОЙ карты мельче, чем на компактной. Одно объявление на
        /// оба места: и зум геометрии, и размер маркера игрока — иначе маркер перерастёт коридоры.
        /// </summary>
        public const float FullMapZoomDivider = 2f;

        /// <summary>
        /// Доля меньшей стороны карточки, уходящая в её рамку с каждого края. Объявлена один раз:
        /// по ней считается и внутренняя область карты, и зум большой карты, и они обязаны совпадать.
        /// </summary>
        public const float CardInsetFraction = 0.13f;

        /// <summary>
        /// Во сколько раз стрелка игрока на КОМПАКТНОЙ карте крупнее согласованного размера
        /// содержимого. Компактную карту видно постоянно, и именно на ней владелец не находил
        /// стрелку глазами. Потолок роста задан не вкусом, а геометрией: маркер обязан помещаться
        /// внутрь одной ячейки сетки (<see cref="LevelGenerationGridMetrics.CellSizeMeters"/> — это
        /// минимальная комната), иначе он снова «вылезает за стены». Сторож — `LevelMinimapFramingTests`.
        /// </summary>
        public const float CompactPlayerMarkerBoost = 1.25f;

        /// <summary>
        /// Минимальный радиус отметки колбы на ПОЛНОЙ карте в ЭКРАННЫХ пикселях. Размер отметки
        /// намеренно не следует за зумом мира: <see cref="ContentMarkerScale"/> на полной карте
        /// делится на <see cref="FullMapZoomDivider"/> и вырождал кружок в 3 px — ровно тот дефект,
        /// который владелец описал как «отметок колб почти не видно». Точечный ориентир обязан быть
        /// замечен, а не совпасть по размеру с комнатой, поэтому нижняя граница задана экраном.
        /// </summary>
        public const float FullMapFragmentMarkerMinRadiusPixels = 8f;

        /// <summary>
        /// Доля от радиуса отметки полной карты, остающаяся на КОМПАКТНОЙ. Меньше единицы by design:
        /// компактная карточка вчетверо мельче по площади, и отметка в полный размер накрыла бы
        /// собой комнату, в которой стоит. Через долю (а не через отдельный размер) — чтобы связь
        /// «компактная строго мельче полной» держалась при любой правке базового радиуса.
        /// </summary>
        public const float CompactFragmentMarkerRadiusFraction = 0.55f;

        /// <summary>Базовый радиус отметки колбы до authored-масштаба содержимого.</summary>
        private const float FragmentMarkerBaseRadiusPixels = 12f;

        /// <summary>
        /// Толщина тёмного канта отметки колбы (доля радиуса и абсолютный минимум). Кант обязателен:
        /// геометрия карты сама сине-голубая и светлая, и одноцветный кружок без подложки тонет в
        /// стенах — из-за этого отметки и «не читались», хотя рисовались.
        /// </summary>
        private const float FragmentMarkerOutlineFraction = 0.28f;

        private const float FragmentMarkerMinOutlinePixels = 1.5f;

        /// <summary>
        /// Как часто пересобирается список колб в комнатах. Не «один раз при Bind»: колбы спавнит
        /// <c>CodeFragmentRuntimeController</c> ПОСЛЕ построения уровня, и снятый при связывании
        /// список остался бы пустым навсегда. Но и не каждый кадр: <c>OnGUI</c> зовут по нескольку
        /// раз за кадр, и обход всех комнат на каждый event — это тот самый расход, из-за которого
        /// мини-карта уже была узким местом профайлера.
        /// </summary>
        private const float FragmentCapsuleRescanSeconds = 1f;

        /// <summary>Префикс имени спавнящегося объекта колбы-капсулы («CodeFragmentCapsule_&lt;fragmentId&gt;»).</summary>
        private const string FragmentCapsuleNamePrefix = "CodeFragmentCapsule_";

        [SerializeField] [Tooltip("Настройка параметра \"drawRuntimeGui\".")]
        private bool drawRuntimeGui = true;

        [SerializeField] [Tooltip("Настройка параметра \"forceVisibleWhenNoLevel\".")]
        private bool forceVisibleWhenNoLevel;

        /// <summary>
        /// Экранный прямоугольник компактной карточки, В ЭКРАННЫХ ПИКСЕЛЯХ и с началом координат в
        /// левом верхнем углу (как у любого <c>OnGUI</c>).
        /// <para>
        /// Значения взяты с макета HUD: карточка прижата к самому углу, пропорция примерно 1.3:1.
        /// Меньшая сторона не произвольна: по ней считается зум
        /// (<see cref="CompactMapScalePixelsPerMeter"/>), а от зума зависит, помещается ли маркер
        /// игрока в ячейку сетки и не теряется ли он на её фоне. Коридор держит
        /// <c>LevelMinimapFramingTests</c>: при текущих <c>visibleMeters</c> и
        /// <c>compactContentScale</c> меньшая сторона обязана лежать примерно между 150 и 210 px.
        /// </para>
        /// </summary>
        [SerializeField] [Tooltip("Экранный прямоугольник компактной карточки (пиксели, начало — левый верх).")]
        private Rect minimapRect = new(24f, 24f, 230f, 178f);

        [SerializeField] [Range(0f, 1f)] [Tooltip("Compact minimap background alpha.")]
        private float compactMapBackgroundAlpha = 0.78f;

        [SerializeField] [Range(0f, 1f)] [Tooltip("Full map background alpha when opened with M.")]
        private float fullMapBackgroundAlpha = 0.48f;

        [SerializeField] [Tooltip("Draw door socket markers on the runtime minimap. Editor preview always shows them.")]
        private bool drawDoorMarkers;

        [SerializeField] [Tooltip("Draw clean room footprint outlines instead of detailed wall segment geometry.")]
        private bool simplifiedDrawing;

        [SerializeField]
        [Tooltip("Показывать отметки колб-фрагментов: несобранные — заполненный кружок, собранные — "
                 + "пустой контур. Выключение убирает отметки с ОБЕИХ карт — и с полной (клавиша M), "
                 + "и с компактной.")]
        private bool drawFragmentMarkers = true;

        [SerializeField]
        [Tooltip("Показывать отметки колб и на КОМПАКТНОЙ карте в углу, а не только на полной. "
                 + "На компактной отметка мельче и рисуется ТОЛЬКО в уже посещённых комнатах: "
                 + "миникарта не должна выдавать неисследованную часть уровня.")]
        private bool drawFragmentMarkersOnCompactMap = true;

        [Header("Player centering")] [SerializeField] [Tooltip("Настройка параметра \"playerTransform\".")]
        private Transform playerTransform;

        [SerializeField] [Tooltip("Настройка параметра \"playerTag\".")]
        private string playerTag = "Player";

        /// <summary>
        /// Базовый охват мира по КОРОТКОЙ стороне внутренней области карточки при масштабе 1.
        /// Итоговый охват равен visibleMeters / compactContentScale; геометрия и маркеры уменьшаются
        /// согласованно. Соотношение сторожит `LevelMinimapFramingTests`.
        /// </summary>
        [SerializeField] [Tooltip("Настройка параметра \"visibleMeters\".")] [Min(12f)]
        private float visibleMeters = 20f;

        [SerializeField]
        [Range(0.25f, 1f)]
        [Tooltip("Единый authored-масштаб содержимого компактной карты. 0.5 показывает вдвое больше мира "
                 + "в прежней экранной карточке и тем же множителем уменьшает динамические маркеры.")]
        private float compactContentScale = 0.5f;

        [SerializeField] [Tooltip("Настройка параметра \"rotateWithPlayer\".")]
        private bool rotateWithPlayer = true;

        [Header("Palette")] [SerializeField] [Tooltip("Настройка параметра \"roomColor\".")]
        private Color roomColor = new(0.26f, 0.78f, 1f, 0.86f);

        [SerializeField] [Tooltip("Настройка параметра \"startRoomColor\".")]
        private Color startRoomColor = new(0.22f, 1f, 0.52f, 0.95f);

        [SerializeField] [Tooltip("Настройка параметра \"exitRoomColor\".")]
        private Color exitRoomColor = new(1f, 0.82f, 0.28f, 0.95f);

        [SerializeField] [Tooltip("Настройка параметра \"connectionColor\".")]
        private Color connectionColor = new(0.45f, 0.92f, 1f, 0.72f);

        [SerializeField]
        [Tooltip("Рисовать линии-связи между центрами комнат. По умолчанию выключено: при вертикальной "
                 + "раскладке они сливаются в одну длинную линию-«хребет» сквозь комнаты, а связность уже "
                 + "видна по контурам стен и дверям.")]
        private bool drawRoomConnections;

        [SerializeField] [Tooltip("Настройка параметра \"lockedDoorColor\".")]
        private Color lockedDoorColor = new(1f, 0.42f, 0.2f, 0.95f);

        private readonly LevelMinimapSession _session = new();
        private readonly Dictionary<string, PlacedRoom> _placedByNodeId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MinimapWallTexture> _wallTexturesByTemplateId = new(StringComparer.Ordinal);

        private readonly Dictionary<string, IReadOnlyList<MinimapWallSegment>> _wallSegmentsByNodeId =
            new(StringComparer.Ordinal);

        private readonly HashSet<string> _templatesWithoutWallTexture = new(StringComparer.Ordinal);

        /// <summary>
        /// Колбы каждой комнаты, снятые со сцены не чаще <see cref="FragmentCapsuleRescanSeconds"/>.
        /// Списки переиспользуются (Clear вместо new): отметки рисуются каждый кадр, и пересоздание
        /// коллекций здесь дало бы постоянный мусор в WebGL.
        /// </summary>
        private readonly Dictionary<string, List<Transform>> _fragmentCapsulesByNodeId =
            new(StringComparer.Ordinal);

        private float _nextFragmentCapsuleScanTime;
        private int _fragmentCapsuleCount;
        private LevelLayout _layout;
        private IReadOnlyDictionary<string, RoomInstance> _roomInstances;
        private IDisposable _roomEnteredSubscription;

        private Texture2D _pixel;

        // Figma minimap art (Resources.Load — runtime AddComponent overlay, not owned/destroyed here).
        private Texture2D _cardTexture;
        private Texture2D _playerMarkerTexture;
        private Texture2D _goalCrystalTexture;
        private bool _minimapTexturesLoaded;
        private bool _hasActiveMapClipRect;
        private Rect _activeMapClipRect;
        private readonly StaticMapCache _compactMapCache = new();
        private readonly StaticMapCache _fullMapCache = new();
        private Material _staticMapMaterial;
        private bool _isRenderingStaticMapCache;
        private int _staticMapStateVersion;
        private int _staticMapRebuildCount;

        public LevelMinimapSession Session => _session;

        /// <summary>
        /// Экранный прямоугольник компактной карточки. Нужен соседям по экрану: HUD ставит свои
        /// плашки ВПЛОТНУЮ справа от карты, а карта — <c>OnGUI</c> в экранных пикселях, тогда как
        /// панель UI Toolkit масштабируется своим <c>PanelSettings</c>. Без этого числа отступ
        /// плашек пришлось бы держать константой, верной ровно на одном разрешении.
        /// </summary>
        public Rect MinimapRect => minimapRect;

        public bool DrawRuntimeGui => drawRuntimeGui;
        public bool ForceVisibleWhenNoLevel => forceVisibleWhenNoLevel;
        public bool HasActiveLevel => _session.HasActiveLevel;
        public float CompactMapBackgroundAlpha => Mathf.Clamp01(compactMapBackgroundAlpha);
        public float FullMapBackgroundAlpha => Mathf.Clamp01(fullMapBackgroundAlpha);
        public bool DrawDoorMarkers => drawDoorMarkers;
        public int StaticMapRebuildCount => _staticMapRebuildCount;

        public bool SimplifiedDrawing
        {
            get => simplifiedDrawing;
            set
            {
                if (simplifiedDrawing == value)
                {
                    return;
                }

                simplifiedDrawing = value;
                MarkStaticMapDirty();
            }
        }

        public void Bind(LevelLayout layout) => Bind(layout, null, null);

        public void Bind(LevelLayout layout, IRoomEventBus roomEventBus) => Bind(layout, roomEventBus, null);

        public void Bind(
            LevelLayout layout,
            IRoomEventBus roomEventBus,
            IReadOnlyDictionary<string, RoomInstance> roomInstances)
        {
            _roomEnteredSubscription?.Dispose();
            _roomEnteredSubscription = null;
            UnsubscribeDoorOpenings();
            _roomInstances = roomInstances;
            InvalidateFragmentCapsuleCache();

            if (layout == null)
            {
                ResetLayout();
                return;
            }

            bool preserveRevealedRooms = ReferenceEquals(_layout, layout);
            _layout = layout;
            RebuildPlacedLookup(layout);
            Dictionary<string, Vector2> floorOffsets = new(StringComparer.Ordinal);
            if (roomInstances != null)
            {
                foreach (KeyValuePair<string, RoomInstance> entry in roomInstances)
                {
                    if (entry.Value != null && entry.Value.Template != null
                        && entry.Value.Template.HasPlacementBounds)
                    {
                        floorOffsets[entry.Key] = new Vector2(entry.Value.Template.PlacementBoundsMinXMeters,
                            entry.Value.Template.PlacementBoundsMinZMeters);
                    }
                }
            }

            _session.Bind(layout, preserveRevealedRooms: preserveRevealedRooms,
                floorOffsetsByNodeId: floorOffsets);
            RebuildWallTextures();
            BindRouteHint(layout, preserveRevealedRooms);
            SubscribeDoorOpenings();
            MarkStaticMapDirty();
            if (roomEventBus != null)
            {
                _roomEnteredSubscription = roomEventBus.Subscribe<RoomEnteredEvent>(OnRoomEntered);
            }
        }

        public void SetOverlayVisible(bool visible, bool forceWhenNoLevel = false)
        {
            drawRuntimeGui = visible;
            forceVisibleWhenNoLevel = forceWhenNoLevel;
            enabled = visible;
            if (!visible)
            {
                // OnDisable зовёт то же самое в игре; здесь — явно, чтобы правило держалось и вне Play Mode.
                SuspendRouteHintFloor();
            }
        }

        public void SetSimplifiedDrawing(bool enabled) => SimplifiedDrawing = enabled;

        public bool ToggleSimplifiedDrawing()
        {
            SimplifiedDrawing = !simplifiedDrawing;
            return simplifiedDrawing;
        }

        public bool ToggleFullMapFromCheat()
        {
            bool toggled = _session.ToggleFullMap();
            if (toggled)
            {
                ReportFullMapToggled("cheat");
            }

            return toggled;
        }

        /// <summary>
        /// Большая карта рисуется прямо сейчас: открыта и ученика не держит UI. Пока открыта панель или
        /// свободная мышь, открытая карта спрятана и вернётся вместе с управлением.
        /// </summary>
        public bool IsFullMapVisible => _session.IsFullMapOpen && _playerPresence == RouteHintPresence.Exploring;

        /// <summary>
        /// Телепорт после загрузки может не вызвать триггер комнаты. Старый список revealedNodeIds
        /// содержал и непосещённых соседей; показываем лишь завершённые комнаты и текущую безопасную.
        /// </summary>
        public void RestoreCheckpoint(IEnumerable<string> revealedNodeIds, string currentNodeId,
            IEnumerable<string> completedNodeIds = null, bool visitedSnapshotConfirmed = false)
        {
            if (!_session.HasActiveLevel)
            {
                return;
            }

            List<string> visited = completedNodeIds != null
                ? new List<string>(completedNodeIds) : new List<string>();
            if (visitedSnapshotConfirmed && revealedNodeIds != null)
            {
                visited.AddRange(revealedNodeIds);
            }
            if (!string.IsNullOrEmpty(currentNodeId))
            {
                visited.Add(currentNodeId);
            }

            _session.RestoreRevealedRooms(visited, currentNodeId);
            _routeHint.RestoreVisitedRooms(visited);
            MarkStaticMapDirty();
        }

        private void OnDestroy()
        {
            _roomEnteredSubscription?.Dispose();
            _roomEnteredSubscription = null;
            UnsubscribeDoorOpenings();
            ClearWallTextures();
            DisposeStaticMapCaches();
            DestroyStaticMapMaterial();
            DestroyPixelTexture();
            DestroyRouteHintResources();
        }

        private void DestroyPixelTexture()
        {
            if (_pixel == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (!UnityEngine.Application.isPlaying)
            {
                DestroyImmediate(_pixel);
            }
            else
#endif
            {
                Destroy(_pixel);
            }

            _pixel = null;
        }

        private void Update()
        {
            if (!_session.HasActiveLevel)
            {
                return;
            }

            IMinimapGameplayContext gameplay = MinimapGameplay.Context;
            RouteHintPresence presence = gameplay.Presence;
            TickFrame(presence, presence == RouteHintPresence.Exploring && WasFullMapKeyPressed(gameplay),
                Time.deltaTime, Time.unscaledDeltaTime, Time.unscaledTime, Time.time);
            UpdateRouteHintDiagnostics(gameplay, presence, Time.deltaTime, Time.unscaledDeltaTime,
                Time.unscaledTime);
        }

        /// <summary>
        /// Один кадр карты без чтения устройств: раскрытие комнат, подсказка маршрута (пол, затем пунктир) и
        /// клавиша большой карты. Пока ученика держит UI (<see cref="RouteHintPresence.Occupied"/>), большая
        /// карта не рисуется и <c>M</c> её не переключает. <see cref="Update"/> подаёт сюда факты игры;
        /// EditMode-тесты — свои, без сцены и без устройства ввода.
        /// </summary>
        internal void TickFrame(RouteHintPresence presence, bool fullMapKeyPressed, float deltaTime,
            float unscaledDeltaTime, float now, float? scaledNow = null)
        {
            if (!_session.HasActiveLevel)
            {
                return;
            }

            UpdateRoomReveals(now);
            _playerPresence = presence;
            bool exploring = presence == RouteHintPresence.Exploring;
            UpdateRouteHint(deltaTime, scaledNow ?? now, _routeHint.Decision.HasTarget && exploring, now,
                unscaledDeltaTime);
            if (fullMapKeyPressed && exploring && _session.ToggleFullMap())
            {
                ReportFullMapToggled("key M");
            }
        }

        /// <summary>
        /// `M` — геймплейная клавиша, и пока клавиатурой владеет UI, она обязана молчать. Без этой проверки
        /// карта разворачивалась поверх открытого чата учителя: ученик печатал сообщение, и буква «m» уводила
        /// пол-экрана под карту. Спрашиваем игру (<see cref="IMinimapGameplayContext.IsGameplayKeyboardBlocked"/>
        /// — у неё общий арбитр ввода), а не конкретную панель, — тогда правило работает и для чата, и для
        /// код-станции, и для любой будущей панели, не требуя знать о них здесь.
        /// </summary>
        private static bool WasFullMapKeyPressed(IMinimapGameplayContext gameplay)
        {
            if (gameplay.IsGameplayKeyboardBlocked)
            {
                return false;
            }

            bool pressed = WasInputSystemMPressed();
#if ENABLE_LEGACY_INPUT_MANAGER
            pressed = pressed || Input.GetKeyDown(KeyCode.M);
#endif
            return pressed;
        }

        private void OnRoomEntered(RoomEnteredEvent evt)
        {
            string previousCurrentRoomId = _session.CurrentRoomId;
            bool revealed = _session.RevealRoom(evt.NodeId, Time.unscaledTime);
            float idleBefore = _routeHint.IdleSeconds;
            bool progressed = _routeHint.MarkRoomVisited(evt.NodeId);
            if (progressed)
            {
                ReportRouteHintIdleRestart($"route progress in '{evt.NodeId}'", idleBefore);
            }
            if (revealed || previousCurrentRoomId != _session.CurrentRoomId)
            {
                MarkStaticMapDirty();
            }
        }

        private static bool WasInputSystemMPressed()
        {
            try
            {
                Type keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                object keyboard = keyboardType
                    ?.GetProperty("current",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.GetValue(null);
                if (keyboard == null)
                {
                    return false;
                }

                object keyControl = keyboardType
                    ?.GetProperty("mKey",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(keyboard);
                Type keyControlType = keyControl?.GetType();
                object value = keyControlType
                    ?.GetProperty("wasPressedThisFrame",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(keyControl);
                return value is bool pressed && pressed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnGUI()
        {
            if (!drawRuntimeGui)
            {
                return;
            }

            EnsurePixel();
            if (!_session.HasActiveLevel)
            {
                if (forceVisibleWhenNoLevel)
                {
                    DrawNoActiveLevel(minimapRect);
                }

                return;
            }

            DrawMap(minimapRect, false);

            // IMGUI рисуется поверх любой панели UI Toolkit: открытая большая карта закрыла бы собой чат,
            // код-станцию или вопрос голубой рамкой посреди экрана. Поэтому она видна, только пока ученик
            // в мире (RouteHintPresenceUseCase), и возвращается, когда панель закрыта.
            if (IsFullMapVisible)
            {
                Rect fullRect = new(Screen.width * 0.15f, Screen.height * 0.20f, Screen.width * 0.70f,
                    Screen.height * 0.68f);
                DrawMap(fullRect, true);
            }
        }

        private void ResetLayout()
        {
            _layout = null;
            _roomInstances = null;
            _placedByNodeId.Clear();
            InvalidateFragmentCapsuleCache();
            ClearWallTextures();
            _session.Reset();
            ResetRouteHint();
            DisposeStaticMapCaches();
            MarkStaticMapDirty();
        }

        private void RebuildPlacedLookup(LevelLayout layout)
        {
            _placedByNodeId.Clear();
            if (layout?.Rooms == null)
            {
                return;
            }

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (room != null && !string.IsNullOrEmpty(room.NodeId))
                {
                    _placedByNodeId[room.NodeId] = room;
                }
            }
        }

        private void DrawNoActiveLevel(Rect rect)
        {
            Color oldColor = GUI.color;
            GUI.color = new Color(0.015f, 0.022f, 0.03f, 0.78f);
            GUI.DrawTexture(rect, _pixel);
            GUI.color = new Color(0.62f, 0.95f, 1f, 0.9f);
            GUI.Label(
                new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 20f),
                "MINIMAP\nNo active level\nHub / debug overlay");
            GUI.color = oldColor;
        }

        private void DrawMap(Rect rect, bool fullMap)
        {
            Color oldColor = GUI.color;
            bool previousHasClipRect = _hasActiveMapClipRect;
            Rect previousClipRect = _activeMapClipRect;
            GUI.BeginGroup(rect);
            try
            {
                Rect localRect = new(0f, 0f, rect.width, rect.height);

                // Figma glass-blue card drawn stretched over the WHOLE rect (its frame/rounded
                // corners live at the edges).
                DrawCardBackground(localRect, fullMap);

                // Room geometry + markers are drawn INSIDE the card frame (inset), so walls/corridors
                // never spill past the rounded card edge. The line clip rect follows the inset too.
                float pad = Mathf.Min(rect.width, rect.height) * CardInsetFraction;
                Rect mapRect = new(pad, pad, Mathf.Max(1f, rect.width - pad * 2f), Mathf.Max(1f, rect.height - pad * 2f));
                _hasActiveMapClipRect = true;
                _activeMapClipRect = mapRect;

                IReadOnlyList<MinimapRoomSnapshot> rooms = RoomsForDrawing();
                bool cached = TryDrawOrBuildStaticMapCache(mapRect, fullMap, true);
                MapProjection projection = default;
                bool projected = rooms.Count > 0 && TryBuildProjection(rooms, mapRect, fullMap, out projection);
                if (fullMap)
                {
                    ReportFullMapDrawOnce(cached, rooms.Count, projected);
                }

                // Живые слои (подсказка маршрута, выход, колбы, игрок) не зависят от кэша геометрии: если
                // кэш в браузере не построился, пунктир всё равно обязан появиться.
                if (projected)
                {
                    DrawLiveLayers(rooms, projection, fullMap);
                    DrawPlayerMarker(ProjectPlayerPosition(projection), projection.Scale);
                }
            }
            finally
            {
                _hasActiveMapClipRect = previousHasClipRect;
                _activeMapClipRect = previousClipRect;
                GUI.EndGroup();
                GUI.color = oldColor;
            }
        }

        private bool WarmStaticMapCache(Rect rect, bool fullMap)
        {
            if (!_session.HasActiveLevel)
            {
                return false;
            }

            EnsurePixel();
            Rect localRect = new(0f, 0f, rect.width, rect.height);
            return TryDrawOrBuildStaticMapCache(localRect, fullMap, false);
        }

        private bool TryDrawOrBuildStaticMapCache(Rect localRect, bool fullMap, bool drawTexture)
        {
            if (localRect.width <= 1f || localRect.height <= 1f)
            {
                return false;
            }

            StaticMapCache cache = fullMap ? _fullMapCache : _compactMapCache;
            ProjectionPoseKey poseKey = BuildProjectionPoseKey();
            int width = Mathf.CeilToInt(localRect.width);
            int height = Mathf.CeilToInt(localRect.height);
            int settingsHash = CalculateStaticMapSettingsHash(fullMap);

            if (!cache.IsValid(width, height, _staticMapStateVersion, settingsHash, poseKey))
            {
                RebuildStaticMapCache(cache, fullMap, width, height, settingsHash, poseKey);
            }

            if (cache.Texture == null)
            {
                return false;
            }

            if (drawTexture)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(localRect, cache.Texture);
            }

            return true;
        }

        /// <summary>
        /// Переводит клип геометрии в координаты ТЕКСТУРЫ кеша: снаружи клип задан в координатах
        /// группы, со смещением на внутренний отступ карточки, и в пространстве текстуры срезал карту
        /// сверху и слева на этот отступ, а снизу и справа уезжал за край и не срезал ничего.
        /// Автоматического стража у этого пока нет — он требует чтения пикселей кеша.
        /// </summary>
        private void BeginStaticCacheClip(int width, int height)
        {
            _hasActiveMapClipRect = true;
            _activeMapClipRect = new Rect(0f, 0f, width, height);
        }

        /// <summary>Комнаты, которые вообще имеет право рисовать карта — ЛЮБАЯ из двух.</summary>
        /// <remarks>
        /// Правило названо ОДИН раз и здесь. Раньше выбор набора стоял в четырёх местах отдельно
        /// (комнаты, связи, маркер выхода, колбы), и достаточно было пропустить одно, чтобы карта
        /// снова выдала непройденную часть уровня: коридор ведёт в пустоту, колба висит в темноте.
        /// </remarks>
        private IReadOnlyList<MinimapRoomSnapshot> RoomsForDrawing() => _session.GetVisibleRooms();

        /// <summary>Связи между комнатами по тому же правилу.</summary>
        private IReadOnlyList<MinimapConnectionSnapshot> ConnectionsForDrawing() =>
            _session.GetVisibleConnections();

        private void RebuildStaticMapCache(
            StaticMapCache cache,
            bool fullMap,
            int width,
            int height,
            int settingsHash,
            ProjectionPoseKey poseKey)
        {
            cache.EnsureTexture(width, height);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = cache.Texture;

            bool previousHasClip = _hasActiveMapClipRect;
            Rect previousClip = _activeMapClipRect;
            BeginStaticCacheClip(width, height);

            GL.PushMatrix();
            try
            {
                GL.Clear(false, true, Color.clear);
                GL.LoadPixelMatrix(0f, width, height, 0f);
                EnsureStaticMapMaterial();
                if (_staticMapMaterial == null || !_staticMapMaterial.SetPass(0))
                {
                    // Кэш помечается построенным, чтобы не повторять отказ каждый кадр, но статус хранит правду:
                    // стен в нём нет, и первая отрисовка большой карты скажет это в логе.
                    ReportStaticMapCacheProblemOnce("the GL material 'Hidden/Internal-Colored' is unavailable");
                    cache.MarkValid(width, height, _staticMapStateVersion, settingsHash, poseKey);
                    cache.Status = StaticMapCacheStatus.MaterialUnavailable;
                    _staticMapRebuildCount++;
                    return;
                }

                _isRenderingStaticMapCache = true;
                GL.Begin(GL.QUADS);
                try
                {
                    // The glass-blue Figma card is drawn per-frame in DrawCardBackground (behind this
                    // cache), so the room-geometry cache stays transparent and composites over it.
                    IReadOnlyList<MinimapRoomSnapshot> rooms = RoomsForDrawing();
                    if (rooms.Count > 0 &&
                        TryBuildProjection(rooms, new Rect(0f, 0f, width, height), fullMap, out MapProjection projection))
                    {
                        IReadOnlyList<MinimapConnectionSnapshot> connections = ConnectionsForDrawing();
                        if (drawRoomConnections && !simplifiedDrawing)
                        {
                            DrawConnections(rooms, connections, projection, fullMap);
                        }

                        DrawVisibleRoomGeometry(rooms, projection, fullMap);
                    }
                }
                finally
                {
                    GL.End();
                    _isRenderingStaticMapCache = false;
                }
            }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previousActive;
                _hasActiveMapClipRect = previousHasClip;
                _activeMapClipRect = previousClip;
            }

            cache.MarkValid(width, height, _staticMapStateVersion, settingsHash, poseKey);
            cache.Status = cache.Created ? StaticMapCacheStatus.Built : StaticMapCacheStatus.TextureNotCreated;
            _staticMapRebuildCount++;
        }

        private void DrawVisibleRoomGeometry(
            IReadOnlyList<MinimapRoomSnapshot> rooms,
            MapProjection projection,
            bool fullMap)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                MinimapRoomSnapshot room = rooms[i];
                if (_session.IsRevealAnimating(room.NodeId))
                {
                    continue;
                }

                DrawRoomGeometry(room, projection, fullMap);
            }
        }

        /// <summary>Стены, заливка и маркеры дверей одной комнаты — и для кэша, и для живого слоя.</summary>
        private void DrawRoomGeometry(MinimapRoomSnapshot room, MapProjection projection, bool fullMap)
        {
            if (simplifiedDrawing)
            {
                DrawSimplifiedRoom(room, projection, fullMap);
            }
            else if (room.Cells != null && room.Cells.Count > 0)
            {
                if (!DrawBakedWallTexture(room, projection, fullMap))
                {
                    DrawRoomFootprintBoundary(room, projection, fullMap);
                }
            }
            else if (!DrawBakedWallTexture(room, projection, fullMap))
            {
                Vector2[] corners = BuildRoomWorldCorners(room);
                Vector2[] projected = new Vector2[corners.Length];
                for (int c = 0; c < corners.Length; c++)
                {
                    projected[c] = ProjectWorld(corners[c], projection);
                }

                DrawRoomWalls(room, projected, fullMap);
            }

            if (drawDoorMarkers)
            {
                DrawRoomDoors(room, projection, fullMap);
            }
        }

        // Simplified minimap mode: SAME wall geometry as the detailed mode, but each wall is a single
        // clean line (not the multi-segment wall texture), with a soft glow radiating from the walls
        // inward into the room. Matches the Figma "Мини Карта" card.
        private void DrawSimplifiedRoom(MinimapRoomSnapshot room, MapProjection projection, bool fullMap)
        {
            // Inner glow base: soft translucent fill of the room footprint (brightest ambient light).
            FillRoomFootprint(room, projection, fullMap);

            if (_wallSegmentsByNodeId.TryGetValue(room.NodeId, out IReadOnlyList<MinimapWallSegment> segments)
                && segments != null
                && segments.Count > 0)
            {
                DrawWallSegmentsSingleLine(room, segments, projection, fullMap);
                return;
            }

            DrawRoomFootprintBoundarySimple(room, projection, fullMap);
        }

        // One thin line per wall segment, under a wide faint halo of the same colour (the "glow").
        private void DrawWallSegmentsSingleLine(
            MinimapRoomSnapshot room,
            IReadOnlyList<MinimapWallSegment> segments,
            MapProjection projection,
            bool fullMap)
        {
            Color line = room.IsCurrent
                ? new Color(1f, 1f, 1f, fullMap ? 0.95f : 0.9f)
                : new Color(0.62f, 0.95f, 1f, fullMap ? 0.9f : 0.82f);
            Color glow = new(line.r, line.g, line.b, fullMap ? 0.22f : 0.18f);
            float glowThickness = 6f;
            float lineThickness = room.IsCurrent ? 2f : 1.6f;

            for (int i = 0; i < segments.Count; i++)
            {
                Vector2 a = ProjectWorld(segments[i].A, projection);
                Vector2 b = ProjectWorld(segments[i].B, projection);
                DrawLine(a, b, glowThickness, glow);
                DrawLine(a, b, lineThickness, line);
            }
        }

        // Fallback single-line footprint boundary (rooms without baked wall segments): merged collinear
        // runs so a straight wall is one clean line, again with a soft inward glow halo.
        private void DrawRoomFootprintBoundarySimple(
            MinimapRoomSnapshot room,
            MapProjection projection,
            bool fullMap)
        {
            Color baseColor = room.IsStart ? startRoomColor : room.IsExit ? exitRoomColor : roomColor;
            Color line = room.IsCurrent
                ? new Color(1f, 1f, 1f, 0.95f)
                : new Color(baseColor.r, baseColor.g, baseColor.b, fullMap ? 0.92f : 0.82f);
            Color glow = new(line.r, line.g, line.b, fullMap ? 0.22f : 0.18f);
            float glowThickness = 6f;
            float lineThickness = room.IsCurrent ? 2f : 1.6f;

            if (room.Cells == null || room.Cells.Count == 0)
            {
                Vector2[] corners = BuildRoomWorldCorners(room);
                for (int i = 0; i < corners.Length; i++)
                {
                    Vector2 a = ProjectWorld(corners[i], projection);
                    Vector2 b = ProjectWorld(corners[(i + 1) % corners.Length], projection);
                    DrawLine(a, b, glowThickness, glow);
                    DrawLine(a, b, lineThickness, line);
                }

                return;
            }

            List<LocalEdge> edges = MergeCollinearBoundaryEdges(BuildBoundaryEdges(room.Cells));
            for (int i = 0; i < edges.Count; i++)
            {
                LocalEdge edge = edges[i];
                Vector2 a = ProjectWorld(RoomLocalToWorld(room, edge.A), projection);
                Vector2 b = ProjectWorld(RoomLocalToWorld(room, edge.B), projection);
                DrawLine(a, b, glowThickness, glow);
                DrawLine(a, b, lineThickness, line);
            }
        }

        private void DrawConnections(
            IReadOnlyList<MinimapRoomSnapshot> rooms,
            IReadOnlyList<MinimapConnectionSnapshot> connections,
            MapProjection projection,
            bool fullMap)
        {
            if (connections == null || connections.Count == 0)
            {
                return;
            }

            Dictionary<string, MinimapRoomSnapshot> roomById = new(StringComparer.Ordinal);
            for (int i = 0; i < rooms.Count; i++)
            {
                MinimapRoomSnapshot room = rooms[i];
                if (!string.IsNullOrEmpty(room.NodeId))
                {
                    roomById[room.NodeId] = room;
                }
            }

            Color shadow = new(0f, 0f, 0f, fullMap ? 0.56f : 0.46f);
            Color color = new(connectionColor.r, connectionColor.g, connectionColor.b,
                fullMap ? connectionColor.a : connectionColor.a * 0.82f);
            float shadowThickness = 3.6f;
            float thickness = 1.5f;

            for (int i = 0; i < connections.Count; i++)
            {
                MinimapConnectionSnapshot connection = connections[i];
                if (!roomById.TryGetValue(connection.FromNodeId, out MinimapRoomSnapshot fromRoom) ||
                    !roomById.TryGetValue(connection.ToNodeId, out MinimapRoomSnapshot toRoom))
                {
                    continue;
                }

                // Связь к проявляющейся комнате проявляется вместе с ней — живым слоем, не в кэше.
                if (_isRenderingStaticMapCache
                    && (_session.IsRevealAnimating(connection.FromNodeId) || _session.IsRevealAnimating(connection.ToNodeId)))
                {
                    continue;
                }

                Vector2 a = ProjectWorld(Average(BuildRoomWorldCorners(fromRoom)), projection);
                Vector2 b = ProjectWorld(Average(BuildRoomWorldCorners(toRoom)), projection);
                DrawLine(a, b, shadowThickness, shadow);
                DrawLine(a, b, thickness, color);
            }
        }

        private void RebuildWallTextures()
        {
            ClearWallTextures();
            if (_roomInstances == null)
            {
                return;
            }

            foreach (KeyValuePair<string, RoomInstance> kv in _roomInstances)
            {
                RoomInstance instance = kv.Value;
                if (instance == null || instance.Template == null)
                {
                    continue;
                }

                string templateId = ResolveWallTemplateId(instance.Template);
                if (string.IsNullOrWhiteSpace(templateId))
                {
                    continue;
                }

                if (!_wallTexturesByTemplateId.TryGetValue(templateId, out MinimapWallTexture texture)
                    && !_templatesWithoutWallTexture.Contains(templateId))
                {
                    texture = MinimapWallTextureBaker.Bake(instance.Template);
                    if (texture != null)
                    {
                        _wallTexturesByTemplateId[templateId] = texture;
                    }
                    else
                    {
                        _templatesWithoutWallTexture.Add(templateId);
                    }
                }

                if (texture?.Segments != null && texture.Segments.Count > 0)
                {
                    _wallSegmentsByNodeId[kv.Key] = TransformWallSegments(instance.transform, texture.Segments);
                }
            }
        }

        private void ClearWallTextures()
        {
            foreach (KeyValuePair<string, MinimapWallTexture> kv in _wallTexturesByTemplateId)
            {
                kv.Value?.Dispose();
            }

            _wallTexturesByTemplateId.Clear();
            _wallSegmentsByNodeId.Clear();
            _templatesWithoutWallTexture.Clear();
        }

        private void MarkStaticMapDirty()
        {
            unchecked
            {
                _staticMapStateVersion++;
            }
        }

        private void DisposeStaticMapCaches()
        {
            _compactMapCache.Dispose();
            _fullMapCache.Dispose();
        }

        private void EnsureStaticMapMaterial()
        {
            if (_staticMapMaterial != null)
            {
                return;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return;
            }

            _staticMapMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _staticMapMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _staticMapMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _staticMapMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _staticMapMaterial.SetInt("_ZWrite", 0);
        }

        private void DestroyStaticMapMaterial()
        {
            if (_staticMapMaterial == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (!UnityEngine.Application.isPlaying)
            {
                DestroyImmediate(_staticMapMaterial);
            }
            else
#endif
            {
                Destroy(_staticMapMaterial);
            }

            _staticMapMaterial = null;
        }

        private ProjectionPoseKey BuildProjectionPoseKey()
        {
            Transform player = ResolvePlayerTransform();
            if (player == null)
            {
                return ProjectionPoseKey.NoPlayer;
            }

            return new ProjectionPoseKey(
                true,
                Mathf.RoundToInt(player.position.x * 100f),
                Mathf.RoundToInt(player.position.z * 100f),
                Mathf.RoundToInt(ResolveLookYaw() * 100f));
        }

        private int CalculateStaticMapSettingsHash(bool fullMap)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + fullMap.GetHashCode();
                hash = hash * 31 + simplifiedDrawing.GetHashCode();
                hash = hash * 31 + drawDoorMarkers.GetHashCode();
                hash = hash * 31 + drawRoomConnections.GetHashCode();
                hash = hash * 31 + rotateWithPlayer.GetHashCode();
                hash = hash * 31 + Mathf.RoundToInt(visibleMeters * 100f);
                hash = hash * 31 + Mathf.RoundToInt(CompactContentScaleValue() * 1000f);
                hash = hash * 31 + Mathf.RoundToInt(CompactMapBackgroundAlpha * 1000f);
                hash = hash * 31 + Mathf.RoundToInt(FullMapBackgroundAlpha * 1000f);
                hash = hash * 31 + ColorHash(roomColor);
                hash = hash * 31 + ColorHash(startRoomColor);
                hash = hash * 31 + ColorHash(exitRoomColor);
                hash = hash * 31 + ColorHash(connectionColor);
                hash = hash * 31 + ColorHash(lockedDoorColor);
                return hash;
            }
        }

        private static int ColorHash(Color color)
        {
            unchecked
            {
                int hash = Mathf.RoundToInt(color.r * 1000f);
                hash = (hash * 397) ^ Mathf.RoundToInt(color.g * 1000f);
                hash = (hash * 397) ^ Mathf.RoundToInt(color.b * 1000f);
                hash = (hash * 397) ^ Mathf.RoundToInt(color.a * 1000f);
                return hash;
            }
        }

        private bool DrawBakedWallTexture(MinimapRoomSnapshot room, MapProjection projection, bool fullMap)
        {
            if (_wallSegmentsByNodeId.TryGetValue(room.NodeId, out IReadOnlyList<MinimapWallSegment> segments)
                && segments != null
                && segments.Count > 0)
            {
                DrawWallSegments(room, segments, projection, fullMap);
                return true;
            }

            return false;
        }

        private void DrawWallSegments(
            MinimapRoomSnapshot room,
            IReadOnlyList<MinimapWallSegment> segments,
            MapProjection projection,
            bool fullMap)
        {
            Color color = room.IsCurrent
                ? new Color(1f, 1f, 1f, fullMap ? 0.9f : 0.82f)
                : new Color(0.5f, 0.96f, 1f, fullMap ? 0.82f : 0.7f);
            Color shadow = new(0f, 0f, 0f, fullMap ? 0.68f : 0.55f);

            for (int i = 0; i < segments.Count; i++)
            {
                MinimapWallSegment segment = segments[i];
                Vector2 a = ProjectWorld(segment.A, projection);
                Vector2 b = ProjectWorld(segment.B, projection);
                float thickness = Mathf.Clamp(segment.WidthMeters * projection.Scale, 1.8f, 4f);
                DrawLine(a, b, thickness + 2f, shadow);
                DrawLine(a, b, thickness, color);
            }
        }

        private static string ResolveWallTemplateId(RoomTemplate template) => template != null ? template.RoomId : null;

        private static IReadOnlyList<MinimapWallSegment> TransformWallSegments(
            Transform roomTransform,
            IReadOnlyList<MinimapWallSegment> source)
        {
            if (roomTransform == null || source == null || source.Count == 0)
            {
                return Array.Empty<MinimapWallSegment>();
            }

            MinimapWallSegment[] result = new MinimapWallSegment[source.Count];
            float widthScale = Mathf.Max(0.01f, AverageScale(roomTransform.lossyScale.x, roomTransform.lossyScale.z));
            for (int i = 0; i < source.Count; i++)
            {
                MinimapWallSegment segment = source[i];
                Vector3 worldA = roomTransform.TransformPoint(new Vector3(segment.A.x, 0f, segment.A.y));
                Vector3 worldB = roomTransform.TransformPoint(new Vector3(segment.B.x, 0f, segment.B.y));
                result[i] = new MinimapWallSegment(
                    new Vector2(worldA.x, worldA.z),
                    new Vector2(worldB.x, worldB.z),
                    segment.WidthMeters * widthScale);
            }

            return result;
        }

        private static float AverageScale(float a, float b) => (Mathf.Abs(a) + Mathf.Abs(b)) * 0.5f;

        internal bool TryBuildProjection(
            IReadOnlyList<MinimapRoomSnapshot> rooms,
            Rect rect,
            bool fullMap,
            out MapProjection projection)
        {
            float meters;
            Vector2 center;
            if (fullMap)
            {
                if (rooms.Count == 0)
                {
                    Transform fallbackPlayer = ResolvePlayerTransform();
                    center = fallbackPlayer != null
                        ? new Vector2(fallbackPlayer.position.x, fallbackPlayer.position.z)
                        : Vector2.zero;
                }
                else if (!TryCalculateFullMapFit(rooms, ResolveLookYaw(), out center, out _))
                {
                    center = ResolveFallbackCenter(rooms);
                }

                meters = CalculateFullMapFitMeters(rect, rooms);
            }
            else
            {
                Transform player = ResolvePlayerTransform();
                center = player != null
                    ? new Vector2(player.position.x, player.position.z)
                    : ResolveFallbackCenter(rooms);
                meters = visibleMeters / CompactContentScaleValue();
            }

            float scale = Mathf.Min(rect.width, rect.height) / Mathf.Max(12f, meters);
            projection = new MapProjection(center, rect.center, scale, ResolveLookYaw());
            return scale > 0.001f;
        }

        // The map rotates around the player's LOOK direction: the yaw comes from the player's own
        // camera (the CMF look pivot), not from the rig root, whose yaw follows only the movement
        // direction. Falls back to Camera.main and then to the player transform itself.
        private Transform ResolveLookTransform()
        {
            Transform player = ResolvePlayerTransform();
            if (player != null)
            {
                Camera playerCamera = player.GetComponentInChildren<Camera>(true);
                if (playerCamera != null)
                {
                    return playerCamera.transform;
                }
            }

            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                return mainCamera.transform;
            }

            return player;
        }

        private float ResolveLookYaw()
        {
            if (!rotateWithPlayer)
            {
                return 0f;
            }

            Transform lookTransform = ResolveLookTransform();
            return lookTransform != null ? lookTransform.eulerAngles.y : 0f;
        }

        // The compact minimap's world-to-screen zoom (pixels per meter across its inner, inset map
        // rect), including the authored content scale. The big map reuses this zoom as its baseline.
        internal float CompactMapScalePixelsPerMeter()
        {
            float outer = Mathf.Min(minimapRect.width, minimapRect.height);
            float inner = Mathf.Max(1f, outer * (1f - 2f * CardInsetFraction));
            return inner / Mathf.Max(12f, visibleMeters) * CompactContentScaleValue();
        }

        private float CompactContentScaleValue() => Mathf.Clamp(compactContentScale, 0.25f, 1f);

        private Transform ResolvePlayerTransform()
        {
            if (playerTransform != null)
            {
                return playerTransform;
            }

            if (!string.IsNullOrWhiteSpace(playerTag))
            {
                try
                {
                    GameObject player = GameObject.FindGameObjectWithTag(playerTag);
                    if (player != null)
                    {
                        playerTransform = player.transform;
                        return playerTransform;
                    }
                }
                catch (UnityException)
                {
                    // The tag may be missing in isolated edit-mode tests.
                }
            }

            Camera mainCamera = Camera.main;
            return mainCamera != null ? mainCamera.transform : null;
        }

        private Vector2 ResolveFallbackCenter(IReadOnlyList<MinimapRoomSnapshot> rooms)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].IsCurrent)
                {
                    return Average(BuildRoomWorldCorners(rooms[i]));
                }
            }

            return rooms.Count > 0 ? Average(BuildRoomWorldCorners(rooms[0])) : Vector2.zero;
        }

        /// <summary>
        /// Большая карта вписывает вписанный прямоугольник показанных комнат с полями и центрируется
        /// на нём, а не на игроке. Мелкую карту кламп не раздувает сильнее
        /// <see cref="FullMapMaxZoomVsCompact"/> от масштаба угловой: комната-выброс за кадром
        /// допустима, нечитаемая мелочь — нет. Угловую карту это не трогает.
        /// </summary>
        public const float FullMapMaxZoomVsCompact = 3f;

        /// <summary>Поля вокруг вписанного набора комнат полной карты, метры.</summary>
        private const float FullMapFitMarginMeters = 8f;

        private bool TryCalculateFullMapFit(
            IReadOnlyList<MinimapRoomSnapshot> rooms,
            float yawDegrees,
            out Vector2 center,
            out float sizeMeters)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            bool any = false;
            for (int i = 0; i < rooms.Count; i++)
            {
                Vector2[] corners = BuildRoomWorldCorners(rooms[i]);
                for (int c = 0; c < corners.Length; c++)
                {
                    minX = Mathf.Min(minX, corners[c].x);
                    minY = Mathf.Min(minY, corners[c].y);
                    maxX = Mathf.Max(maxX, corners[c].x);
                    maxY = Mathf.Max(maxY, corners[c].y);
                    any = true;
                }
            }

            if (!any)
            {
                center = Vector2.zero;
                sizeMeters = 0f;
                return false;
            }

            center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            float cosine = Mathf.Abs(Mathf.Cos(yawDegrees * Mathf.Deg2Rad));
            float sine = Mathf.Abs(Mathf.Sin(yawDegrees * Mathf.Deg2Rad));
            sizeMeters = Mathf.Max(sizeX * cosine + sizeY * sine, sizeX * sine + sizeY * cosine)
                         + FullMapFitMarginMeters;
            return true;
        }

        private float CalculateFullMapFitMeters(Rect rect, IReadOnlyList<MinimapRoomSnapshot> rooms)
        {
            if (!TryCalculateFullMapFit(rooms, ResolveLookYaw(), out _, out float sizeMeters))
            {
                return Mathf.Max(12f, visibleMeters);
            }

            float maxScale = FullMapMaxZoomVsCompact * CompactMapScalePixelsPerMeter();
            float minMeters = Mathf.Min(rect.width, rect.height) / Mathf.Max(0.001f, maxScale);
            return Mathf.Max(sizeMeters, minMeters);
        }

        private void DrawRoomFootprintBoundary(
            MinimapRoomSnapshot room,
            MapProjection projection,
            bool fullMap)
        {
            if (room.Cells == null || room.Cells.Count == 0)
            {
                Vector2[] corners = BuildRoomWorldCorners(room);
                for (int i = 0; i < corners.Length; i++)
                {
                    Vector2 a = ProjectWorld(corners[i], projection);
                    Vector2 b = ProjectWorld(corners[(i + 1) % corners.Length], projection);
                    DrawRoomWallSegment(room, a, b, fullMap);
                }

                return;
            }

            // Merge the per-cell boundary edges into maximal straight runs first: a wall
            // spanning N cells must draw as ONE clean line, not N short segments (each
            // segment is drawn with an inset inner line, so unmerged cell edges render as a
            // broken/"dashed" crooked outline in the simplified minimap mode).
            List<LocalEdge> edges = MergeCollinearBoundaryEdges(BuildBoundaryEdges(room.Cells));
            for (int i = 0; i < edges.Count; i++)
            {
                LocalEdge edge = edges[i];
                Vector2 a = ProjectWorld(RoomLocalToWorld(room, edge.A), projection);
                Vector2 b = ProjectWorld(RoomLocalToWorld(room, edge.B), projection);
                DrawRoomWallSegment(room, a, b, fullMap);
            }
        }

        private void DrawRoomWalls(MinimapRoomSnapshot room, Vector2[] corners, bool fullMap)
        {
            for (int i = 0; i < corners.Length; i++)
            {
                DrawRoomWallSegment(room, corners[i], corners[(i + 1) % corners.Length], fullMap);
            }
        }

        private void DrawRoomWallSegment(MinimapRoomSnapshot room, Vector2 a, Vector2 b, bool fullMap)
        {
            Color baseColor = room.IsStart ? startRoomColor : room.IsExit ? exitRoomColor : roomColor;
            Color shadow = new(0f, 0f, 0f, fullMap ? 0.66f : 0.54f);
            Color outer = room.IsCurrent
                ? new Color(1f, 1f, 1f, 0.95f)
                : new Color(0.34f, 0.95f, 1f, fullMap ? 0.92f : 0.76f);
            Color inner = new(baseColor.r, baseColor.g, baseColor.b, room.IsCurrent ? 0.95f : 0.72f);

            DrawLine(a, b, 4f, shadow);
            DrawLine(a, b, room.IsCurrent ? 3f : 2f, outer);
            DrawLine(Vector2.Lerp(a, b, 0.08f), Vector2.Lerp(a, b, 0.92f), 1f, inner);
        }

        private void DrawRoomDoors(MinimapRoomSnapshot room, MapProjection projection, bool fullMap)
        {
            if (_roomInstances != null && _roomInstances.TryGetValue(room.NodeId, out RoomInstance instance) &&
                instance?.Doors != null)
            {
                for (int i = 0; i < instance.Doors.Count; i++)
                {
                    DoorSocket door = instance.Doors[i];
                    if (door == null || !ShouldDrawDoor(door.State))
                    {
                        continue;
                    }

                    Vector2 center = ResolveDoorWorldCenter(instance, door);
                    Vector3 normal3 = instance.transform.rotation
                                      * Quaternion.Euler(0f, ResolveDoorLocalYaw(instance, door), 0f)
                                      * Vector3.right;
                    Vector2 normal = new(normal3.x, normal3.z);
                    if (normal.sqrMagnitude < 0.001f)
                    {
                        normal = Vector2.right;
                    }

                    normal.Normalize();
                    DrawDoorSlot(center, normal, ResolveDoorFrameWidth(instance, door), door.State, projection,
                        fullMap);
                }

                return;
            }

            if (!_placedByNodeId.TryGetValue(room.NodeId, out PlacedRoom placed) || placed.DoorStates == null)
            {
                return;
            }

            foreach (KeyValuePair<string, DoorState> kv in placed.DoorStates)
            {
                if (!ShouldDrawDoor(kv.Value))
                {
                    continue;
                }

                if (!TryBuildFallbackDoor(room, kv.Key, out Vector2 center, out Vector2 normal))
                {
                    continue;
                }

                DrawDoorSlot(center, normal, DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(DoorSize.Medium),
                    kv.Value, projection, fullMap);
            }
        }

        private void DrawDoorSlot(
            Vector2 worldCenter,
            Vector2 worldNormal,
            float width,
            DoorState state,
            MapProjection projection,
            bool fullMap)
        {
            Vector2 tangent = new(-worldNormal.y, worldNormal.x);
            Vector2 left = ProjectWorld(worldCenter - tangent * (width * 0.5f), projection);
            Vector2 right = ProjectWorld(worldCenter + tangent * (width * 0.5f), projection);
            Vector2 center = ProjectWorld(worldCenter, projection);
            Vector2 inward = ProjectWorld(worldCenter - worldNormal * 0.45f, projection);
            Vector2 outward = ProjectWorld(worldCenter + worldNormal * 0.45f, projection);

            Color color = DoorColor(state);
            float markerScale = ContentMarkerScale(fullMap);
            DrawLine(left, right, 5f * markerScale, new Color(0f, 0f, 0f, 0.72f));
            DrawLine(left, right, 3f * markerScale, color);
            DrawLine(inward, outward, 2f * markerScale, color);
            DrawDot(center, 3f * markerScale, color);
        }

        private bool TryBuildFallbackDoor(
            MinimapRoomSnapshot room,
            string socketId,
            out Vector2 center,
            out Vector2 normal)
        {
            string id = socketId?.ToLowerInvariant() ?? string.Empty;
            Vector2 local = Vector2.zero;
            normal = Vector2.right;

            if (id.Contains("east") || id == "e")
            {
                local = new Vector2(room.Width, room.Depth * 0.5f);
                normal = Vector2.right;
            }
            else if (id.Contains("west") || id == "w")
            {
                local = new Vector2(0f, room.Depth * 0.5f);
                normal = Vector2.left;
            }
            else if (id.Contains("north") || id == "n")
            {
                local = new Vector2(room.Width * 0.5f, room.Depth);
                normal = Vector2.up;
            }
            else if (id.Contains("south") || id == "s")
            {
                local = new Vector2(room.Width * 0.5f, 0f);
                normal = Vector2.down;
            }
            else
            {
                center = default;
                normal = default;
                return false;
            }

            center = RoomLocalToWorld(room, local);
            normal = Rotate(normal, room.RotationDegrees).normalized;
            return true;
        }

        /// <summary>
        /// Цвет заполненного кружка несобранной колбы. Непрозрачный: отметка собирается из
        /// перекрывающихся полос, и полупрозрачный цвет дал бы швы на стыках вместо ровного кружка.
        /// </summary>
        private static readonly Color UncollectedFragmentColor = new(0.36f, 1f, 0.44f, 1f);

        /// <summary>Цвет контура собранной колбы — приглушённый, чтобы не спорить с несобранными.</summary>
        private static readonly Color CollectedFragmentColor = new(0.72f, 0.80f, 0.88f, 1f);

        /// <summary>
        /// Тёмная подложка отметки колбы. Она и делает отметку заметной: карта светло-голубая, и
        /// цветной кружок без канта сливается со стенами.
        /// </summary>
        private static readonly Color FragmentMarkerOutlineColor = new(0.02f, 0.05f, 0.09f, 1f);

        private void EnsureMinimapTextures()
        {
            if (_minimapTexturesLoaded)
            {
                return;
            }

            _minimapTexturesLoaded = true;
            _cardTexture = Resources.Load<Texture2D>("Minimap/card");
            _playerMarkerTexture = Resources.Load<Texture2D>("Minimap/player-marker");
            _goalCrystalTexture = Resources.Load<Texture2D>("Minimap/crystal");
        }

        // Figma glass-blue card behind the room geometry. Falls back to a flat blue quad if the
        // texture is missing (isolated edit-mode tests without the Resources asset).
        private void DrawCardBackground(Rect localRect, bool fullMap)
        {
            EnsureMinimapTextures();
            float alpha = fullMap ? FullMapBackgroundAlpha : CompactMapBackgroundAlpha;
            if (_cardTexture != null)
            {
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha + 0.2f));
                GUI.DrawTexture(localRect, _cardTexture, ScaleMode.StretchToFill);
            }
            else
            {
                GUI.color = new Color(0.20f, 0.46f, 0.88f, alpha);
                GUI.DrawTexture(localRect, _pixel);
            }

            GUI.color = Color.white;
        }

        // Magenta crystal marker on the goal (exit) room — drawn live (textured) over the geometry cache.
        private void DrawGoalMarker(IReadOnlyList<MinimapRoomSnapshot> rooms, MapProjection projection, bool fullMap)
        {
            EnsureMinimapTextures();
            if (_goalCrystalTexture == null)
            {
                return;
            }

            // Маркер выхода — самый сильный спойлер: он один отвечает на вопрос «куда идти»; rooms —
            // только раскрытые (RoomsForDrawing).

            float markerScale = ContentMarkerScale(fullMap);
            float w = 18f * markerScale;
            float h = 26f * markerScale;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (!rooms[i].IsExit)
                {
                    continue;
                }

                Vector2 c = ProjectWorld(Average(BuildRoomWorldCorners(rooms[i])), projection);
                Rect iconRect = new(c.x - w * 0.5f, c.y - h * 0.5f, w, h);
                if (_hasActiveMapClipRect && !iconRect.Overlaps(_activeMapClipRect))
                {
                    continue;
                }

                GUI.color = Color.white;
                GUI.DrawTexture(iconRect, _goalCrystalTexture, ScaleMode.ScaleToFit);
            }

            GUI.color = Color.white;
        }

        /// <summary>
        /// Отметки колб-фрагментов. Позиции берутся из сцены: колба спавнится ребёнком комнаты с
        /// именем «CodeFragmentCapsule_&lt;fragmentId&gt;», а при сборе получает <c>SetActive(false)</c>
        /// на самой себе, поэтому <c>activeSelf</c> — несобранная колба. Проверяется именно
        /// <c>activeSelf</c>, а не <c>activeInHierarchy</c>: комнату могут выключить целиком (стриминг
        /// дальних комнат), и по иерархии несобранная колба соврала бы «собрана».
        /// Несобранные рисуются заполненным кружком, собранные — контуром.
        /// На КОМПАКТНОЙ карте отметки показываются только в уже посещённых комнатах — иначе миникарта
        /// выдала бы расположение колб в неисследованной части уровня, которую она принципиально скрывает.
        /// </summary>
        private void DrawFragmentMarkers(MapProjection projection, bool fullMap)
        {
            if (!drawFragmentMarkers || _roomInstances == null)
            {
                return;
            }

            if (!fullMap && !drawFragmentMarkersOnCompactMap)
            {
                return;
            }

            // Отметки рисуются пачкой примитивов на маркер, а IMGUI зовёт OnGUI по нескольку раз за
            // кадр (Layout, ввод, Repaint). Рисует только Repaint — на остальных событиях эта работа
            // была бы потрачена впустую, а мини-карта уже была первой строкой в профайлере.
            if (Event.current != null && Event.current.type != EventType.Repaint)
            {
                return;
            }

            // Список колб — раньше проекции: на уровнях без колб (HUB, превью) это единственная
            // выполняемая здесь работа, и она обязана быть дешёвой.
            RefreshFragmentCapsuleCache();
            if (_fragmentCapsuleCount == 0)
            {
                return;
            }

            float markerRadius = FragmentMarkerRadiusPixels(fullMap);

            foreach (KeyValuePair<string, List<Transform>> kv in _fragmentCapsulesByNodeId)
            {
                // Исключения для большой карты больше нет: колба в непройденной комнате выдавала
                // и саму комнату, и то, что в ней есть что забрать.
                if (!_session.IsRoomRevealed(kv.Key))
                {
                    continue;
                }

                List<Transform> capsules = kv.Value;
                for (int i = 0; i < capsules.Count; i++)
                {
                    Transform capsule = capsules[i];
                    if (capsule == null)
                    {
                        continue;
                    }

                    Vector2 screen = ProjectWorld(new Vector2(capsule.position.x, capsule.position.z), projection);
                    DrawFragmentMarker(screen, markerRadius, capsule.gameObject.activeSelf);
                }
            }
        }

        /// <summary>
        /// Радиус отметки колбы в ЭКРАННЫХ пикселях. Считается в одном месте: и отрисовка, и
        /// страж-тест обязаны спорить об одном и том же числе. Полная карта берёт максимум из
        /// authored-размера и экранного минимума (иначе деление на <see cref="FullMapZoomDivider"/>
        /// вырождало отметку в невидимые 3 px), компактная — фиксированную долю от него, поэтому
        /// «компактная мельче полной» выполняется при любом authored-масштабе.
        /// </summary>
        private float FragmentMarkerRadiusPixels(bool fullMap)
        {
            float fullMapRadius = Mathf.Max(
                FullMapFragmentMarkerMinRadiusPixels,
                FragmentMarkerBaseRadiusPixels * ContentMarkerScale(true));
            return fullMap ? fullMapRadius : fullMapRadius * CompactFragmentMarkerRadiusFraction;
        }

        /// <summary>Одна отметка колбы: тёмный кант + заполненный кружок (несобранная) или контур (собранная).</summary>
        private void DrawFragmentMarker(Vector2 center, float radius, bool uncollected)
        {
            float outline = Mathf.Max(FragmentMarkerMinOutlinePixels, radius * FragmentMarkerOutlineFraction);
            float outerRadius = radius + outline;

            // Ранний выход по клипу карточки: маркеров может быть много, а за рамкой карты у них
            // всё равно не останется ни одного видимого примитива.
            if (_hasActiveMapClipRect)
            {
                Rect markerRect = new(
                    center.x - outerRadius, center.y - outerRadius, outerRadius * 2f, outerRadius * 2f);
                if (!markerRect.Overlaps(_activeMapClipRect))
                {
                    return;
                }
            }

            DrawDisc(center, outerRadius, FragmentMarkerOutlineColor);
            if (uncollected)
            {
                DrawDisc(center, radius, UncollectedFragmentColor);
                return;
            }

            // Собранная колба остаётся тёмным кружком со светлым ободом: «пусто» читается контуром,
            // а сама отметка не исчезает — по ней видно, что здесь уже подобрано.
            DrawRing(center, radius, Mathf.Max(1.6f, radius * 0.36f), CollectedFragmentColor);
        }

        /// <summary>
        /// Заполненный круг из горизонтальных полос по хорде окружности. Полосы, а не спицы из
        /// центра: спицы при радиусе больше пары пикселей расходятся у обода и кружок читается
        /// «звёздочкой» с просветами — именно так выглядели прежние отметки колб.
        /// </summary>
        private void DrawDisc(Vector2 center, float radius, Color color)
        {
            if (radius <= 0.1f)
            {
                return;
            }

            int rows = Mathf.Clamp(Mathf.CeilToInt(radius * 2f), 4, 64);
            float step = radius * 2f / rows;
            for (int i = 0; i < rows; i++)
            {
                float offsetY = -radius + (i + 0.5f) * step;
                float halfWidth = Mathf.Sqrt(Mathf.Max(0f, radius * radius - offsetY * offsetY));
                if (halfWidth <= 0.05f)
                {
                    continue;
                }

                float y = center.y + offsetY;
                DrawLine(new Vector2(center.x - halfWidth, y), new Vector2(center.x + halfWidth, y), step + 0.5f,
                    color);
            }
        }

        /// <summary>Обод окружности заданной толщины (дугами по хордам).</summary>
        private void DrawRing(Vector2 center, float radius, float thickness, Color color)
        {
            int segments = Mathf.Clamp(Mathf.CeilToInt(radius * 2.5f), 12, 48);
            for (int i = 0; i < segments; i++)
            {
                float angleA = i * Mathf.PI * 2f / segments;
                float angleB = (i + 1) * Mathf.PI * 2f / segments;
                Vector2 a = center + new Vector2(Mathf.Cos(angleA), Mathf.Sin(angleA)) * radius;
                Vector2 b = center + new Vector2(Mathf.Cos(angleB), Mathf.Sin(angleB)) * radius;
                DrawLine(a, b, thickness, color);
            }
        }

        private void InvalidateFragmentCapsuleCache()
        {
            _fragmentCapsulesByNodeId.Clear();
            _fragmentCapsuleCount = 0;
            _nextFragmentCapsuleScanTime = 0f;
        }

        /// <summary>
        /// Пересобирает список колб по комнатам не чаще <see cref="FragmentCapsuleRescanSeconds"/>.
        /// Ищутся ПРЯМЫЕ дети комнаты: <c>CodeFragmentRuntimeController.Spawn</c> инстанцирует капсулу
        /// ровно в <c>room.transform</c>, а обход всей иерархии комнаты (сотни трансформов на префаб)
        /// ради этого — лишний мусор в WebGL.
        /// </summary>
        private void RefreshFragmentCapsuleCache()
        {
            if (Time.unscaledTime < _nextFragmentCapsuleScanTime)
            {
                return;
            }

            _nextFragmentCapsuleScanTime = Time.unscaledTime + FragmentCapsuleRescanSeconds;
            _fragmentCapsuleCount = 0;

            foreach (KeyValuePair<string, List<Transform>> kv in _fragmentCapsulesByNodeId)
            {
                kv.Value.Clear();
            }

            if (_roomInstances == null)
            {
                return;
            }

            foreach (KeyValuePair<string, RoomInstance> kv in _roomInstances)
            {
                RoomInstance room = kv.Value;
                if (room == null)
                {
                    continue;
                }

                Transform roomTransform = room.transform;
                for (int i = 0; i < roomTransform.childCount; i++)
                {
                    Transform child = roomTransform.GetChild(i);
                    if (child == null
                        || !child.name.StartsWith(FragmentCapsuleNamePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!_fragmentCapsulesByNodeId.TryGetValue(kv.Key, out List<Transform> capsules))
                    {
                        capsules = new List<Transform>();
                        _fragmentCapsulesByNodeId[kv.Key] = capsules;
                    }

                    capsules.Add(child);
                    _fragmentCapsuleCount++;
                }
            }
        }

        private void DrawPlayerMarker(Vector2 center, float pixelsPerMeter)
        {
            EnsureMinimapTextures();

            float scale = PlayerMarkerScale(pixelsPerMeter);

            // Figma orange triangle marker (points up = player heading, since the map rotates with the player).
            if (_playerMarkerTexture != null)
            {
                float size = PlayerMarkerPixelSize(pixelsPerMeter);
                Rect markerRect = new(center.x - size * 0.5f, center.y - size * 0.5f, size, size);
                GUI.color = Color.white;
                GUI.DrawTexture(markerRect, _playerMarkerTexture, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
                return;
            }

            Color color = new(0.92f, 1f, 1f, 1f);
            Vector2 tip = center + new Vector2(0f, -8f * scale);
            Vector2 left = center + new Vector2(-6f * scale, 6f * scale);
            Vector2 right = center + new Vector2(6f * scale, 6f * scale);
            DrawLine(left, tip, 2.5f * scale, new Color(0f, 0f, 0f, 0.75f));
            DrawLine(right, tip, 2.5f * scale, new Color(0f, 0f, 0f, 0.75f));
            DrawLine(left, tip, 1.6f * scale, color);
            DrawLine(right, tip, 1.6f * scale, color);
            DrawDot(center, 3.2f * scale, new Color(0.2f, 1f, 0.88f, 1f));
        }

        private float ContentMarkerScale(bool fullMap)
        {
            float scale = CompactContentScaleValue();
            return fullMap ? scale / FullMapZoomDivider : scale;
        }

        private float PlayerMarkerScale(float pixelsPerMeter) =>
            CompactContentScaleValue() * CompactPlayerMarkerBoost * pixelsPerMeter
            / CompactMapScalePixelsPerMeter();

        private float PlayerMarkerPixelSize(float pixelsPerMeter) =>
            PlayerMarkerPixels * PlayerMarkerScale(pixelsPerMeter);

        private static bool ShouldDrawDoor(DoorState state)
        {
            return state == DoorState.Open
                   || state == DoorState.Closed
                   || state == DoorState.Locked
                   || state == DoorState.ExitGate;
        }

        private Color DoorColor(DoorState state)
        {
            switch (state)
            {
                case DoorState.Locked:
                    return lockedDoorColor;
                case DoorState.ExitGate:
                    return exitRoomColor;
                case DoorState.Closed:
                    return new Color(0.8f, 0.95f, 1f, 0.92f);
                case DoorState.Open:
                default:
                    return connectionColor;
            }
        }

        private static Vector2 ResolveDoorWorldCenter(RoomInstance instance, DoorSocket door)
        {
            IDoorSocketDescriptor descriptor = FindDoorDescriptor(instance, door != null ? door.SocketId : null);
            if (instance != null && descriptor != null)
            {
                Vector3 local = new(descriptor.ConnectionLocalXMeters(), 0f, descriptor.ConnectionLocalZMeters());
                Vector3 world = instance.transform.TransformPoint(local);
                return new Vector2(world.x, world.z);
            }

            return door != null
                ? new Vector2(door.Anchor.position.x, door.Anchor.position.z)
                : Vector2.zero;
        }

        private static float ResolveDoorFrameWidth(RoomInstance instance, DoorSocket door)
        {
            IDoorSocketDescriptor descriptor = FindDoorDescriptor(instance, door != null ? door.SocketId : null);
            return descriptor != null
                ? descriptor.FrameWidthMeters()
                : DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(door != null ? door.Size : DoorSize.Medium);
        }

        private static Vector3 DoorDirectionToWorld(DoorDirection direction, Transform roomRoot)
        {
            Vector3 local = direction switch
            {
                DoorDirection.North => Vector3.forward,
                DoorDirection.South => Vector3.back,
                DoorDirection.West => Vector3.left,
                DoorDirection.East => Vector3.right,
                _ => Vector3.right
            };

            return roomRoot != null ? roomRoot.TransformDirection(local) : local;
        }

        private static Vector2[] BuildRoomWorldCorners(PlacedRoom room)
        {
            float cellSize = room.WorldPose.HasValue
                ? LevelMinimapSession.CellSizeMeters
                : LevelMinimapSession.LogicalCellSize;
            float width = room.Footprint.WidthCells * cellSize;
            float depth = room.Footprint.DepthCells * cellSize;
            int rotation = room.WorldPose.HasValue
                ? Mathf.RoundToInt(new Quaternion(
                    room.WorldPose.Value.RotationX,
                    room.WorldPose.Value.RotationY,
                    room.WorldPose.Value.RotationZ,
                    room.WorldPose.Value.RotationW).eulerAngles.y)
                : room.Pose.RotationDegrees;

            if (room.WorldPose.HasValue)
            {
                return BuildRoomWorldCorners(room.WorldPose.Value.X, room.WorldPose.Value.Z, width, depth, rotation);
            }

            return BuildRoomWorldCorners(room.Pose.CellX, room.Pose.CellY, width, depth, rotation);
        }

        private Vector2[] BuildRoomWorldCorners(MinimapRoomSnapshot room)
        {
            if (room.Cells == null || room.Cells.Count == 0)
            {
                return BuildRoomWorldCorners(room, 0f, 0f, room.Width, room.Depth);
            }

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < room.Cells.Count; i++)
            {
                MinimapCellSnapshot cell = room.Cells[i];
                minX = Mathf.Min(minX, cell.X);
                minY = Mathf.Min(minY, cell.Y);
                maxX = Mathf.Max(maxX, cell.X + cell.Width);
                maxY = Mathf.Max(maxY, cell.Y + cell.Depth);
            }

            return BuildRoomWorldCorners(room, minX, minY, maxX - minX, maxY - minY);
        }

        private Vector2[] BuildRoomWorldCorners(MinimapRoomSnapshot room, MinimapCellSnapshot cell) => BuildRoomWorldCorners(room, cell.X, cell.Y, cell.Width, cell.Depth);

        private Vector2 RoomLocalToWorld(MinimapRoomSnapshot room, Vector2 local) => new Vector2(room.X, room.Y) + Rotate(local, room.RotationDegrees);

        private Vector2[] BuildRoomWorldCorners(
            MinimapRoomSnapshot room,
            float localX,
            float localY,
            float width,
            float depth)
        {
            Vector2 origin = new(room.X, room.Y);
            return new[] { origin + Rotate(new Vector2(localX, localY), room.RotationDegrees), origin + Rotate(new Vector2(localX + width, localY), room.RotationDegrees), origin + Rotate(new Vector2(localX + width, localY + depth), room.RotationDegrees), origin + Rotate(new Vector2(localX, localY + depth), room.RotationDegrees) };
        }

        private static List<LocalEdge> BuildBoundaryEdges(IReadOnlyList<MinimapCellSnapshot> cells)
        {
            Dictionary<LocalEdgeKey, LocalEdge> edgesByKey = new();
            for (int i = 0; i < cells.Count; i++)
            {
                MinimapCellSnapshot cell = cells[i];
                Vector2 a = new(cell.X, cell.Y);
                Vector2 b = new(cell.X + cell.Width, cell.Y);
                Vector2 c = new(cell.X + cell.Width, cell.Y + cell.Depth);
                Vector2 d = new(cell.X, cell.Y + cell.Depth);
                AddBoundaryEdge(edgesByKey, a, b);
                AddBoundaryEdge(edgesByKey, b, c);
                AddBoundaryEdge(edgesByKey, c, d);
                AddBoundaryEdge(edgesByKey, d, a);
            }

            return new List<LocalEdge>(edgesByKey.Values);
        }

        private static void AddBoundaryEdge(Dictionary<LocalEdgeKey, LocalEdge> edgesByKey, Vector2 a, Vector2 b)
        {
            LocalEdgeKey key = new(a, b);
            if (edgesByKey.ContainsKey(key))
            {
                edgesByKey.Remove(key);
                return;
            }

            edgesByKey[key] = new LocalEdge(a, b);
        }

        // Collapses collinear, touching boundary edges into single long runs so a straight
        // wall renders as one line. Boundary edges from axis-aligned cells are themselves
        // axis-aligned (horizontal: equal Y, vertical: equal X); anything else is passed through.
        private static List<LocalEdge> MergeCollinearBoundaryEdges(List<LocalEdge> edges)
        {
            Dictionary<int, List<Vector2>> horizontalRuns = new();
            Dictionary<int, List<Vector2>> verticalRuns = new();
            List<LocalEdge> result = new();

            for (int i = 0; i < edges.Count; i++)
            {
                LocalEdge edge = edges[i];
                if (Mathf.Approximately(edge.A.y, edge.B.y))
                {
                    int key = Mathf.RoundToInt(edge.A.y * 1000f);
                    if (!horizontalRuns.TryGetValue(key, out List<Vector2> spans))
                    {
                        spans = new List<Vector2>();
                        horizontalRuns[key] = spans;
                    }

                    spans.Add(new Vector2(Mathf.Min(edge.A.x, edge.B.x), Mathf.Max(edge.A.x, edge.B.x)));
                }
                else if (Mathf.Approximately(edge.A.x, edge.B.x))
                {
                    int key = Mathf.RoundToInt(edge.A.x * 1000f);
                    if (!verticalRuns.TryGetValue(key, out List<Vector2> spans))
                    {
                        spans = new List<Vector2>();
                        verticalRuns[key] = spans;
                    }

                    spans.Add(new Vector2(Mathf.Min(edge.A.y, edge.B.y), Mathf.Max(edge.A.y, edge.B.y)));
                }
                else
                {
                    result.Add(edge);
                }
            }

            foreach (KeyValuePair<int, List<Vector2>> kv in horizontalRuns)
            {
                float y = kv.Key / 1000f;
                foreach (Vector2 span in MergeSpans(kv.Value))
                {
                    result.Add(new LocalEdge(new Vector2(span.x, y), new Vector2(span.y, y)));
                }
            }

            foreach (KeyValuePair<int, List<Vector2>> kv in verticalRuns)
            {
                float x = kv.Key / 1000f;
                foreach (Vector2 span in MergeSpans(kv.Value))
                {
                    result.Add(new LocalEdge(new Vector2(x, span.x), new Vector2(x, span.y)));
                }
            }

            return result;
        }

        // Merges 1D intervals (stored as Vector2 lo/hi) that touch or overlap into maximal spans.
        private static List<Vector2> MergeSpans(List<Vector2> spans)
        {
            spans.Sort((p, q) => p.x.CompareTo(q.x));
            List<Vector2> merged = new();
            for (int i = 0; i < spans.Count; i++)
            {
                Vector2 span = spans[i];
                if (merged.Count > 0 && span.x <= merged[merged.Count - 1].y + 0.001f)
                {
                    Vector2 last = merged[merged.Count - 1];
                    if (span.y > last.y)
                    {
                        merged[merged.Count - 1] = new Vector2(last.x, span.y);
                    }
                }
                else
                {
                    merged.Add(span);
                }
            }

            return merged;
        }

        private static Vector2[] BuildRoomWorldCorners(float x, float y, float width, float depth, int rotationDegrees) => BuildRoomWorldCorners(x, y, 0f, 0f, width, depth, rotationDegrees);

        private static Vector2[] BuildRoomWorldCorners(
            float x,
            float y,
            float localX,
            float localY,
            float width,
            float depth,
            int rotationDegrees)
        {
            Vector2 origin = new(x, y);
            return new[] { origin + Rotate(new Vector2(localX, localY), rotationDegrees), origin + Rotate(new Vector2(localX + width, localY), rotationDegrees), origin + Rotate(new Vector2(localX + width, localY + depth), rotationDegrees), origin + Rotate(new Vector2(localX, localY + depth), rotationDegrees) };
        }

        internal Transform PlayerTransform
        {
            get => playerTransform;
            set => playerTransform = value;
        }

        internal Vector2 ProjectPlayerPosition(MapProjection projection)
        {
            Transform player = ResolvePlayerTransform();
            return player != null
                ? ProjectWorld(new Vector2(player.position.x, player.position.z), projection)
                : projection.ScreenCenter;
        }

        private static Vector2 ProjectWorld(Vector2 world, MapProjection projection)
        {
            Vector2 delta = world - projection.WorldCenter;
            Vector2 rotated = Rotate(delta, -projection.YawDegrees);
            return projection.ScreenCenter + new Vector2(rotated.x * projection.Scale, -rotated.y * projection.Scale);
        }

        private static Vector2 Average(Vector2[] points)
        {
            if (points == null || points.Length == 0)
            {
                return Vector2.zero;
            }

            Vector2 sum = Vector2.zero;
            for (int i = 0; i < points.Length; i++)
            {
                sum += points[i];
            }

            return sum / points.Length;
        }

        private static float ResolveDoorLocalYaw(RoomInstance instance, DoorSocket door)
        {
            IDoorSocketDescriptor descriptor = FindDoorDescriptor(instance, door != null ? door.SocketId : null);
            return descriptor != null ? descriptor.LocalYawDegrees : door != null ? door.LocalYawDegrees : 0f;
        }

        private static IDoorSocketDescriptor FindDoorDescriptor(RoomInstance instance, string socketId)
        {
            if (instance == null || instance.Template == null || string.IsNullOrEmpty(socketId))
            {
                return null;
            }

            IReadOnlyList<IDoorSocketDescriptor> descriptors = ((IRoomTemplateDescriptor)instance.Template).Doors;
            for (int i = 0; i < descriptors.Count; i++)
            {
                IDoorSocketDescriptor descriptor = descriptors[i];
                if (descriptor != null && string.Equals(descriptor.SocketId, socketId, StringComparison.Ordinal))
                {
                    return descriptor;
                }
            }

            return null;
        }

        private static Vector2 Rotate(Vector2 point, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector2(point.x * cos + point.y * sin, -point.x * sin + point.y * cos);
        }

        private void DrawDot(Vector2 center, float radius, Color color)
        {
            if (_hasActiveMapClipRect)
            {
                Rect dotRect = new(center.x - radius, center.y - radius, radius * 2f, radius * 2f);
                if (!dotRect.Overlaps(_activeMapClipRect))
                {
                    return;
                }
            }

            color = ApplyRoomRevealTint(color);
            if (_isRenderingStaticMapCache)
            {
                DrawSolidRectToStaticCache(new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f),
                    color);
                return;
            }

            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f), _pixel);
        }

        private void DrawLine(Vector2 a, Vector2 b, float thickness, Color color)
        {
            if (_hasActiveMapClipRect)
            {
                Rect clipRect = _activeMapClipRect;
                float inset = Mathf.Max(0f, thickness * 0.5f);
                clipRect.xMin += inset;
                clipRect.xMax -= inset;
                clipRect.yMin += inset;
                clipRect.yMax -= inset;
                if (clipRect.width <= 0.01f || clipRect.height <= 0.01f)
                {
                    return;
                }

                if (!TryClipLineToRect(clipRect, ref a, ref b))
                {
                    return;
                }
            }

            float length = Vector2.Distance(a, b);
            if (length <= 0.01f)
            {
                return;
            }

            color = ApplyRoomRevealTint(color);
            if (_isRenderingStaticMapCache)
            {
                DrawLineToStaticCache(a, b, thickness, color);
                return;
            }

            Matrix4x4 matrix = GUI.matrix;
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.color = color;
            GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, length, thickness), _pixel);
            GUI.matrix = matrix;
        }

        private static void DrawSolidRectToStaticCache(Rect rect, Color color)
        {
            GL.Color(color);
            GL.Vertex3(rect.xMin, rect.yMin, 0f);
            GL.Vertex3(rect.xMax, rect.yMin, 0f);
            GL.Vertex3(rect.xMax, rect.yMax, 0f);
            GL.Vertex3(rect.xMin, rect.yMax, 0f);
        }

        // Soft translucent fill of a room's footprint (one GL quad per cell, so rotated rooms fill
        // correctly). Only runs inside the static-cache GL.QUADS pass.
        private void FillRoomFootprint(MinimapRoomSnapshot room, MapProjection projection, bool fullMap)
        {
            Color fill = ApplyRoomRevealTint(ResolveRoomFillColor(room, fullMap));
            if (room.Cells != null && room.Cells.Count > 0)
            {
                for (int i = 0; i < room.Cells.Count; i++)
                {
                    FillQuad(BuildRoomWorldCorners(room, room.Cells[i]), projection, fill);
                }

                return;
            }

            FillQuad(BuildRoomWorldCorners(room), projection, fill);
        }

        private void FillQuad(Vector2[] worldCorners, MapProjection projection, Color color)
        {
            if (_isRenderingStaticMapCache)
            {
                FillProjectedQuad(worldCorners, projection, color);
                return;
            }

            FillProjectedQuadLive(worldCorners, projection, color);
        }

        /// <summary>
        /// Заливка клетки живым слоем (проявляющаяся комната в упрощённом режиме): проекция карты —
        /// поворот с равномерным масштабом, поэтому клетка остаётся прямоугольником и рисуется одним
        /// повёрнутым <c>GUI.DrawTexture</c>. Без этого заливка появлялась бы скачком в момент запекания.
        /// </summary>
        private void FillProjectedQuadLive(Vector2[] worldCorners, MapProjection projection, Color color)
        {
            if (worldCorners == null || worldCorners.Length < 4 || _pixel == null)
            {
                return;
            }

            Vector2 a = ProjectWorld(worldCorners[0], projection);
            Vector2 b = ProjectWorld(worldCorners[1], projection);
            Vector2 d = ProjectWorld(worldCorners[3], projection);
            Vector2 c = ProjectWorld(worldCorners[2], projection);
            Vector2 center = (a + c) * 0.5f;
            float width = Vector2.Distance(a, b);
            float height = Vector2.Distance(a, d);
            if (width <= 0.01f || height <= 0.01f)
            {
                return;
            }

            float half = Mathf.Max(width, height) * 0.75f;
            if (_hasActiveMapClipRect
                && !new Rect(center.x - half, center.y - half, half * 2f, half * 2f).Overlaps(_activeMapClipRect))
            {
                return;
            }

            Matrix4x4 matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg, center);
            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - width * 0.5f, center.y - height * 0.5f, width, height), _pixel);
            GUI.matrix = matrix;
        }

        private static void FillProjectedQuad(Vector2[] worldCorners, MapProjection projection, Color color)
        {
            if (worldCorners == null || worldCorners.Length < 4)
            {
                return;
            }

            GL.Color(color);
            GL.Vertex3(ProjectWorld(worldCorners[0], projection).x, ProjectWorld(worldCorners[0], projection).y, 0f);
            GL.Vertex3(ProjectWorld(worldCorners[1], projection).x, ProjectWorld(worldCorners[1], projection).y, 0f);
            GL.Vertex3(ProjectWorld(worldCorners[2], projection).x, ProjectWorld(worldCorners[2], projection).y, 0f);
            GL.Vertex3(ProjectWorld(worldCorners[3], projection).x, ProjectWorld(worldCorners[3], projection).y, 0f);
        }

        private Color ResolveRoomFillColor(MinimapRoomSnapshot room, bool fullMap)
        {
            float alpha = fullMap ? 0.30f : 0.40f;
            if (room.IsCurrent)
            {
                return new Color(0.96f, 0.99f, 1f, alpha + 0.16f);
            }

            if (room.IsStart)
            {
                return new Color(startRoomColor.r, startRoomColor.g, startRoomColor.b, alpha);
            }

            if (room.IsExit)
            {
                return new Color(exitRoomColor.r, exitRoomColor.g, exitRoomColor.b, alpha);
            }

            // Soft light-cyan corridors/rooms like the Figma card.
            return new Color(0.74f, 0.91f, 1f, alpha);
        }

        private static void DrawLineToStaticCache(Vector2 a, Vector2 b, float thickness, Color color)
        {
            Vector2 direction = b - a;
            float length = direction.magnitude;
            if (length <= 0.01f)
            {
                return;
            }

            Vector2 normal = new Vector2(-direction.y / length, direction.x / length) * (thickness * 0.5f);
            GL.Color(color);
            GL.Vertex3(a.x + normal.x, a.y + normal.y, 0f);
            GL.Vertex3(b.x + normal.x, b.y + normal.y, 0f);
            GL.Vertex3(b.x - normal.x, b.y - normal.y, 0f);
            GL.Vertex3(a.x - normal.x, a.y - normal.y, 0f);
        }

        private static bool TryClipLineToRect(Rect rect, ref Vector2 a, ref Vector2 b)
        {
            float minX = rect.xMin;
            float maxX = rect.xMax;
            float minY = rect.yMin;
            float maxY = rect.yMax;

            float dx = b.x - a.x;
            float dy = b.y - a.y;
            float t0 = 0f;
            float t1 = 1f;

            if (!ClipLineAxis(-dx, a.x - minX, ref t0, ref t1))
            {
                return false;
            }

            if (!ClipLineAxis(dx, maxX - a.x, ref t0, ref t1))
            {
                return false;
            }

            if (!ClipLineAxis(-dy, a.y - minY, ref t0, ref t1))
            {
                return false;
            }

            if (!ClipLineAxis(dy, maxY - a.y, ref t0, ref t1))
            {
                return false;
            }

            if (t1 < 1f)
            {
                b = new Vector2(a.x + t1 * dx, a.y + t1 * dy);
            }

            if (t0 > 0f)
            {
                a = new Vector2(a.x + t0 * dx, a.y + t0 * dy);
            }

            return true;
        }

        private static bool ClipLineAxis(float p, float q, ref float t0, ref float t1)
        {
            const float epsilon = 0.00001f;
            if (Mathf.Abs(p) < epsilon)
            {
                return q >= 0f;
            }

            float r = q / p;
            if (p < 0f)
            {
                if (r > t1)
                {
                    return false;
                }

                if (r > t0)
                {
                    t0 = r;
                }
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }

                if (r < t1)
                {
                    t1 = r;
                }
            }

            return true;
        }

        private void EnsurePixel()
        {
            if (_pixel != null)
            {
                return;
            }

            _pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();
        }

        private enum StaticMapCacheStatus
        {
            NotBuilt = 0,
            Built = 1,
            MaterialUnavailable = 2,
            TextureNotCreated = 3
        }

        private sealed class StaticMapCache : IDisposable
        {
            public RenderTexture Texture { get; private set; }
            private int _width;
            private int _height;
            private int _stateVersion;
            private int _settingsHash;
            private ProjectionPoseKey _poseKey;
            private bool _isValid;

            public bool IsValid(
                int width,
                int height,
                int stateVersion,
                int settingsHash,
                ProjectionPoseKey poseKey)
            {
                return _isValid
                       && Texture != null
                       && _width == width
                       && _height == height
                       && _stateVersion == stateVersion
                       && _settingsHash == settingsHash
                       && _poseKey.Equals(poseKey);
            }

            public void EnsureTexture(int width, int height)
            {
                if (Texture != null && _width == width && _height == height)
                {
                    return;
                }

                Dispose();
                _width = width;
                _height = height;
                Texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                Created = Texture.Create();
            }

            /// <summary>Видеокарта приняла текстуру кэша (WebGL может отказать большой карте).</summary>
            public bool Created { get; private set; }

            /// <summary>Чем закончилось последнее построение — для строки первой отрисовки большой карты.</summary>
            public StaticMapCacheStatus Status { get; set; }

            public void MarkValid(
                int width,
                int height,
                int stateVersion,
                int settingsHash,
                ProjectionPoseKey poseKey)
            {
                _width = width;
                _height = height;
                _stateVersion = stateVersion;
                _settingsHash = settingsHash;
                _poseKey = poseKey;
                _isValid = true;
            }

            public void Dispose()
            {
                _isValid = false;
                Status = StaticMapCacheStatus.NotBuilt;
                Created = false;
                if (Texture == null)
                {
                    return;
                }

                Texture.Release();
#if UNITY_EDITOR
                if (!UnityEngine.Application.isPlaying)
                {
                    DestroyImmediate(Texture);
                }
                else
#endif
                {
                    Destroy(Texture);
                }

                Texture = null;
            }
        }

        private readonly struct ProjectionPoseKey : IEquatable<ProjectionPoseKey>
        {
            public static readonly ProjectionPoseKey NoPlayer = new(false, 0, 0, 0);

            private readonly bool _hasPlayer;
            private readonly int _xCentimeters;
            private readonly int _zCentimeters;
            private readonly int _yawCentidegrees;

            public ProjectionPoseKey(bool hasPlayer, int xCentimeters, int zCentimeters, int yawCentidegrees)
            {
                _hasPlayer = hasPlayer;
                _xCentimeters = xCentimeters;
                _zCentimeters = zCentimeters;
                _yawCentidegrees = yawCentidegrees;
            }

            public bool Equals(ProjectionPoseKey other)
            {
                return _hasPlayer == other._hasPlayer
                       && _xCentimeters == other._xCentimeters
                       && _zCentimeters == other._zCentimeters
                       && _yawCentidegrees == other._yawCentidegrees;
            }

            public override bool Equals(object obj) => obj is ProjectionPoseKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _hasPlayer.GetHashCode();
                    hash = (hash * 397) ^ _xCentimeters;
                    hash = (hash * 397) ^ _zCentimeters;
                    hash = (hash * 397) ^ _yawCentidegrees;
                    return hash;
                }
            }
        }

        internal readonly struct MapProjection
        {
            public readonly Vector2 WorldCenter;
            public readonly Vector2 ScreenCenter;
            public readonly float Scale;
            public readonly float YawDegrees;

            public MapProjection(Vector2 worldCenter, Vector2 screenCenter, float scale, float yawDegrees)
            {
                WorldCenter = worldCenter;
                ScreenCenter = screenCenter;
                Scale = scale;
                YawDegrees = yawDegrees;
            }
        }

        private readonly struct LocalEdge
        {
            public readonly Vector2 A;
            public readonly Vector2 B;

            public LocalEdge(Vector2 a, Vector2 b)
            {
                A = a;
                B = b;
            }
        }

        private readonly struct LocalPointKey : IEquatable<LocalPointKey>
        {
            private readonly int _x;
            private readonly int _y;

            public LocalPointKey(Vector2 point)
            {
                _x = Mathf.RoundToInt(point.x * 1000f);
                _y = Mathf.RoundToInt(point.y * 1000f);
            }

            public bool Equals(LocalPointKey other) => _x == other._x && _y == other._y;

            public int CompareTo(LocalPointKey other)
            {
                if (_x != other._x)
                {
                    return _x.CompareTo(other._x);
                }

                return _y.CompareTo(other._y);
            }

            public override bool Equals(object obj) => obj is LocalPointKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_x * 397) ^ _y;
                }
            }
        }

        private readonly struct LocalEdgeKey : IEquatable<LocalEdgeKey>
        {
            private readonly LocalPointKey _a;
            private readonly LocalPointKey _b;

            public LocalEdgeKey(Vector2 a, Vector2 b)
            {
                LocalPointKey ak = new(a);
                LocalPointKey bk = new(b);
                if (Compare(ak, bk) <= 0)
                {
                    _a = ak;
                    _b = bk;
                }
                else
                {
                    _a = bk;
                    _b = ak;
                }
            }

            public bool Equals(LocalEdgeKey other) => _a.Equals(other._a) && _b.Equals(other._b);

            public override bool Equals(object obj) => obj is LocalEdgeKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_a.GetHashCode() * 397) ^ _b.GetHashCode();
                }
            }

            private static int Compare(LocalPointKey a, LocalPointKey b) => a.CompareTo(b);
        }
    }
}
