using RedoSchool.Features.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Presentation
{
    /// <summary>Как рисуется подсказка маршрута на картах.</summary>
    public enum RouteHintStyle
    {
        /// <summary>Бегущий пунктир из точек.</summary>
        Dots = 0,

        /// <summary>Сплошная линия с бегущим по ней бликом.</summary>
        Line = 1
    }

    /// <summary>
    /// Настройки миникарты, которые дизайнер меняет без программиста: раскрытие комнат при открытии
    /// двери и подсказка маршрута — пунктир на картах и путь на полу (длительности, цвета, движение искр,
    /// шаг и бюджет звёздочек). Контроллер миникарты создаётся рантаймом (authored-экземпляра нет),
    /// поэтому ассет лежит в <c>Resources/Minimap/LevelMinimapSettings</c> рядом с текстурами карточки;
    /// поле <c>settings</c> у контроллера позволяет подменить его явно.
    /// </summary>
    [CreateAssetMenu(menuName = "Neoxider/LevelGeneration/Minimap Settings", fileName = "LevelMinimapSettings")]
    public sealed class LevelMinimapSettings : ScriptableObject
    {
        public const string ResourcesPath = "Minimap/LevelMinimapSettings";

        /// <summary>Free width along the trail is measured up to this distance; beyond it nothing limits the stars.</summary>
        public const float RouteHintClearanceCapMeters = 4f;

        [Header("Раскрытие комнат")]
        [SerializeField] [Min(0f)]
        [Tooltip("Через сколько секунд после открытия двери комната за ней появляется на картах.")]
        private float roomRevealDelaySeconds = 1f;

        [SerializeField] [Min(0f)]
        [Tooltip("Длительность анимации проявления комнаты на картах. 0 — появляется мгновенно.")]
        private float roomRevealAnimationSeconds = 0.6f;

        [Header("Подсказка маршрута")]
        [SerializeField] [Tooltip("Показывать подсказку маршрута, если ученик долго не продвигается к выходу.")]
        private bool routeHintEnabled = true;

        [SerializeField] [Min(1f)]
        [Tooltip("Сколько секунд без прогресса по пути к выходу ждать до подсказки. Пока открыта UI-панель, "
                 + "вопрос или брифинг, время не идёт.")]
        private float routeHintIdleSeconds = 60f;

        [SerializeField] [Tooltip("Пунктир из точек или сплошная линия.")]
        private RouteHintStyle routeHintStyle = RouteHintStyle.Dots;

        [SerializeField] [Tooltip("Цвет подсказки.")]
        private Color routeHintColor = new(1f, 0.84f, 0.32f, 1f);

        [SerializeField] [Tooltip("Тёмный кант под подсказкой — чтобы она читалась поверх светлых стен карты.")]
        private Color routeHintOutlineColor = new(0.02f, 0.05f, 0.09f, 0.85f);

        [SerializeField] [Min(0.5f)] [Tooltip("Радиус точки пунктира на компактной карте, пиксели.")]
        private float routeHintDotRadiusPixels = 3.2f;

        [SerializeField] [Min(2f)] [Tooltip("Шаг между точками пунктира вдоль пути, пиксели.")]
        private float routeHintDotSpacingPixels = 11f;

        [SerializeField] [Min(0.5f)] [Tooltip("Толщина сплошной линии на компактной карте, пиксели.")]
        private float routeHintLineWidthPixels = 3f;

        [SerializeField] [Min(0.5f)]
        [Tooltip("Во сколько раз точки и линия крупнее на большой карте (клавиша M).")]
        private float routeHintFullMapSizeMultiplier = 1.25f;

        [SerializeField] [Min(0f)] [Tooltip("Скорость бега пунктира/блика к цели, пиксели в секунду.")]
        private float routeHintFlowSpeedPixelsPerSecond = 26f;

        [SerializeField] [Min(0.01f)] [Tooltip("Длительность появления и исчезновения подсказки, секунды.")]
        private float routeHintFadeSeconds = 0.35f;

        [SerializeField] [Min(0.01f)] [Tooltip("За сколько секунд линия «прорисовывается» от игрока до цели.")]
        private float routeHintDrawOnSeconds = 0.8f;

        [SerializeField] [Min(1f)] [Tooltip("Радиус пульсирующего маркера у целевой двери, пиксели.")]
        private float routeHintTargetMarkerRadiusPixels = 7f;

        [SerializeField] [Min(0.1f)] [Tooltip("Период пульса маркера у целевой двери, секунды.")]
        private float routeHintTargetPulseSeconds = 1.2f;

        [SerializeField] [Min(0.05f)]
        [Tooltip("Как часто можно пересчитывать путь, секунды: перепланирование ограничено этим интервалом "
                 + "и не идёт каждый кадр. Путь дешёвый, но не бесплатный.")]
        private float routeHintReplanIntervalSeconds = 0.25f;

        [SerializeField] [Min(0.1f)]
        [Tooltip("Насколько далеко от пути может отойти ученик, прежде чем путь строится заново от него, метры. "
                 + "Пока он ближе, путь только тянет своё начало за ним.")]
        private float routeHintReplanDeviationMeters = 1.5f;

        [SerializeField] [Min(0f)]
        [Tooltip("За сколько секунд старая линия пути перетекает в новую, когда ученик отошёл от неё и путь "
                 + "построился заново: линия плывёт, а не прыгает вбок. 0 — мгновенная замена.")]
        private float routeHintReplanBlendSeconds = 0.35f;

        [Header("Подсказка маршрута — геометрия пути (общая для пола и миникарты)")]
        [SerializeField] [Range(0f, 3f)]
        [Tooltip("Отступ пути от поверхности стен, метры: путь идёт по середине коридоров и комнат и не "
                 + "прижимается к стенам и колоннам. 0 — прежний кратчайший путь с отступом 0,45 м.")]
        private float routeHintPathWallClearanceMeters = 0.9f;

        [SerializeField] [Min(0f)]
        [Tooltip("Прямой участок перед дверным проёмом по перпендикуляру к стене, метры: путь проходит центр "
                 + "проёма, а не срезает косяк.")]
        private float routeHintPathDoorApproachMeters = 1.4f;

        [SerializeField] [Min(0f)]
        [Tooltip("На сколько метров путь продолжается за целевую дверь в следующую комнату, метры: путь идёт "
                 + "СКВОЗЬ дверь, а не обрывается перед ней. Отрезок прямой и обрезается по полу и стенам "
                 + "следующей комнаты. 0 — путь заканчивается в двери.")]
        private float routeHintPathDoorLeadOutMeters = 2f;

        [SerializeField] [Min(0f)]
        [Tooltip("На сколько метров впереди ученика (вдоль пути) начинается видимая часть пути: звёзды "
                 + "начинаются не у его ног.")]
        private float routeHintPathStartOffsetMeters = 0.75f;

        [SerializeField] [Min(0f)]
        [Tooltip("Сколько метров пути у цели видно всегда, даже когда ученик почти дошёл, метры.")]
        private float routeHintPathMinVisibleMeters = 0.5f;

        [Header("Подсказка маршрута — путь на полу")]
        [SerializeField]
        [Tooltip("Сначала рисовать путь цепочкой звёздочек над полом, потом пунктиром на миникарте. Выключено или "
                 + "нет префаба — пунктир на миникарте появляется сразу.")]
        private bool routeHintFloorEnabled = true;

        [SerializeField]
        [Tooltip("Префаб цепочки: дочерний ParticleSystem с аддитивной четырёхлучевой звездой; "
                 + "без полосы и стрелок.")]
        private RouteHintFloorView routeHintFloorViewPrefab;

        [SerializeField] [Min(0.05f)]
        [Tooltip("За сколько секунд лента проявляется от игрока до цели.")]
        private float routeHintFloorRevealSeconds = 1.25f;

        [SerializeField] [Min(0.05f)]
        [Tooltip("За сколько секунд лента гаснет, когда ученик пошёл к цели.")]
        private float routeHintFloorFadeSeconds = 0.9f;

        [SerializeField] [Min(0.05f)]
        [Tooltip("Unscaled seconds to hide the floor ribbon and minimap hint when the hint is no longer wanted.")]
        private float routeHintFloorHideFadeSeconds = 0.18f;

        [SerializeField] [Min(0f)]
        [Tooltip("Устарело: ходьба по пути подсказку НЕ гасит (решение владельца 2026-10-16: подсказка держится, "
                 + "пока ученик не продвинулся в верную комнату). 0 — не гасить; значение больше нуля вернёт "
                 + "угасание ленты при приближении к цели.")]
        private float routeHintFloorApproachMeters = 0f;

        [SerializeField] [Min(0.05f)] [Tooltip("Ширина служебной геометрии пути, метры; меш не рисуется.")]
        private float routeHintFloorWidthMeters = 0.55f;

        [SerializeField] [Min(0f)] [Tooltip("Наибольший радиус скругления углов общего пути, метры.")]
        private float routeHintFloorCornerRadiusMeters = 0.9f;

        [SerializeField] [Min(0f)]
        [Tooltip("Насколько скруглённый угол общего пути может отойти от точки поворота, метры. Планировщик "
                 + "добавляет это к отступу от стен, чтобы скругление не съело отступ.")]
        private float routeHintFloorCornerMaxDeviationMeters = 0.25f;

        [SerializeField] [Min(0.05f)] [Tooltip("Шаг точек общего пути (и лучей к полу), метры.")]
        private float routeHintFloorSampleSpacingMeters = 0.3f;

        [SerializeField] [Min(0f)] [Tooltip("На сколько лента приподнята над полом, метры.")]
        private float routeHintFloorLiftMeters = 0.03f;

        [SerializeField] [Tooltip("Слои коллайдеров пола, по которым лента ищет высоту лучом вниз.")]
        private LayerMask routeHintFloorLayers = 1;

        [SerializeField] [Min(0.05f)]
        [Tooltip("С какой высоты над полом предыдущей точки ленты луч ищет пол под следующей, метры. Выше "
                 + "ступенек и пандусов, ниже потолка.")]
        private float routeHintFloorProbeHeightMeters = 1.2f;

        [SerializeField] [Min(0f)]
        [Tooltip("Наибольший перепад пола между соседними точками ленты, метры. Больше — это ящик или декор, "
                 + "лента остаётся на прежней высоте; лестница и пандус проходят ступенька за ступенькой.")]
        private float routeHintFloorMaxStepMeters = 0.35f;

        [SerializeField] [Min(0f)] [Tooltip("Длина плавного появления искр у игрока, метры.")]
        private float routeHintFloorStartFadeMeters = 0.35f;

        [SerializeField] [Min(0.05f)] [Tooltip("Расстояние между искрами вдоль пути, метры.")]
        private float routeHintFloorSparkleSpacingMeters = 0.1f;

        [SerializeField] [Range(0, RouteHintFloorView.HardParticleCap)]
        [Tooltip("Максимум искр одновременно: по умолчанию 400; защитный предел WebGL — 512.")]
        private int routeHintFloorMaxSparkles = 400;

        [SerializeField] [ColorUsage(true, true)]
        [Tooltip("Золотисто-жёлтый цвет и яркость искр (HDR).")]
        private Color routeHintFloorSparkleColor = new(2.2f, 1.65f, 0.12f, 1f);

        [SerializeField] [Min(0.01f)] [Tooltip("Базовый размер четырёхлучевой звезды, метры.")]
        private float routeHintFloorSparkleSizeMeters = 0.1f;

        [SerializeField] [Min(0.05f)]
        [Tooltip("Наименьший размер звезды в долях базового (0,5 — вдвое меньше).")]
        private float routeHintFloorSparkleSizeMinScale = 0.5f;

        [SerializeField] [Min(0.05f)]
        [Tooltip("Наибольший размер звезды в долях базового (2 — вдвое больше).")]
        private float routeHintFloorSparkleSizeMaxScale = 2f;

        [SerializeField] [Min(0.1f)]
        [Tooltip("Перекос размеров: больше 1 — большинство звёзд мелкие, несколько крупных; 1 — поровну.")]
        private float routeHintFloorSparkleSizeSkew = 3f;

        [SerializeField] [Min(0f)]
        [Tooltip("Средняя скорость дрейфа искр по пути к цели, метры в секунду; у каждой звезды своя качка вокруг неё.")]
        private float routeHintFloorSparkleSpeedMetersPerSecond = 0.5f;

        [SerializeField] [Min(0f)] [Tooltip("Высота звёздочек над найденным лучом полом, метры.")]
        private float routeHintFloorSparkleHeightMeters = 0.175f;

        [SerializeField] [Min(0f)]
        [Tooltip("Полуширина потока: по умолчанию ±0,2 м. Рассеивание дополнительно зажато свободной шириной "
                 + "вдоль пути, так что звёзды не ложатся на стены, на поворотах и в дверях тоже.")]
        private float routeHintFloorSparkleLateralJitterMeters = 0.2f;

        [SerializeField] [Min(0f)] [Tooltip("Амплитуда колебания высоты, метры.")]
        private float routeHintFloorSparkleVerticalJitterMeters = 0.125f;

        [SerializeField] [Min(0f)]
        [Tooltip("Частота быстрого мерцания искр по игровому времени, герцы.")]
        private float routeHintFloorSparkleTwinkleHertz = 2.4f;

        [SerializeField] [Range(0f, 1f)]
        [Tooltip("Разброс частоты мерцания между звёздами, доля частоты: 0,5 — от половины до полуторной.")]
        private float routeHintFloorSparkleTwinkleHertzSpread = 0.5f;

        [SerializeField] [Range(0f, 1f)] [Tooltip("Глубина мерцания прозрачности: 0 — постоянная яркость.")]
        private float routeHintFloorSparkleTwinkleAlpha = 0.45f;

        [SerializeField] [Range(0f, 1f)] [Tooltip("Глубина мерцания размера: 0 — постоянный размер.")]
        private float routeHintFloorSparkleTwinkleSize = 0.3f;

        [SerializeField] [Range(0f, 1f)]
        [Tooltip("Доля звёзд, которые мерцают долго и медленно (меньшинство), а не быстро.")]
        private float routeHintFloorSparkleLongTwinkleFraction = 0.2f;

        [SerializeField] [Min(0.1f)]
        [Tooltip("Период долгого мерцания, секунды (у каждой звезды в пределах ±30%).")]
        private float routeHintFloorSparkleLongTwinkleSeconds = 3.5f;

        [SerializeField] [Range(0f, 1f)]
        [Tooltip("Глубина долгого мерцания: 0,9 — звезда почти гаснет и разгорается заново.")]
        private float routeHintFloorSparkleLongTwinkleDepth = 0.9f;

        [SerializeField] [Min(0f)]
        [Tooltip("Амплитуда медленных качаний звезды вдоль пути, метры: собственная вариация скорости.")]
        private float routeHintFloorSparkleAlongWobbleMeters = 0.12f;

        [SerializeField] [Min(0f)]
        [Tooltip("Амплитуда медленных качаний звезды вбок, метры (вместе с полушириной потока не больше 0,3 м).")]
        private float routeHintFloorSparkleLateralWobbleMeters = 0.08f;

        [SerializeField] [Min(0.1f)]
        [Tooltip("Средний период качаний звезды, секунды (у каждой от 0,6 до 1,4 от него).")]
        private float routeHintFloorSparkleWobbleSeconds = 5f;

        [SerializeField] [Min(0f)]
        [Tooltip("Зазор до поверхности ближайшей стены, который звезда не нарушает, метры.")]
        private float routeHintFloorSparkleWallMarginMeters = 0.3f;

        [SerializeField] [Min(0f)] [Tooltip("На каком расстоянии до цели искры начинают гаснуть, метры.")]
        private float routeHintFloorSparkleEndFadeMeters = 0.65f;

        [SerializeField] [Min(0f)] [Tooltip("Мягкость фронта проявления цепочки, метры.")]
        private float routeHintFloorSparkleRevealFeatherMeters = 0.35f;

        public float RoomRevealDelaySeconds => Mathf.Max(0f, roomRevealDelaySeconds);
        public float RoomRevealAnimationSeconds => Mathf.Max(0f, roomRevealAnimationSeconds);
        public bool RouteHintEnabled => routeHintEnabled;

        internal void SetRouteHintEnabled(bool value) => routeHintEnabled = value;
        public float RouteHintIdleSeconds => Mathf.Max(1f, routeHintIdleSeconds);
        public RouteHintStyle RouteHintStyle => routeHintStyle;
        public Color RouteHintColor => routeHintColor;
        public Color RouteHintOutlineColor => routeHintOutlineColor;
        public float RouteHintDotRadiusPixels => Mathf.Max(0.5f, routeHintDotRadiusPixels);
        public float RouteHintDotSpacingPixels => Mathf.Max(2f, routeHintDotSpacingPixels);
        public float RouteHintLineWidthPixels => Mathf.Max(0.5f, routeHintLineWidthPixels);
        public float RouteHintFullMapSizeMultiplier => Mathf.Max(0.5f, routeHintFullMapSizeMultiplier);
        public float RouteHintFlowSpeedPixelsPerSecond => Mathf.Max(0f, routeHintFlowSpeedPixelsPerSecond);
        public float RouteHintFadeSeconds => Mathf.Max(0.01f, routeHintFadeSeconds);
        public float RouteHintDrawOnSeconds => Mathf.Max(0.01f, routeHintDrawOnSeconds);
        public float RouteHintTargetMarkerRadiusPixels => Mathf.Max(1f, routeHintTargetMarkerRadiusPixels);
        public float RouteHintTargetPulseSeconds => Mathf.Max(0.1f, routeHintTargetPulseSeconds);
        public float RouteHintReplanIntervalSeconds => Mathf.Max(0.05f, routeHintReplanIntervalSeconds);
        public float RouteHintReplanDeviationMeters => Mathf.Max(0.1f, routeHintReplanDeviationMeters);
        public float RouteHintReplanBlendSeconds => Mathf.Max(0f, routeHintReplanBlendSeconds);
        public float RouteHintPathWallClearanceMeters => Mathf.Clamp(routeHintPathWallClearanceMeters, 0f, 3f);
        public float RouteHintPathDoorApproachMeters => Mathf.Max(0f, routeHintPathDoorApproachMeters);
        public float RouteHintPathDoorLeadOutMeters => Mathf.Max(0f, routeHintPathDoorLeadOutMeters);
        public float RouteHintPathStartOffsetMeters => Mathf.Max(0f, routeHintPathStartOffsetMeters);
        public float RouteHintPathMinVisibleMeters => Mathf.Max(0f, routeHintPathMinVisibleMeters);

        public bool RouteHintFloorEnabled => routeHintFloorEnabled;
        public RouteHintFloorView RouteHintFloorViewPrefab => routeHintFloorViewPrefab;
        public float RouteHintFloorRevealSeconds => Mathf.Max(0.05f, routeHintFloorRevealSeconds);
        public float RouteHintFloorFadeSeconds => Mathf.Max(0.05f, routeHintFloorFadeSeconds);
        public float RouteHintFloorHideFadeSeconds => Mathf.Max(0.05f, routeHintFloorHideFadeSeconds);
        public float RouteHintFloorApproachMeters => Mathf.Max(0f, routeHintFloorApproachMeters);
        public float RouteHintFloorWidthMeters => Mathf.Max(0.05f, routeHintFloorWidthMeters);
        public float RouteHintFloorCornerRadiusMeters => Mathf.Max(0f, routeHintFloorCornerRadiusMeters);
        public float RouteHintFloorCornerMaxDeviationMeters => Mathf.Max(0f, routeHintFloorCornerMaxDeviationMeters);
        public float RouteHintFloorSampleSpacingMeters => Mathf.Max(0.05f, routeHintFloorSampleSpacingMeters);
        public float RouteHintFloorLiftMeters => Mathf.Max(0f, routeHintFloorLiftMeters);
        public LayerMask RouteHintFloorLayers => routeHintFloorLayers;
        public float RouteHintFloorProbeHeightMeters => Mathf.Max(0.05f, routeHintFloorProbeHeightMeters);
        public float RouteHintFloorMaxStepMeters => Mathf.Max(0f, routeHintFloorMaxStepMeters);
        public float RouteHintFloorStartFadeMeters => Mathf.Max(0f, routeHintFloorStartFadeMeters);
        public float RouteHintFloorSparkleSpacingMeters => Mathf.Max(0.05f, routeHintFloorSparkleSpacingMeters);
        public int RouteHintFloorMaxSparkles => Mathf.Clamp(routeHintFloorMaxSparkles, 0, RouteHintFloorView.HardParticleCap);
        public Color RouteHintFloorSparkleColor => routeHintFloorSparkleColor;
        public float RouteHintFloorSparkleSizeMeters => Mathf.Max(0.01f, routeHintFloorSparkleSizeMeters);
        public float RouteHintFloorSparkleSizeMinScale => Mathf.Max(0.05f, routeHintFloorSparkleSizeMinScale);

        public float RouteHintFloorSparkleSizeMaxScale =>
            Mathf.Max(RouteHintFloorSparkleSizeMinScale, routeHintFloorSparkleSizeMaxScale);

        public float RouteHintFloorSparkleSizeSkew => Mathf.Max(0.1f, routeHintFloorSparkleSizeSkew);
        public float RouteHintFloorSparkleSpeedMetersPerSecond => Mathf.Max(0f, routeHintFloorSparkleSpeedMetersPerSecond);
        public float RouteHintFloorSparkleHeightMeters => Mathf.Max(0f, routeHintFloorSparkleHeightMeters);
        public float RouteHintFloorSparkleLateralJitterMeters => Mathf.Max(0f, routeHintFloorSparkleLateralJitterMeters);
        public float RouteHintFloorSparkleVerticalJitterMeters => Mathf.Max(0f, routeHintFloorSparkleVerticalJitterMeters);
        public float RouteHintFloorSparkleTwinkleHertz => Mathf.Max(0f, routeHintFloorSparkleTwinkleHertz);
        public float RouteHintFloorSparkleTwinkleHertzSpread => Mathf.Clamp01(routeHintFloorSparkleTwinkleHertzSpread);
        public float RouteHintFloorSparkleTwinkleAlpha => Mathf.Clamp01(routeHintFloorSparkleTwinkleAlpha);
        public float RouteHintFloorSparkleTwinkleSize => Mathf.Clamp01(routeHintFloorSparkleTwinkleSize);
        public float RouteHintFloorSparkleLongTwinkleFraction => Mathf.Clamp01(routeHintFloorSparkleLongTwinkleFraction);
        public float RouteHintFloorSparkleLongTwinkleSeconds => Mathf.Max(0.1f, routeHintFloorSparkleLongTwinkleSeconds);
        public float RouteHintFloorSparkleLongTwinkleDepth => Mathf.Clamp01(routeHintFloorSparkleLongTwinkleDepth);
        public float RouteHintFloorSparkleAlongWobbleMeters => Mathf.Max(0f, routeHintFloorSparkleAlongWobbleMeters);
        public float RouteHintFloorSparkleLateralWobbleMeters => Mathf.Max(0f, routeHintFloorSparkleLateralWobbleMeters);
        public float RouteHintFloorSparkleWobbleSeconds => Mathf.Max(0.1f, routeHintFloorSparkleWobbleSeconds);
        public float RouteHintFloorSparkleWallMarginMeters => Mathf.Max(0f, routeHintFloorSparkleWallMarginMeters);
        public float RouteHintFloorSparkleEndFadeMeters => Mathf.Max(0f, routeHintFloorSparkleEndFadeMeters);
        public float RouteHintFloorSparkleRevealFeatherMeters => Mathf.Max(0f, routeHintFloorSparkleRevealFeatherMeters);

        /// <summary>Длительности и пороги показа ленты для <c>DecideRouteHintRevealUseCase</c>.</summary>
        public RouteHintRevealTiming CreateRouteHintRevealTiming() => new(RouteHintFloorRevealSeconds,
            RouteHintFloorFadeSeconds, RouteHintFloorHideFadeSeconds, RouteHintFloorApproachMeters);

        /// <summary>
        /// Настройки планировщика: отступ от стен плюс допуск скругления углов (скругление не должно съесть отступ)
        /// и прямой участок у дверей. Нулевой отступ — прежний кратчайший путь.
        /// </summary>
        public RouteHintPathOptions CreateRouteHintPathOptions() =>
            RouteHintPathWallClearanceMeters <= 0f
                ? RouteHintPathOptions.Legacy
                : new RouteHintPathOptions(RouteHintPathWallClearanceMeters + RouteHintFloorCornerMaxDeviationMeters,
                    RouteHintPathDoorApproachMeters, RouteHintPathDoorLeadOutMeters);

        /// <summary>Скругление общего пути: радиус, допуск отхода и шаг точек из настроек подсказки.</summary>
        public RouteHintRibbonShape CreateRouteHintSmoothingShape() => CreateRouteHintRibbonShape();

        /// <summary>Вид потока звёзд: все значения из этого ассета.</summary>
        public RouteHintStarStyle CreateRouteHintStarStyle() => new(
            RouteHintFloorSparkleSpacingMeters, RouteHintFloorMaxSparkles, RouteHintFloorSparkleSizeMeters,
            RouteHintFloorSparkleSizeMinScale, RouteHintFloorSparkleSizeMaxScale, RouteHintFloorSparkleSizeSkew,
            RouteHintFloorSparkleSpeedMetersPerSecond, RouteHintFloorSparkleHeightMeters, RouteHintFloorLiftMeters,
            RouteHintFloorSparkleLateralJitterMeters, RouteHintFloorSparkleVerticalJitterMeters,
            RouteHintFloorSparkleTwinkleHertz, RouteHintFloorSparkleTwinkleHertzSpread,
            RouteHintFloorSparkleTwinkleAlpha, RouteHintFloorSparkleTwinkleSize,
            RouteHintFloorSparkleLongTwinkleFraction, RouteHintFloorSparkleLongTwinkleSeconds,
            RouteHintFloorSparkleLongTwinkleDepth, RouteHintFloorSparkleAlongWobbleMeters,
            RouteHintFloorSparkleLateralWobbleMeters, RouteHintFloorSparkleWobbleSeconds,
            RouteHintFloorStartFadeMeters, RouteHintFloorSparkleEndFadeMeters,
            RouteHintFloorSparkleRevealFeatherMeters, RouteHintFloorSparkleWallMarginMeters);

        /// <summary>Форма ленты для <c>RouteHintRibbonMeshBuilder</c>.</summary>
        public RouteHintRibbonShape CreateRouteHintRibbonShape() => new(RouteHintFloorWidthMeters,
            RouteHintFloorCornerRadiusMeters, RouteHintFloorCornerMaxDeviationMeters,
            RouteHintFloorSampleSpacingMeters, RouteHintFloorLiftMeters, RouteHintFloorMaxStepMeters);
    }
}
