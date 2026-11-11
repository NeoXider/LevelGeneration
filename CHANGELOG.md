# Neoxider LevelGeneration Changelog

## 5.0.0 — 2026-11-11 breaking: пространства имён `RedoSchool.LevelGeneration.*`

**Ломающее изменение для кода, который ссылается на пакет:** корень пространств имён переименован с
`RedoSchool.Features.LevelGeneration` на `RedoSchool.LevelGeneration` — теперь он совпадает с именами сборок
пакета (`RedoSchool.LevelGeneration`, `.Domain`, `.Unity`, `.Runtime`, `.Runtime.Domain`, `.Editor`). Хвосты не
менялись: `…Generation.Application.Pipeline`, `…Generation.Domain.Models`, `…Runtime.Presentation` и остальные
на месте. Потребителю достаточно заменить префикс в `using` и в полных именах типов.

- **Зачем.** Слово `Features` осталось от раскладки игры, из которой пакет был вынесен, и не значило в нём
  ничего. Хуже того, четыре пространства (`…Editor`, `…Runtime.Application`, `…Runtime.Infrastructure`,
  `…Runtime.Presentation`) игра RedoSchool объявляла у себя под теми же именами, то есть пакет и игра делили
  пространство имён, и по имени нельзя было понять, чей это тип. После переименования пакет занимает свой
  корень, игра — свой (`RedoSchool.World.*`).
- **Что не менялось:** имена сборок, GUID скриптов и ассетов (префабы, сцены и `ScriptableObject` потребителя
  продолжают ссылаться на те же скрипты), поведение генерации, `LayoutContract` (`2`) — сохранённые чекпойнты
  миссий остаются действительными.
- **Страж границы.** `LevelGenerationPackageInfoTests` запрещает ядру генерации (`Generation/Domain`,
  `Generation/Application`) импортировать пространства игры; список запретов переведён на её новые корни
  (`RedoSchool.World`, `RedoSchool.Teacher`, `RedoSchool.Player`, `RedoSchool.Hub`, `RedoSchool.Platform`,
  `RedoSchool.CodePlayground`) и на собственный рантайм-слой пакета (`RedoSchool.LevelGeneration.Runtime`).
- Сериализованные поля `m_EditorClassIdentifier` в ассетах пакета обновлены на новые имена; на загрузку
  ассетов они не влияют.
- **Тест, знавший игру, убран из пакета.** `DoorAutoOpenTriggerTests.SpawnerPreservesClosedRuntimeOnlyNoneDoor`
  искал по строке тип `LevelSpawner` из сборки игры `RedoSchool.World` и вызывал его приватный метод через
  reflection: пакет проверял чужой код, а вне RedoSchool тест был красным. Проверка переехала в тесты игры,
  в пакете остались только тесты его собственных типов.

## 4.1.1 — 2026-10-29 fix: лишние .meta пустых папок

- **Пакет без `.meta` пустых папок.** В репозитории лежали `.meta` пяти папок без файлов (`Application`,
  `Generation/Application/Topology`, `Generation/Infrastructure/DependencyHints`, `Generation/Infrastructure/Runtime`,
  `Tests/EditMode/Editor`); git пустые папки не хранит, и Unity при каждом импорте пакета по git URL писал
  предупреждение «A meta data file (.meta) exists but its folder … can't be found». `.meta` удалены, код,
  раскладка и `LayoutContract` (`2`) не менялись.

## 4.1.0 — 2026-10-28 fix: детерминизм сида, честные лабиринты, ключ до замка, путь в обход предметов

Раскладка для того же сида меняется (правильная дверь лабиринта, заглушки, бюджет размещения): чекпойнты миссий
со старым сидом надо сбросить.

- **Правильная дверь лабиринта — по сиду.** `SocketBacktrackingRoomPlacer` назначает двери комнаты-лабиринта
  (`MazeDoorPlan` → `MazeDoorAssignment`) независимо от позы: правильная выбирается ГСЧ сида среди дверей ответа,
  неверные — из остальных; каждый перезапуск поиска берёт следующую по кругу дверь, так что неудачный выбор не
  повторяется во всех перезапусках. Раньше правильной почти всегда была дверь «прямо по курсу» (мировой угол ≈ 0°).
  Тесты: `Place_MazeCorrectAnswerDoor_IsChosenBySeed_NotAlwaysStraightAhead`,
  `Place_DenseMazeBranchRoles_AssignEachAnswerSocketExactlyOnce`, `Place_FailingCorrectDoorAssignment_IsRotatedOnRestart`.
- **Заглушки неверных ответов никогда не ставятся с наложением и не блокируют обязательные комнаты.** Место под
  них резервируется при постановке лабиринта: позы, где заглушка не помещается, идут в конец очереди, подходящие
  заглушки ставятся тем же шагом поиска. Если поддерево под таким кандидатом провалилось, поиск повторяет того же
  кандидата без резерва (заглушки — в конце и только без наложения), поэтому заглушка не может занять место
  тоннеля правильного ответа. Не поместившаяся заглушка не размещается; `MissionGenerator` убирает её из графа
  (`MazeWrongAnswerDeadEnds` — общий набор best-effort узлов для размещения и проверки покрытия), дверь
  запечатывается, в `LastWarnings` — предупреждение с той самой дверью ответа (`RoomPlacementReport` через
  `IReportingRoomPlacer`). `LevelValidator`: пересечение с основным путём или любой не-тупиковой комнатой — ошибка
  (сид отклоняется), предупреждение — только две заглушки `DeadEnd`; проверяются все пары, а не до первого
  пересечения. Тесты: `Place_ReservedCapThatWouldBlockTheCorrectSuccessor_IsDeferredInsteadOfFailing`,
  `Place_ReservesWrongAnswerCapBeforeTheMainPathContinues`, `Place_MazePoseWhereTheCapFits_IsPreferredOverAPoseWhereItDoesNot`,
  `Place_WrongAnswerCapThatCannotFit_IsNotForcePlacedWithOverlap`, `Place_Report_NamesTheMazeDoorOfAnUnplacedCap`,
  `BuildLayout_WrongAnswerCapThatDoesNotFit_IsSealedWithWarning_NeverOverlapped`,
  `Validate_DeadEndCapOverlappingNonDeadEndRoom_IsError`, `Validate_ReportsEveryOverlap_SoADeadEndWarningCannotHideAnError`.
- **Исход сида не зависит от часов.** Убран лимит стадии размещения по времени (`max(500 мс, 0.4 бюджета)`) и
  переход «таймаут стадии → следующий сид» в обход TrimBranches/MandatoryOnly. Размещение ограничено счётным
  бюджетом `SocketBacktrackingRoomPlacer.CandidateEvaluationBudget` (оценённые позы; по умолчанию
  `max(40000, узлы × 2000)`), исчерпанная стадия переходит к следующей стадии того же сида. Часы
  (`GenerationTimeout` × `GenerationTimeBudgetMultiplier`) только прерывают прогон `TimeoutException`; граф V2
  стадии строится без собственного таймера. Часы прогона подставляемые (`IGenerationClock`,
  `MissionGenerator.GenerationClockFactory`; с ними системный таймер не взводится). Исправлены ложные комментарии о
  детерминизме (`MissionGenerator`, `LayoutPacingMissionGenerator.ResolveRetryStages`). Для калибровки бюджета в
  `LastDebugCounters`: `CandidateEvaluations`/`PlacementBudgetExhaustions` (последняя попытка) и
  `RunCandidateEvaluations`, `RunMaxCandidateEvaluationsPerAttempt`, `RunPlacementBudgetExhaustions` (весь прогон).
  Тесты: `BuildLayout_SameSeedProducesSameLayout_RegardlessOfTimeBudgetAndMultiplier` (синхронный и кооперативный
  путь, подставные часы), `BuildLayout_RunOverItsTimeBudget_AbortsWithTimeout_InsteadOfSwitchingSeed`,
  `Place_ExhaustedCandidateBudget_StopsDeterministically`.
- **Ключ не ставится за собственный замок, у замка всегда есть ключ.** `KeyLockReachability`: ключ годится, только
  если до него дойти от старта, не входя в комнату-замок; на заглушку неверного ответа (best-effort, её могут убрать)
  ключ не ставится. Один обход у генератора (`FindBestSourceNode`) и у валидатора (`ValidateDependencyHints`,
  проверка каждого замка). Замок без достижимого ключа — `KeyDependencyUnsatisfied`: генератор отклоняет стадию до
  размещения, валидатор — раскладку, где замок поставлен, а ключа нет. Тесты:
  `BuildLayout_DependencyHintNeverPutsKeyBehindItsOwnLock`, `BuildLayout_RejectsLockWhoseOnlyKeyCandidateIsBehindIt`,
  `BuildLayout_DependencyHintNeverPutsKeyOnWrongAnswerDeadEnd`, `Validate_RejectsKeyPlacedBehindItsOwnLock`,
  `Validate_RejectsLockWithoutAnyKeyRoom`, `Validate_AcceptsKeyBeforeItsLock`.
- **Нет общего статического состояния предупреждений.** Удалён `SocketBacktrackingRoomPlacer.ForcedMazeDeadEndWarnings`;
  предупреждения о заглушках — в `MissionGenerator.LastWarnings` экземпляра. Тест: `Placer_KeepsNoStaticMutableCollections`
  (типы полей и сами значения, включая вложенные типы).
- **Повторяющийся id подключаемого сокета — явная ошибка.** `DoorSocketIdRules`: `DuplicateDoorSocketId` с именем
  комнаты и сокета до размещения (`MissionGenerator`), в `LevelValidator` и в `RoomPrefabGridValidator.ValidatePrefab`.
  Раньше такие двери молча делили одно состояние. Тесты: `Validate_RejectsRoomTemplateWithDuplicateConnectableSocketIds`,
  `BuildLayout_RejectsDuplicateConnectableSocketIdsBeforePlacement`, `ValidatePrefab_RejectsDuplicateSocketIds`,
  `DoorSocketIdRules_IgnoresDuplicatesAmongNonConnectableSockets`.
- **Случайные касания дверей.** `IncidentalDoorContactMode` (`Seal` по умолчанию, `ConnectOutsideGraph` — песочница)
  и конструктор `DoorConnector(IncidentalDoorContactMode)`; `DoorConnector(true)` = `ConnectOutsideGraph`.
  Генератор миссий проверяет топологию по графу, поэтому для него — `new DoorConnector()`; при `true`
  `MissionGenerator` пишет предупреждение. Тесты: `Connect_IncidentalDoorContactMode_SealsByDefault_AndConnectsOnlyOutsideGraphWhenAsked`,
  `BuildLayout_WarnsWhenDoorConnectorConnectsIncidentalContactsOutsideGraph`.
- **Производительность (исход прежний).** Строки причин отказа — только при включённой трассировке; дешёвые
  отказы (замок, размер, рама) — до построения позы; начальные состояния дверей — одна копия на шаблон; стены
  позы-кандидата — в переиспользуемый буфер, синус/косинус — раз на трансформацию. `LevelValidator`: стены каждой
  комнаты трансформируются один раз, AABB-broadphase перед SAT, соединения — по словарю. `MissionGenerator`:
  снимки шаблонов, пул тупиков, подсказки зависимостей и план контента V2 — раз на прогон; диагностические
  строки строятся только при `DiagnosticLogsEnabled`.
- **Лицензия.** Начиная с 4.1.0 пакет выпускается под PolyForm Small Business 1.0.0 (`LICENSE.md`): бесплатно для
  личного и некоммерческого использования и для малого бизнеса; крупным компаниям нужна коммерческая лицензия.
  Прежние версии остаются под своей лицензией.
- **Контракт раскладки.** `LevelGenerationPackageInfo.LayoutContract` (сейчас `2`) меняется, только когда тот же
  сид и та же конфигурация дают другую раскладку — как в 4.1.0. Хозяин, сохраняющий прогресс по сиду, хранит
  это значение и начинает прогон заново, если оно другое, а не раскладывает сохранённые id комнат по чужим
  комнатам. Известное ограничение генерации: при нескольких парах ключ/замок взаимная блокировка (ключ
  первого замка за вторым замком и наоборот) не отсекается — проверяется каждая пара по отдельности.
- **Ломающее изменение API хозяина карты.** `IMinimapGameplayContext.IsGameplayKeyboardBlocked` заменён на
  `WasFullMapTogglePressed`: хозяин сам читает свою клавишу большой карты и молчит, пока клавиатурой владеет UI.
  Пакет больше не ищет менеджер ввода через reflection; без хозяина — `M` старого менеджера ввода, если он включён.
  Тест-страж `PackageRuntime_UsesNoReflection`.
- **Подсказка пути обходит предметы.** Раньше планировщик знал только стены миникарты: путь шёл впритирку к стенам
  и сквозь мебель, а со стола — через стул на высоте стола. Теперь `RouteHintGeometry` знает препятствия
  (`RouteHintObstacle`, выпуклые следы твёрдых коллайдеров; классификация — `ClassifyRouteHintColliderUseCase`,
  сбор — `RouteHintObstacleCollector`): путь держит отступ от стен и предметов и никогда их не пересекает; старт
  внутри предмета (на столе) сначала сходит на ближайший свободный пол; лента пути спускается со стола, а не
  висит на его высоте (`ResolveRouteHintFloorHeightUseCase` принимает спуск). Шаг со стола проверяется всем
  отрезком, а не только точкой схода: пересечь можно только край своего предмета, но не стену и не соседний
  предмет (скамья, придвинутая к столу), и так же для запасного круга точек. Предмет обходится по углам: одна
  точка обхода на угол по внешней биссектрисе (`TryObstacleCornerDetour`) вместо четырёх на сторону — граф
  видимости обставленной комнаты меньше в четыре раза, пар проверки — в шестнадцать. Тесты
  `RouteHintObstacleTests` (в том числе стол со скамьёй, запасной круг у шкафа, комната с 40 предметами).
  Известное ограничение: если между предметами нет прохода шире плеча ученика или дверь стоит на помосте выше
  шага, подсказки в этой комнате нет (исход `Unreachable`) — проходимый помост помечается не коллайдером-коробкой.
  Тесты: комнаты в `LevelMinimapControllerWiringTests` стоят вплотную по `LogicalCellSize` (прежний шаг 2 оставлял
  пустую клетку, и тесты закрытого прохода проходили по ложной причине); спрайт отметки фрагмента в
  `LevelMinimapRuntimeCostTests` читается с GPU — его текстура нечитаема намеренно.
- **Настоящие соединения не считаются запертыми.** Проход решает дверь стороны «откуда»; сторона «куда» закрывает
  его, только если там живая запертая дверь. Комната за дверью раскрывается на карте с обеих сторон
  (`LevelMinimapSession.ScheduleRevealAcrossDoor`).
- **Производительность миникарты.** Статический слой карты запекается один раз в мировой ориентации и рисуется
  одним повёрнутым квадом (пересобирается только при изменении карты, настроек или размера, а не каждый кадр);
  `OnGUI` рисует только на Repaint; камера, геометрия комнат и подписи фрагментов кэшируются; стены для подгонки
  точек пути отбираются один раз по близости; высоты пола ленты пути пробуются раз на перестройку, а не на каждый
  кадр смешивания; маркеры фрагментов — заранее запечённые спрайты. `AdaptiveLevelLoadingController.Bind` забывает
  комнаты прошлой миссии. Тесты `LevelMinimapRuntimeCostTests` и расширенные тесты контроллера карты.
  Повёрнутый слой кладётся на экран не матрицей GUI: IMGUI отсекает прямоугольник по группе до поворота, и при
  любом взгляде, кроме «на север», карта выходила пустой. Слой каждый кадр одним GL-квадом поворачивается в
  экранную текстуру размером с область карты (`Hidden/BlitCopy`), а она кладётся на экран без поворота. Слой
  запекается без смешивания: смешивание по альфе в прозрачную текстуру возводило альфу в квадрат, и стены
  бледнели до невидимости. Проверено в игре: миникарта и большая карта при повороте взгляда на 130°.
  Шаг со стола: если чистого шага нет (стол обставлен со всех сторон), берётся ближайший свободный пол, а не
  пропадает подсказка.

## 4.0.0 — 2026-10-28 change(runtime): пакет не зависит от игры

- **Отдельный публичный репозиторий** https://github.com/NeoXider/LevelGeneration, лицензия PolyForm
  Noncommercial 1.0.0 (`LICENSE.md`); коммерческая — по запросу. Тесты находят пакет по asmdef и Resources, а
  не по `Assets/ThirdParty/...`: проверяются и встроенными, и установленными из git в `Packages`.

- **Ввод игры — через контракт.** `IMinimapGameplayContext` (где ученик — `RouteHintPresence`, занята ли
  клавиатура интерфейсом, строка состояния управления для диагностики) и точка подключения
  `MinimapGameplay.Context`. Без реализации карта автономна: ученик в мире, клавиатура свободна.
  `RouteHintPresenceUseCase` (решение по `PlayerControlState` игры) переехал в игру.
- **Журнал — через приёмники.** `LevelGenerationRuntimeLog.Info/Warn` передают сообщение, файл и метод вызова
  в `InfoSink`/`WarnSink`; без приёмника — консоль Unity.
- **Пути проекта — настройка, а не константы.** `LevelGenerationProjectPaths` (`RoomPrefabRoots`,
  `RoomPrefabFolder`, `DefaultRoomPoolPath`) вместо зашитых `Assets/_source/...` в `RoomDatabase`,
  `RoomAuthoringToolkitWindow` и `RoomTemplateEditor`; кнопка пула — «Use Default Pool». `TilesetCatalog`
  ищет `TilesetSO` по всему проекту, а не в трёх папках игры. Файл сетки `LevelGenerationGridMetrics.cs`
  находится по скрипту; в установленном (не embedded) пакете инструмент сообщает, что он только для чтения.
- **Значения по умолчанию окон — лениво.** Room Authoring Toolkit и Room Compatibility Report читают
  `LevelGenerationProjectPaths` при первом `OnGUI`: окно, восстановленное после перекомпиляции, включается раньше
  `[InitializeOnLoad]` проекта. Новое `RoomResourcesPath` — папка Resources для отчёта совместимости.
- **Зависимости объявлены:** `com.unity.render-pipelines.universal` (шейдер ленты маршрута включает URP
  `Core.hlsl`), `com.unity.nuget.newtonsoft-json` (тесты).
- **Ломающее:** `RedoSchool.LevelGeneration.Runtime` больше не ссылается на `RedoSchool.Player.Input` и
  `RedoSchool.Platform.Shared.Logging`; потребитель сам подключает контекст и приёмники журнала.
  Страж — `LevelGenerationIndependenceTests` (объявленные и фактические ссылки всех сборок пакета).

## 2026-10-16 change(route-hint): путь идёт сквозь дверь и плавно перестраивается

- **Сквозь целевую дверь.** `RouteHintPathOptions.DoorLeadOutMeters` / `routeHintPathDoorLeadOutMeters` (2 м)
  продолжает маршрут за дверь прямым отрезком, обрезанным по полу и стенам следующей комнаты; нуль — маршрут
  кончается в двери, как раньше. Тесты: `PlanRouteHintPathClearanceTests`.
- **Плавная замена линии.** `RouteHintTrail.AssignBlended` / `AdvanceBlend` и `routeHintReplanBlendSeconds`
  (0,35 с): при уходе ученика с линии новая линия перетекает из старой, а не заменяет её рывком. Тесты:
  `RouteHintTrailTests`.

## 2026-10-16 change(route-hint): один путь на двоих, вдали от стен, держится до прогресса; игрок в проёме

- **Один путь.** Звёзды на полу и пунктир миникарты рисуют одну линию `RouteHintTrail`: углы скруглены один раз,
  заменяется она только при перепланировании, между ними двигается лишь видимое начало по тем же точкам.
  Раньше пол сглаживал и пересэмплировал путь по-своему и застывал в начале показа, пунктир тянулся от живой
  позиции — линии расходились. Вьюшка пола получает точки без изменений (`BuildFromAxis`), высоту пола зондирует
  один раз на перепланирование.
- **Не жмётся к стенам.** Планировщик получил режим отступа (`RouteHintPathOptions`): путь держится от
  поверхности стен не ближе `routeHintPathWallClearanceMeters` (0,9 м; плюс допуск скругления, чтобы оно не
  съело отступ), идёт по середине коридоров и комнат (узлы в центре и четвертях ячеек), проход проёма — по
  центру прямым участком `routeHintPathDoorApproachMeters` (1,4 м) перпендикулярно стене. В узких комнатах
  отступ ступенчато уменьшается, затем прежний поиск — маршрут не теряется. Боковой разброс звёзд зажат
  свободной шириной вдоль линии, поэтому поток не ложится на стены, на поворотах и в дверях тоже. Допуск
  скругления `routeHintFloorCornerMaxDeviationMeters` поднят с 0,12 до 0,25 м (углы скруглены заметнее,
  планировщик добавляет ту же величину к отступу).
- **Следует за игроком.** Видимое начало — на `routeHintPathStartOffsetMeters` (0,75 м) впереди проекции
  игрока по пути; у цели всегда видно `routeHintPathMinVisibleMeters`. Игрок в пределах
  `routeHintReplanDeviationMeters` (1,5 м) от линии только тянет начало за собой; дальше — перепланирование
  от него, не чаще `routeHintReplanIntervalSeconds` и никогда не каждый кадр (`DecideRouteHintReplanUseCase`).
  Периодическое перепланирование раз в интервал заменено сигнатурой запертых дверей и теми же триггерами смены
  мира (цель, раскрытие, задание).
- **Подсказка держится, пока ученик идёт по ней** (владелец): угасание при приближении к цели отключено
  (`routeHintFloorApproachMeters` = 0, `RouteHintRevealTiming.ApproachMeters` по умолчанию 0). Уходит только
  при прогрессе (`DecideRouteHintProgressUseCase`: комната критического пути дальше посещённых или
  задание цели; таймер простоя сбрасывается там же) и при занятости интерфейсом; боковая комната не прогресс —
  путь перепланируется от ученика.
- **Баг: подсказка не появлялась, когда ученик стоит в дверном проёме.** Проём лежит на несколько дециметров за
  краем пола комнаты, и прежний планировщик отвечал `Unreachable` без конца (в логе — `idle=2348/60`,
  `planned=False`, `path=Unreachable`). Теперь старт снапится на ближайший пол раскрытой комнаты
  (`StartSnapMeters` 1,2 м), а ученик без свободного первого шага стартует с точки на кольце 0,4–1,6 м.
  Отказ объясняется одной строкой на смену причины (`Route hint unavailable: …; cause: …`,
  `PlanRouteHintPathUseCase.Diagnose`) и `cause=` в `RouteHintDiagnosticText`; периодическая строка
  состояния при `PathNotPlanned` больше не повторяет ту же причину.
- **Звёзды (владелец).** Золотисто-жёлтый цвет (2,2/1,65/0,12; материал и SO), средний дрейф 0,5 м/с, размеры от
  0,5× до 2× базового с перекосом к мелким (`SizeSkew`), доля 20% звёзд мерцает долго и глубоко (период 3,5 с),
  остальные быстро со спредом частоты, у каждой своя качка вдоль пути и вбок; все значения — в SO
  (`routeHintFloorSparkleSizeMinScale/MaxScale/Skew`, `…TwinkleHertzSpread`, `…LongTwinkle*`, `…AlongWobbleMeters`,
  `…LateralWobbleMeters`, `…WobbleSeconds`, `…WallMarginMeters`). Поле `routeHintFloorSparkleSizeVariation`
  заменено диапазоном масштабов.
- Тесты: `PlanRouteHintPathClearanceTests` (баг проёма, закрытая дверь, отступ и центр проёма, деградация в узкой
  комнате, диагностика), `RouteHintTrailTests` (линия, начало впереди, порог и троттл перепланирования),
  `RouteHintStarFieldTests` (разброс у стен, размеры, мерцание, дрейф, привязка к пути),
  `LevelMinimapRouteHintTrailWiringTests` (общие точки пола и миникарты, троттл, подсказка живёт до прогресса,
  боковая комната), `LevelMinimapRouteHintSettingsTests` (значения SO), дополнены тесты вьюшки и порядка показа.

## 2026-10-16 change(route-hint): золотая цепочка звёзд вместо стрелок и полосы

- По уточнённому референсу Genshin убраны шевроны, сплошная лента и подложка пола. Главный элемент —
  мелкие аддитивные четырёхлучевые звёздочки из готовой `Epic Toon FX/Textures/sparkle2.png`.
- По уточнению владельца это густой поток: 10 искр/м (шаг 0,1 м), ширина 0,4 м, высота над найденным
  полом 0,05–0,3 м, разные размеры и фазы мерцания. Скорость — 2,5 м/с по прежней осевой линии к цели;
  у цели искры гаснут. Все параметры — в SO, включая независимый разброс размеров.
- Бюджет по умолчанию — 400 частиц, защитный предел — 512; на длинном пути плотность снижается.
  WebGL: один ParticleSystem, один материал, до 400 билбордов/800 треугольников, без allocations в кадре.
- Shuriken рисует из переиспользуемого массива без выделений памяти в кадре; движение, проявление и
  мерцание используют общий scaled clock. Прежние порядок показа и unscaled-скрытие под UI сохранены,
  пунктир миникарты не изменён. При скрытии все частицы очищаются, включая паузу.
- Обновлены проверки вида, поставки, проводки и PlayMode: частицы следуют поворотам пути, бюджет
  соблюдён, полосы нет, пауза и занятость интерфейсом сохраняют прежний контракт.

## 2026-10-15 fix(route-hint): скрытие интерфейсом завершается на паузе

- При `visible=false` (включая `Occupied`) лента и пунктир гаснут по unscaled time за существующую
  настройку `routeHintFloorHideFadeSeconds` (теперь 0,18 с); по завершении контроллер вызывает `Hide()`.
  Открытая панель больше не оставляет подсказку и замороженные частицы до снятия паузы.
- Проявление, угасание при подходе к цели, шевроны, эмиссия и симуляция частиц сохраняют scaled time.
  Новый контроллерный тест воспроизводит `FloorHolding` → `Occupied` при `timeScale=0`;
  PlayMode ожидание скрытия обновлено на независимость от паузы.


## 2026-10-15 fix(interactive-content): отсутствие требуемого шаблона видно при генерации

- Добавлена способность правила `RequiresTemplate`: обязательный контент и optional-контент такого вида
  останавливают генерацию без совместимой комнаты с явным сообщением (content id, kind, tileset).
  Общий конвейер не знает конкретных видов.

## 2026-10-15 fix(route-hint): проекция игрока, общий игровой таймер и проверка пола

- Маркер игрока и безопасное живое начало маршрута большой карты используют мировую позицию игрока
  в проекции комнат; компактная карта сохраняет прежнее центрирование на игроке.
- Проявление, угасание, шевроны (`_AnimationTime`), эмиссия и Shuriken используют игровое время.
  `Hide()` и завершённое угасание очищают звёздочки, включая дочерние системы, даже при `timeScale = 0`.
- Тесты проводки используют internal-границы вместо доступа reflection к приватным членам.
  Страж комнат проверяет пол производственным зондом: включённая стена не компенсирует исключённый пол,
  выключенный родитель не считается активной поверхностью.


## 2026-10-14 fix(route-hint): прогресс только по пути, лента держится весь срок, большая карта вписывает показанное

- Прогресс решает новый `DecideRouteHintProgressUseCase` (исход `RouteHintProgress`:
  `NoProgress`/`ProgressAdvanced`/`RouteComplete`, без состояния): вход в комнату критического пути
  дальше всех посещённых, выполнение задания цели (тот самый якорь `IRoomTaskAnchor` сообщает
  `IsTaskCompleted`; выключенная панель завершением не считается) или завершение маршрута.
  Прогресс начинает отсчёт заново, и подсказка прячется до нового простоя.
- Открытие двери таймер простоя больше не трогает (`NotifyDoorOpened` только раскрывает комнату);
  боковая комната, тупик, ветка неверного ответа и повторный визит (`MarkRoomVisited`) таймер не
  сбрасывают и показанную подсказку не гасят — путь перепланируется с новой позиции. Строка
  `Route hint idle timer restarted (…)` теперь только про прогресс.
- Лента на полу держится весь срок подсказки, как пунктир на миникарте: терминального угасания по
  времени больше нет — `HoldTimedOut`, `Timing.HoldSeconds` и настройка `routeHintFloorHoldSeconds`
  (поле, свойство и строка ассета) удалены; гаснет на подходе к цели, прогрессе и скрытии подсказки.
  Начало каждого показа пишет
  `Route hint floor ribbon shown: points=… verts=… tris=… length=… center=… player=…`.
  Проявление, угасание, шевроны, эмиссия и симуляция звёздочек идут по игровому времени; пауза их замораживает.
- Большая карта вписывает показанный набор комнат с полями (`TryCalculateFullMapFit`,
  `FullMapFitMarginMeters`), с учётом поворота карты, и центрируется на нём, а не на игроке; мелкую
  карту кламп держит в `FullMapMaxZoomVsCompact` от масштаба угловой. Угловая карта без изменений.
  `Full map first draw` дописывает `fit=center(…) size=… top=<комната>:<дальность>,…` — видно, что
  раздуло охват.
- Тесты: `DecideRouteHintProgressUseCaseTests`, `LevelRouteHintSessionProgressTests`,
  `LevelMinimapFullMapFitTests` (без reflection: проекция и масштаб — internal), guard
  `RouteHintFloorLayersGuardTests` (у каждого префаба комнаты есть коллайдер пола на слоях зонда),
  PlayMode `RouteHintFloorRenderPlayModeTests` (форсированный хинт →
  `FloorHolding`: рендерер включён, `_Alpha/_Reveal ≈ 1`, меш у игрока); обновлены ожидания
  `RouteHintRevealTimelineTests` (холд без таймаута), `LevelRouteHintTests` (сайд-комната),
  `LevelMinimapSessionTests` (фит широкой карты), `LevelMinimapControllerWiringTests` (дверь и якорь).

## 2026-10-15 fix(walls): толщина следа стены — настоящая, а не минимум стен столкновений

- `ExtractFootprints` берёт толщину с полом 1 см (`MinFootprintThicknessMeters`), а не с 8 см минимума
  `MinWallWidthMeters`: плоскость-декаль, стоящая на стене, не превращается в 8-см плиту и не вырезает участок над
  собой в пролёте, на котором лежит. `Extract` (стены столкновений и миникарта) не менялся — там минимум остаётся.
- Новый `RoomWallSegmentExtractorFootprintTests`: yaw 0/37/90/180, неравномерный и отрицательный масштаб, повёрнутый и
  сдвинутый корень комнаты, рендерер без `MeshFilter` — следы совпадают со стенами столкновений; наклонённая плоскость
  даёт проекцию на пол (глубина 10·sin 45°), плоская декаль — глубину меньше 2 см и не рубит пролёт.

## 2026-10-14 feat(walls): `RoomWallSegmentExtractor.ExtractFootprints` — настоящий след стены на полу

- Новый `ExtractFootprints(RoomTemplate)`: те же рендереры, что у `Extract` (корень стен миникарты, фильтр имён), но
  длина и толщина каждого меша — по его ориентированному боксу в осях комнаты: длина вдоль оси бокса, самой длинной
  на полу, толщина — поперёк. Для меша, повёрнутого только вокруг вертикали, результат совпадает с `Extract`.
- `Extract` не менялся (переехал в общий `ExtractSegments`). Его дефект описан в xml-doc: толщина берётся по
  собственным осям X/Z меша, поэтому плоскость, стоящая на стене на боку, становится плитой толщиной в свою высоту
  (плоскости `Redo_wall_*` плитки `SM_Bld_Wall_05` в туннелях SciFiAcademy — до 2,2 м), а труба вдоль стены —
  коротким отрезком поперёк. Замер 2026-10-14: такие меши есть во всех 18 префабах комнат. Перевод генерации и
  миникарты на следы меняет расстановку комнат для сохранённых сидов — решение владельца.

## 2026-10-14 fix(interactive-rooms): вид контента — точно, вид комнаты — один раз на шаблон

- `IInteractiveRoomKindRules.TryResolve` разбирает вид контента только по каноническому идентификатору; старые
  написания и вывод по модулю — только для комнат-префабов в новом `TryResolveRoom` (заменил
  `TryResolveModuleName`), в прежнем порядке `quizbranch → lever → password` (`IInteractiveRoomKindRule.LegacyMatchPriority`,
  `MatchesKind` → `MatchesLegacyKind`, параметр `kindTokens` → `legacyKindTokens`).
- Вывод по модулю снова учитывает тип модуля, а не только имя ассета:
  `IInteractiveRoomTemplateDescriptor.InteractiveModuleHint` (`RoomTemplate`: «тип имя», снимок копирует).
- Разрешённое правило комнаты кэшируется по `RoomId` (со сверкой объявленного вида и подсказки модуля): селектор и
  расстановщик больше не нормализуют строку на каждый вызов.
- `InteractiveRoomKindAttribute` на `RoomTemplate.interactiveRoomKind`: хост рисует поле как выбор своих видов.

## 2026-10-14 refactor(interactive-rooms): виды интерактивных комнат — правила хоста, а не перечисления пакета

- Удалены `InteractiveRoomTemplateKind`, `InteractiveMissionContentKinds` и V2-перечисление `InteractiveKind`:
  пакет больше не знает конкретных видов. Вид — строка (`IInteractiveRoomTemplateDescriptor.InteractiveKind`,
  `PlannedInteractiveContent.InteractiveKind`); что он значит, говорит `IInteractiveRoomKindRules` хоста
  (`IInteractiveRoomKindRule`, готовое `InteractiveRoomKindRule`, `InteractiveRoomGenerationHints`).
- `MissionGenerator`, `MissionGraphBuilder`, `RoomSelector`, `SocketBacktrackingRoomPlacer`,
  `DoorFrontierRoomPlacer`, `CreateInteractiveContentConfig` получают правила параметром; `V2RoomSelectionAdapter`
  требует селектор явно. Ветки по `"mazebranch"`/`"passworddoor"` и шаблонам комнат (`Room_Door4_Int`,
  `PlusTonnel_3Doors`, `Toonel`) переехали в подсказки правил хоста — поведение генерации прежнее.
- `RoomTemplate.interactiveRoomKind` — строка вида (было перечисление); сохранённые значения префабов
  переведены (`1/2/3` → `MazeBranch/LeverPanel/PasswordDoor`, `0` → пусто). Вид по имени модуля выводит правило
  хоста (`MatchesModuleName`).
- Контент неизвестного вида `CreateInteractiveContentConfig` пропускает с явным предупреждением.

## 2026-10-14 feat(minimap): путь подсказки на полу; таймер подсказки в WebGL

- **Причина «пунктир не появляется в WebGL» (Demo b332, план b333 §2a; подтвердить логом WebGL-билда).**
  Таймер простоя шёл только при `GameplayUiInputMode.IsPlayerInControl`. В WebGL после закрытия экрана
  загрузки, любой панели или возврата фокуса курсор виден, а взгляд выключен, пока браузер не подтвердит
  pointer lock по жесту игрока (`WebGlPointerLockGate`). Ученик, который стоит и ничего не нажимает, жеста не
  делает — таймер стоял вечно, хотя никакая панель его не держала; в редакторе захват мгновенный, поэтому там
  всё работало. Теперь «ученик в мире или занят UI» решает `RouteHintPresenceUseCase` по ПОЛНОМУ набору фактов
  `GameplayUiInputMode.ControlState` (`PlayerControlState`), а не по первому блокеру: видимый курсор и
  выключенный взгляд (рукопожатие с браузером) — в мире; UI-лок, захват клавиатуры, поле ввода, свободная мышь,
  замороженный контроллер или выключенное движение — занят. Тесты — `RouteHintPresenceUseCaseTests`, PlayMode
  `LoadingScreenClose_WebGlWithoutBrowserGesture_RouteHintDashesAppear` (пунктир появляется без жеста).
- Большая карта (`M`) рисуется только пока ученик в мире: IMGUI лежит поверх любой панели UI Toolkit, и
  открытая карта закрывала собой чат, код-станцию или вопрос голубой рамкой посреди экрана. Открытая карта
  прячется на время панели или свободной мыши и возвращается с управлением; `M` в это время не действует.
  `IsFullMapVisible`; кадр карты без устройств ввода — `TickFrame` (тесты
  `LevelMinimapRouteHintFloorWiringTests`).
- **Пустая большая карта — причина не найдена**, §2a открыт до лога WebGL-билда. Добавлена строка
  `Full map first draw: geometry=… textureCreated=… rooms=… projection=…`, которая называет первую причину
  пустоты: слишком маленький прямоугольник, нет GL-материала (стены пустые), текстура кэша не создана, нет
  раскрытых комнат, не легла проекция. Отсутствие шейдера `Hidden/Internal-Colored` или отказ `SetPass` — одно
  предупреждение вместо исключения в каждом `OnGUI` (раньше такое исключение опустошало и угловую карту).
- Диагностика для WebGL-сборки: одна строка `Route hint state changed: …` на смену состояния (presence,
  исход решения, путь проложен, фаза пути на полу, большая карта, устойчивая невозможность начать пунктир от
  игрока), строка на сброс таймера простоя (новая комната / новая дверь), строки об открытии большой карты
  и её первой отрисовке, одна строка на причину, по которой нет ленты на полу (нет префаба, нет материала,
  вырожденная лента, слишком сложный путь, путь не готов).
- **Путь на полу (план b333 §2b).** Префаб `RouteHintFloorView` и его материалы (`RouteHintFloorRibbon.mat` на
  шейдере ленты, `RouteHintSparkles.mat` у звёздочек) собраны в редакторе в `Assets/_source/Prefabs/RouteHint/`,
  префаб назначен в `Resources/Minimap/LevelMinimapSettings.asset` (`routeHintFloorViewPrefab`); страж поставки —
  `RouteHintFloorPrefabGuardTests`. Без назначенного префаба лента не появляется, а пунктир на миникарте
  включается сразу, как раньше. Устройство: `RouteHintFloorView` только рисует светящуюся ленту по той же полилинии, что и пунктир
  (`PlanRouteHintPathUseCase`), со звёздочками Shuriken (бюджет частиц из настроек, без VFX Graph) и
  URP-шейдером `RedoSchool/LevelGeneration/RouteHintFloorRibbon` (шевроны к цели, мягкие края, фронт
  проявления, HDR-свечение). Геометрию строит чистый `RouteHintRibbonMeshBuilder`: скругление углов не
  отходит от точек пути дальше `routeHintFloorCornerMaxDeviationMeters`; разворот назад (круче ~164°) не
  скругляется и всегда получает шов без перекрута и сужения — один порог на оба решения; точки с
  NaN/бесконечностью отбрасываются; при нехватке бюджета точек укрупняется шаг только прямых участков, а точки
  углов (вход, вершина, выход скругления) сохраняются всегда — хвост не обрезается, поворот не срезается хордой
  через стену; жёсткий потолок `HardMaxSamples` (4·`MaxSamples`) — дальше ленты нет
  (`RouteHintRibbonFailure.TooComplex`), меш всегда в пределах 16-битного индекса; шаги прямого участка
  ограничены, счёт в `long` — гигантские отрезки не переполняют его. Высоту точки даёт луч вниз (`Runtime/Infrastructure/RouteHintFloorRaycastProbe`, мимо
  триггеров и игрока, попадания по расстоянию, при заполненном буфере — `RaycastAll`, с высоты над ПРЕДЫДУЩЕЙ
  точкой), решение —
  `ResolveRouteHintFloorHeightUseCase`: перепад больше `routeHintFloorMaxStepMeters` от предыдущей принятой
  точки — декор. Первый отрезок от живой позиции игрока проверяется той же проверкой стен, что у пунктира.
  Порядок показа — `DecideRouteHintRevealUseCase` (состояние — `RouteHintRevealTimeline`): лента проявляется
  → держится, пока ученик не прошёл `routeHintFloorApproachMeters` к цели или не вышло
  `routeHintFloorHoldSeconds` → гаснет; пунктир на миникарте включается по окончании проявления. Пустой путь —
  повтор на следующем кадре, а не отказ; выключенная миникарта (`OnDisable`, `SetOverlayVisible(false)`)
  гасит ленту. Тесты — `RouteHintRibbonMeshBuilderTests`, `RouteHintRevealTimelineTests`,
  `RouteHintFloorViewTests`, `LevelMinimapRouteHintFloorWiringTests`.

## 2026-10-14 feat(doors): событие видимого движения двери

- `DoorSocket.VisualOpenChanged(door, opened)` — дверь на глазах игрока открылась или закрылась: auto-open
  триггер (игрок подошёл / отошёл) или смена состояния логикой (`ApplyState`). Только настоящая смена видимого
  положения; генерация (`ApplyGeneratedState`, `Awake`) молчит. `DoorSocket.IsVisuallyOpen`.
- `DoorSocket.RestoreState(state)` — то же, что `ApplyState`, но молча: для восстановления чекпоинта (иначе
  возобновление «открыло» бы разом все пройденные двери). Первый потребитель — звук дверей игры (план b333 §7b).
  Тесты — `DoorSocketVisualOpenChangedTests`.

## 2026-10-14 fix(minimap): комната за открытой дверью видна на карте

- Открытая перед игроком дверь снова раскрывает на картах комнату за ней — через `roomRevealDelaySeconds`
  (1 с) и той же анимацией проявления (владелец: «при открытой двери комната не отрисовывается, только когда
  полностью зашёл»). Посещённой для подсказки маршрута такая комната не становится, текущая комната не
  меняется. Ожидающее раскрытие в checkpoint не попадает. Тест —
  `LevelMinimapControllerWiringTests.DoorOpenedForPlayer_InSpawnedRoom_RevealsTheRoomBehind_WithoutVisitingIt`.

## 2026-09-27 fix(minimap): подсказка после простоя и безопасный путь

- Карта раскрывает комнату только при входе. Первое открытие двери новой комнаты и вход в новую
  комнату начинают 60-секундный отсчёт заново; повторный сигнал двери таймер не сбрасывает.
  Resume восстанавливает подтверждённо посещённые комнаты, не раскрывая старые превью за дверями.
- Планировщик учитывает внутренние стены, непрерывный контур пола и запертые транзитные двери.
  Подтверждённый сокет связи формирует проём, даже если bounds декоративной стены захватил дверь.
  Статический граф обхода перегородок и пути между дверями кэшируются на уровень. Настенная панель
  незавершённой комнаты получает ближайшую достижимую точку на полу; завершённая панель уступает
  цель двери следующей комнаты. При недостижимой цели и при опасном первом отрезке
  линия не рисуется. При движении уже пройденные углы отбрасываются; блокировка транзитной двери
  инвалидирует кэшированный путь, а недостижимая цель повторно проверяется не чаще раза в 2 секунды.

## 2026-09-24 feat(minimap): раскрытие комнаты по открытию двери и подсказка маршрута

- `DoorSocket.OpenedForPlayer` — событие «дверь открылась перед игроком» (auto-open триггер открыл
  дверь). `ApplyState` его не поднимает, поэтому восстановление чекпоинта не «открывает» все двери.
- `LevelMinimapSession`: отложенное раскрытие (`ScheduleReveal` → `RevealDueRooms(now)`; текущую
  комнату не меняет), `FindRoomBehindDoor`, анимация раскрытия (`GetRevealProgress`,
  `IsRevealAnimating`, `PruneFinishedRevealAnimations`), `RestoreRevealedRooms` (ожидающие
  раскрываются сразу), `GetPersistableRevealedRoomIds` (раскрытые + ожидающие), `RevealVersion`.
  `RevealRoom(nodeId, animationStart)`: при ожидающем раскрытии только делает комнату текущей и
  сохраняет срок. Перепривязка того же уровня переносит ожидающие сроки. `GetVisibleRooms()`
  пересобирается только при раскрытии или смене текущей комнаты.
- Подсказка маршрута (Application): `LevelCriticalPath` (путь старт → выход по
  `DoorConnection.IsMainPath`, запасной — по всем связям), `DecideRouteHintUseCase` (цель — дверь в
  следующую неоткрытую комнату пути, а при запертой логикой комнаты двери — задание комнаты,
  `RouteHintTargetKind.RoomTask`; исходы `NoCriticalPath` / `RouteComplete` / `Waiting` / `ShowHint`),
  `LevelRouteHintSession` (открытые комнаты, таймер простоя, принудительный показ, проба запертости
  двери), `RouteHintGeometry` + `PlanRouteHintPathUseCase` (`Execute` к двери, `ExecuteToRoomTask`
  к заданию; линия по раскрытым комнатам и полу комнат).
- `IRoomTaskAnchor` — точка задания комнаты для подсказки (в RedoSchool её реализует настенная панель
  вопроса).
- `LevelMinimapController` стал `partial`: `LevelMinimapController.RouteHint.cs` подписывается на
  двери комнат (без дублей при перепривязке, уничтоженный контроллер снимает подписку сам), тикает
  таймер (только при `GameplayUiInputMode.IsPlayerInControl`; без управления подсказка гаснет) и
  рисует пунктир или линию одинаково на компактной и большой карте. Проявляющиеся комнаты и их связи
  рисуются живым слоем поверх кэша и запекаются в кэш один раз по окончании анимации. Все живые
  слои (проявление, подсказка, маркер выхода, колбы) делят одну проекцию на кадр и рисуются только
  на `Repaint`. Кант точки пунктира запечён в её текстуру, точек не больше 160 на карту.
  Невозможность показать подсказку — одно `RedoLog.Warn` на смену исхода (сборка ссылается на
  `RedoSchool.Platform.Shared.Logging`). Новые публичные члены: `Settings`, `RouteHint`,
  `RouteHintAlpha`, `RouteHintWorldPath`, `LastRouteHintPathOutcome`, `NotifyDoorOpened`,
  `SetRouteHintForced`.
- Настройки — ScriptableObject `LevelMinimapSettings` (`Resources/Minimap/LevelMinimapSettings`,
  меню `Neoxider/LevelGeneration/Minimap Settings`); без ассета действуют те же значения по умолчанию.

## 2026-08-19 feat(minimap): карточка по макету HUD и публичный `MinimapRect`

- `LevelMinimapController.minimapRect` по умолчанию — `24,24,230,178` вместо `16,16,220,160`:
  положение и пропорция карточки с макета HUD RedoSchool. Authored-экземпляра компонента в проекте нет
  (рантайм создаёт его сам в `LevelSpawner`), поэтому значение по умолчанию и есть production-значение.
  Меньшая сторона выбрана внутри коридора, который держит `LevelMinimapFramingTests`: от неё считается
  зум, а от зума — помещается ли маркер игрока в ячейку сетки и виден ли он на её фоне.
- Добавлено публичное read-only свойство `MinimapRect`. Карта — `OnGUI` в экранных пикселях, а соседний
  HUD рисуется UI Toolkit со своим масштабированием, поэтому «где кончается карта» ему нужно измерять,
  а не хранить константой, верной на одном разрешении.

## 2026-06-24 fix(minimap): кэш статического слоя карты

- `LevelMinimapController` больше не перерисовывает весь граф мини-карты в каждом `OnGUI`-кадре: статический слой комнат, связей и дверей кэшируется в `RenderTexture` отдельно для compact/full map.
- В обычном кадре `OnGUI` делает один `GUI.DrawTexture` для готового слоя и поверх рисует только маркер игрока; кэш инвалидируется при `Bind`, `RoomEnteredEvent`, смене режима/размера/настроек отображения.
- Добавлен счётчик `StaticMapRebuildCount` и EditMode-покрытие, фиксирующее, что повторный кадр без изменения карты не увеличивает число перестроений.

## 2026-06-01 docs

- Обновлены package README/API/PACKAGE docs на русском под текущую embedded-library boundary.
- Зафиксировано, что Layout Pacing V2 является текущим generator path, а V1 остается rollback.
- Уточнено, что RedoSchool JSON parsing, rewards, cheats, MissionMap glue и SciFiAcademy content остаются вне reusable package.
## 3.0.19

- Runtime spawns now create fallback `DoorAutoOpenTrigger` components for usable runtime-only doors with `DoorSocket.Flow Direction = None`, so closed/open/exit-gate doors can auto-open even when the generator ignores them for room docking.
- Door docking center now stays bound to the authored physical door center (`DoorSocket` bounds / `snapAnchor`); wall-derived geometry may refine yaw/frame width, but cannot move the connection point away from the visible doorway.
- Runtime spawns now auto-create/bind a `LevelMinimapController` when no minimap exists under the generated level root, so `M` can open the full map without requiring the Cheats overlay first.
- Adaptive room loading is now opt-in; generated rooms stay active from mission start by default while the previous active-window loader remains available for explicit performance testing.
- Added isolated `Generation/Application/V2` Layout Pacing core: pacing config/domain, content plan, graph planner, quiz answer tunnel expansion, branch planner, retry policy, diagnostics, selection adapter and placement candidate memory.
- `MissionGenerator` can now use Layout Pacing V2 behind `ILayoutPacingConfigProvider` while keeping the V1 graph builder as rollback path.
- In Layout Pacing mode, `TotalRoomsRange` is treated as legacy/cap data for validation instead of an exact room target.
- Added V2 EditMode coverage for defaults, biased branch sampling, mandatory/optional content split, main path gaps, answer tunnels, retry stages, deterministic known seeds, selection adapter, placement memory and V1/V2 runtime switching.
- Socket backtracking placement now rejects any candidate where a `Locked` door becomes the incoming/start-side connection; this applies to PasswordDoor, MazeBranch, LeverPanel and future locked-door rooms.
- Added a RedoSchool command-line SciFiAcademy smoke check that validates known seeds, mandatory interactive count, skipped-warning absence, locked-door orientation and door-center docking.
- QuizBranch room modules no longer publish the global `QuizRequestedEvent` on room bind; authored `option_N` + `submit` room triggers drive the branch locally.
- Level Preview and Hub mission start now build `MissionGenerator` through the same `LevelGenerationPipelineFactory`, so preview tracks runtime pipeline changes automatically.
- SciFiAcademy interactive room prefabs now have in-room UI panels and module fallback assets for prefab-only designer checks across MazeBranch, LeverPanel and PasswordDoor.
- Planned JSON interactive content remains mandatory while optional branching is bounded to avoid oversized optional graphs and placement timeouts.

## 3.0.18

- Restored one production door-frame source of truth across manual grid snapping, Level Preview and runtime generation.
- `DoorConnector` now disables every unused socket after graph connections are applied, including unused `Locked` answer doors, so interactive rooms such as `Room_Door4_Int` no longer leave answer exits open when they are not connected.
- SciFiAcademy procedural generation now inserts a small controlled budget of ordinary passage spacers into planned side-branch content, reducing back-to-back interactive room repetition while keeping known-seed placement within the smoke-test timeout.
- Added strict interactive room selection tests so mandatory content does not silently fall back to a generic incompatible interactive prefab.
- Level Preview now reuses cached room database/catalog/hints and bounded generation attempts to keep editor previews responsive while matching the runtime factory path.
- Fixed normal EditMode Test Runner hangs by isolating geometry smoke tests from JSON interactive-content expansion; content-specific tests still exercise planned interactive rooms separately.
- Interactive content graph injection now fills interactive spine slots first and inserts remaining planned content into the main path, avoiding oversized optional-only graphs.
- Added `LevelGenerationDebugCounters` and surfaced generation diagnostics in runtime mission logs, failure logs and Level Preview status.
- Room selection now biases interactive rooms with matching tool/content metadata and passage rooms with enough usable doors for branching.
- Added cancellable `MissionGenerator.BuildLayoutAsync(...)` with worker-thread generation snapshots, plus opt-in `DiagnosticLogsEnabled` / `DiagnosticLogSink` analysis logs.
- Generation now rejects partial placements before door connection and validates graph node/edge coverage through `IGraphAwareLevelValidator`.
- Door connection is graph-only by default; invalid precomputed pairs are dropped and incidental snapped socket pairs require explicit `new DoorConnector(connectIncidentalDoorContacts: true)`.
- Removed the direct runtime asmdef dependency on Unity Input System; minimap hotkeys now use optional reflection/fallback input handling.
- Auto-open door visuals with `DoorAutoOpenTrigger` now start closed until triggered, including exit gates.
- Reduced expensive generation retry loops in failing tests and made visual smoke screenshot tests explicit to keep regular EditMode runs responsive.
- Removed legacy `RoomType`, `RoomCategory`, `RoomTopologyKind` and topology quota APIs from generator-facing contracts and tests.
- `RoomTemplate` now resolves `RoomKind` from current authoring fields for existing prefabs that have not serialized the new field yet.
- SciFi Academy procedural mission content now uses the full Python questions JSON and marks door-choice room prefabs as `Interactive` / `MazeBranch`.
- `SocketBacktrackingRoomPlacer` reuses transformed candidate wall geometry during overlap checks, reducing known-seed visual smoke generation from `1617 ms` to `1397 ms` under the `1500 ms` budget.
- Production runtime and Level Preview now share the fast `RoomPlacer` path with async timeout and debug counters.
- SciFiAcademy interactive generation now treats compatible JSON rooms as required, supports PasswordDoor, MazeBranch and LeverPanel room kinds, and skips only unsupported items with warnings.
- Password wall-panel UI text is valid UTF-8 and reports game-style feedback for empty, wrong and accepted codes.
- Verified `MissionConfig_SciFiAcademy_Procedural_Tunnels_01` from Hub with seed `11`: 42 rooms, 28 interactive modules, no generation exception.
- Updated RedoSchool level-designer docs for room count semantics, fallback questions, locked target-door contract and preview/runtime parity.

## 3.0.17

- Moved reusable package to Assets/ThirdParty/Neoxider/LevelGeneration.
- Split RedoSchool-specific room gameplay, mission runtime bridge, SciFiAcademy resources and level-designer docs into Assets/_source/Features/RedoSchoolLevelGeneration.
- Added RedoSchool.LevelGeneration.Unity and RedoSchool.LevelGeneration.Runtime asmdefs for Unity-facing library code.

- Added `RedoSchool.LevelGeneration.Domain` and `RedoSchool.LevelGeneration` runtime asmdefs for generator domain/application code.
- Added separate `RedoSchool.LevelGeneration.Editor` asmdef for LevelGeneration editor tooling.
- Updated project/test assembly references so runtime integration can keep using generator core without keeping it in `RedoSchool.Source`.
- Expanded API documentation for assembly ownership, mission JSON content and room gameplay integration boundaries.
- Fixed level-designer guide encoding issues so RoomPools and InteractiveRooms docs are readable in UTF-8.
- Removed `UniTask` and `MessagePipe` references from the LevelGeneration core asmdef; the host game now owns async scheduling and event publication around `MissionGenerator.BuildLayout(...)`.

## 3.0.16

- Added a dedicated room-logic integration guide and a custom interactive-room demo.
- Moved LevelGeneration generator/editor tests and minimap runtime tests into the package test folder.
- Added a package-local EditMode test assembly for generator/minimap coverage.

## 3.0.15

- Added package identity metadata for `com.neoxider.level-generation`.
- Documented the reusable LevelGeneration library boundary separately from optional room gameplay modules.
- Documented the public generator/minimap API and the future asmdef extraction path.
- Clipped runtime minimap wall/door drawing to the minimap viewport to prevent wall geometry from bleeding outside the minimap panel.

## 3.0.14

- Door docking uses resolved door frames from wall geometry.
- Level Preview renders wall geometry and door-frame labels instead of occupied-cell/bounds rectangles.
- Placement and validation use wall collision; `RoomBounds` is broad/editor metadata only.
- Generation placement runs through `SocketBacktrackingRoomPlacer`; Taurus was removed.
