# Neoxider LevelGeneration API

Документ описывает публичную поверхность reusable библиотеки. Разбор контента игры, награды, читы и gameplay-модули комнат живут в игре-хозяине, вне границы пакета.

## Pure C# generator

**Роли шаблонов (`RoomTemplateRoles`).** Комнаты, которые генератор вставляет сам — обычный проход (спейсеры,
туннели ответов лабиринта), развилка для цепочки ветки и тупик неверного ответа, — он берёт по ролям, которые
называет хозяин: `new RoomTemplateRoles(passageId, branchHubId, answerDeadEndId)`. Роли передаются ДВАЖДЫ и
одинаково: в `MissionGraphBuilder` (путь V1) и в `MissionGenerator` (путь Layout Pacing V2); тем же значением — в
`V2RoomSelectionAdapter` и `PacingSocketPlacementRunner`, если хозяин зовёт их напрямую. Без ролей
(`RoomTemplateRoles.None`) подходит любой совместимый шаблон: тайлсету, у которого туннели ответов обязаны совпадать
по семейству дверей с комнатой лабиринта, роли нужно передать, иначе ответвления не состыкуются.

Основной facade:

```csharp
MissionGenerator.BuildLayout(IMissionConfigView config, int seed, CancellationToken ct = default);
MissionGenerator.BuildLayoutAsync(IMissionConfigView config, int seed, CancellationToken ct = default);
```

`BuildLayoutAsync` snapshot-ит Unity-backed config/tileset/rooms на caller thread, затем запускает generator pipeline на worker thread с cooperative cancellation.

`LevelGenerationPackageInfo.LayoutContract` — версия раскладки по сиду: меняется, только когда тот же сид и та же
конфигурация дают другую раскладку (4.1.0 → `2`). Хозяин, сохраняющий прогресс миссии по сиду, хранит это значение
вместе с сохранением и начинает прогон заново, если оно отличается.

## Interactive room kinds

Пакет не знает ни одного конкретного вида интерактивной комнаты. Хост передаёт правила своих типов как
`IInteractiveRoomKindRules` (у игры — её реестр типов комнат) в `MissionGenerator`, `MissionGraphBuilder`,
`RoomSelector`, `SocketBacktrackingRoomPlacer` и `MissionGenerator.CreateInteractiveContentConfig`; параметр
обязательный, `InteractiveRoomKindRules.Empty` — «интерактивных типов нет».

- `IInteractiveRoomKindRule` — вид (`Kind`), раскладка ответов `InteractiveRoomAnswerLayout`
  (`DoorPerOption`: каждый вариант — запертая дверь, неверные ведут в тупики; `LockedExit`: решение открывает
  выход), совместимость контента с комнатой (`SupportsContent`, `IsCompatible`), подсказки размещения
  `InteractiveRoomGenerationHints`, разбор устаревших написаний вида комнаты (`MatchesLegacyKind`) и
  типа/имени модуля (`MatchesModuleName`) в порядке `LegacyMatchPriority`.
- `InteractiveRoomKindRule` — готовое правило для вопросных типов (раскладка + минимум верных ответов +
  `legacyKindTokens` / `moduleNameTokens` / `legacyMatchPriority` для старых префабов).
- `IInteractiveRoomKindRules.TryResolve` — вид КОНТЕНТА: только канонический идентификатор (без учёта регистра и
  пробелов по краям), без старых написаний. `TryResolveRoom` — вид КОМНАТЫ-префаба: объявленный вид, затем старые
  написания, затем тип и имя модуля (`IInteractiveRoomTemplateDescriptor.InteractiveModuleHint`, по умолчанию
  `ModuleAssetId`); результат кэшируется по `RoomId` (со сверкой входа), селектор и расстановщик не разбирают
  строку на каждый вызов.
- `InteractiveMissionContentMatcher` — только разрешает правило для контента/комнаты и спрашивает его; сам вид не
  сравнивает. `IInteractiveRoomTemplateDescriptor.InteractiveKind` — вид, объявленный на префабе
  (`RoomTemplate.interactiveRoomKind`, строка; в инспекторе хост рисует поле с атрибутом
  `InteractiveRoomKindAttribute` как выбор своих видов).

## Layout Pacing V2

V2 включается через provider на стороне config. Pipeline:

```text
ResolvePlan -> BuildGraph -> SelectRooms -> PlaceRooms -> ConnectDoors -> Validate -> Result + Diagnostics
```

Core concepts:

- `LayoutPacingConfig` - main gaps, branch count/length, bias, optional caps, budget, retry policy.
- `PlannedInteractiveContent` - уже resolved mandatory/optional content; core не парсит JSON игры напрямую.
- `PacingMainPathPlanner` - строит `Start -> ordinary gap -> mandatory interactive -> ... -> Exit`.
- `QuizBranchGraphExpander` - создает answer tunnels для контента с раскладкой `DoorPerOption` (`AnswerBranchPolicy.CorrectContinuesWrongTerminal`).
- `PacingBranchPlanner` - добавляет ordinary/optional side branches по pacing config.
- `LayoutRetryPlanner` - деградирует optional complexity, сохраняя mandatory path.
- `LayoutGenerationDiagnostics` - machine-readable counters, retry stage и door snap diagnostics.

## Unity authoring surface

`Neoxider.LevelGeneration.Unity` содержит:

- `RoomTemplate` - root descriptor prefab комнаты.
- `DoorSocket` - socket id, size, initial state, connectability и visual state.
- `RoomBounds`/wall geometry descriptors - source of truth для overlap/minimap/docking.
- `RoomWallSegmentExtractor.Extract` - стены столкновений и миникарты (`RoomTemplate.WallCollisionSegments`); толщину
  читает по собственным осям X/Z меша, поэтому меш, повёрнутый на бок, получает чужой след (известный дефект, правка
  меняет расстановку для сохранённых сидов). `RoomWallSegmentExtractor.ExtractFootprints` - те же рендереры, след
  ориентированного бокса меша на полу комнаты; им пользуются постеры на стенах.
- `TilesetSO`, `RoomPoolSO`, `RoomDatabase` - reusable data containers.

Door docking source of truth: physical visual/bounds door center from `DoorSocket` bounds / `snapAnchor`, with yaw, size и frame width resolved from wall geometry/fallback metadata. The connection point is a full 3D local point (`ConnectionLocalXMeters`, `ConnectionLocalYMeters`, `ConnectionLocalZMeters`); placement matches connected doors on X/Y/Z. `Disabled`, disabled component и inactive door GameObject исключаются из docking.

## Runtime primitives

`Neoxider.LevelGeneration.Runtime` содержит reusable runtime-поведение:

- `LevelMinimapController` и minimap rendering primitives.
- `RoomInstance` и generated room metadata.
- Room-enter/endpoint/door-transition helper components/events.

Pure C# события комнат, дверей и endpoint вынесены в отдельную assembly
`Neoxider.LevelGeneration.Runtime.Domain` (`noEngineReferences=true`); Unity-компоненты runtime зависят
от неё, а не владеют этими контрактами.

Обе presentation-карты `LevelMinimapController` используют только
`LevelMinimapSession.GetVisibleRooms()` и `GetVisibleConnections()`: большая карта, как и угловая,
не раскрывает комнаты, в которые игрок не входил и двери в которые перед ним не открывались, а также их связи, выход и предметы. `GetAllRooms()` и
`GetAllConnections()` остаются сырым API layout для диагностики и непредставленческих потребителей;
подключать их к карте нельзя, иначе игрок заранее увидит выход и длину маршрута.

Раскрытие по входу и подсказка маршрута:

- `DoorSocket.VisualOpenChanged(door, opened)` — видимое открытие или закрытие двери во время игры (триггер
  или `ApplyState`); генерация и `DoorSocket.RestoreState` (восстановление чекпоинта) его не поднимают.
- `DoorSocket.OpenedForPlayer` — комната за дверью раскрывается на картах через `RoomRevealDelaySeconds`
  (`ScheduleReveal`), не становясь текущей и посещённой и не трогая таймер подсказки: прогресс —
  только вход дальше по пути и задание цели. Текущей комнату делает только `RoomEnteredEvent`.
- `LevelMinimapSession.ScheduleReveal(nodeId, revealAt)` / `RevealDueRooms(now)` /
  `RestoreRevealedRooms(ids, current)` / `GetPersistableRevealedRoomIds()` / `FindRoomBehindDoor`.
- `LevelCriticalPath.Build(layout)` — путь старт → выход по `DoorConnection.IsMainPath`.
- `DecideRouteHintUseCase.Execute(path, visited, idle, threshold, forced)` →
  `RouteHintDecision` (`NoCriticalPath` / `RouteComplete` / `Waiting` / `ShowHint`, целевая дверь).
  Был ли визит прогрессом — `DecideRouteHintProgressUseCase.Execute(path, previousProgress, visited)` →
  `RouteHintProgress` (`NoProgress` / `ProgressAdvanced` / `RouteComplete`): таймер сбрасывают только вход
  дальше по пути, задание цели и финал маршрута.
- `LevelRouteHintSession` — посещённые комнаты, таймер простоя (`Tick(dt, playerInControl)`), чит-показ,
  `MarkRoomVisited` по входу. При resume передаются подтверждённый `visited_room_ids`, завершённые
  комнаты и текущая безопасная; у старого checkpoint без `visited_room_ids` список раскрытых соседей
  игнорируется.
- `RouteHintGeometry` + `PlanRouteHintPathUseCase.Execute(...)` (к двери) /
  `ExecuteToRoomTask(...)` (к доступному полу перед настенным заданием) — линия по раскрытым
  комнатам, без запертых транзитных дверей и пересечения сегментов стен. Стены передаются как
  `RouteHintWall` в `AddRoom`; подтверждённый `AddDoor` оставляет узкий проём в стене. Перегрузки с
  `RouteHintPathOptions(clearanceMeters, doorApproachMeters)` держат путь от поверхности стен не ближе
  отступа (ступенчато уменьшается в узких комнатах, затем прежний поиск), проходят центр проёма по
  перпендикуляру к стене и по возможности идут по середине комнат; `RouteHintPathOptions.Legacy` — прежний
  кратчайший путь. Игрок в проёме (за краем пола не дальше `StartSnapMeters` = 1,2 м) стартует с ближайшей
  точки пола, игрок без свободного первого шага — с точки кольца вокруг (`TryRecoverStart`).
  `PlanRouteHintPathUseCase.Diagnose(...)` → `RouteHintFailure` (`Cause`: `PlayerFarFromFloor` /
  `NoOpenDoorChain` / `PlayerWalledIn` / `RoomGeometryBlocksRoute` / `TargetDoorUnknown` /
  `TargetRoomHidden`, `Describe()`, `Key`) — почему маршрут не проложен, для одной строки лога на смену причины.
  `RouteHintGeometry.MeasureWallClearance(point, cap)` и `TryFindNearestFloor(...)` — свободная ширина и
  ближайший пол.
- `RouteHintTrail` — ОДНА линия подсказки для пола и миникарты: `Points`/`Distances`/`Clearances`, `Revision`
  (растёт только при `Assign` — перепланировании), `HeadDistance`/`HeadPoint` (видимое начало),
  `ProgressMeters`, `RemainingMeters`, `Follow(player, startOffset, minVisible, deviationLimit)` →
  `RouteHintTrailFollow` (`OnTrail`, `DeviationMeters`), `FillVisible(list)` (голова и точки за ней).
  `RouteHintTrailBuilder.Build(geometry, route, smoothingShape, clearanceCap, startOffset, minVisible, trail)` —
  сглаживание углов один раз и замер свободной ширины.
  `DecideRouteHintReplanUseCase.Execute(RouteHintReplanInput)` → `RouteHintReplanReason` (`None` /
  `WorldChanged` / `PlayerLeftTrail` / `NoTrail`): изменение мира перепланирует сразу, уход игрока от линии
  дальше порога — не чаще троттла.
- `IRoomTaskAnchor` — точка задания и `IsTaskCompleted`; незавершённая активная панель получает
  приоритет перед дверью, завершённая уступает цель следующей двери.
- `LevelMinimapController.NotifyDoorOpened`, `SetRouteHintForced`, `RouteHint`, `Settings`
  (`LevelMinimapSettings`, `Resources/Minimap/LevelMinimapSettings`).
- `IMinimapGameplayContext` — что карте нужно от игры: `Presence` → `RouteHintPresence` (`Exploring` /
  `Occupied`: таймер простоя идёт и большая карта рисуется только в мире), `WasFullMapTogglePressed`
  (нажатие клавиши большой карты из ввода игры; молчит, пока клавиатурой владеет UI), `DescribeControl()` (фрагмент `RouteHintDiagnosticText`).
  Подключение — `MinimapGameplay.Context`; без него карта автономна (в мире, клавиатура свободна).
  Журнал — `LevelGenerationRuntimeLog.Info/Warn` с приёмниками `InfoSink`/`WarnSink` (сообщение, файл,
  метод); без приёмника — консоль Unity. Решение о присутствии по фактам ввода живёт в игре
  (решение и мост к карте — на стороне игры). `LevelMinimapController.IsFullMapVisible`; `TickFrame(presence,
  fullMapKeyPressed, deltaTime, unscaledDeltaTime, now, scaledNow = null)` — кадр карты без устройств ввода (internal, для
  EditMode-тестов через `InternalsVisibleTo`). Публичные точки хозяина: `SetSettings(settings)` — свои настройки
  карты (префаб подсказки на полу, префикс предметов `CollectibleMarkerNamePrefix`), `PlayerTransform` — игрок, за
  которым следит карта, `UpdateRouteHint(...)` — шаг подсказки для собственного цикла хозяина.

Путь подсказки на полу (сначала пол, потом миникарта). Префаб цепочки `RouteHintFloorView` (аддитивный материал звёздочек) даёт игра: в ассете пакета `Resources/Minimap/LevelMinimapSettings.asset` поле `RouteHintFloorViewPrefab` пустое, а хозяин подставляет свои настройки через `LevelMinimapController.SetSettings`. Без префаба подсказка на полу не рисуется (предупреждение в журнале), пунктир миникарты остаётся:

- `DecideRouteHintRevealUseCase.Execute(state, input, timing)` / `Evaluate(...)` и
  `RouteHintRevealTimeline.Advance(RouteHintRevealInput, RouteHintRevealTiming)` → `RouteHintRevealFrame`
  (`Phase`, `FloorReveal`, `FloorAlpha`, `MinimapVisible`, `StartedReveal`). Фазы `RouteHintRevealPhase`:
  `Hidden` → `FloorRevealing` → `FloorHolding` → `FloorFading` → `FloorDone`; причина угасания —
  `RouteHintFloorFadeReason`. Без ленты (`FloorAvailable = false`) — сразу `FloorDone`, пунктир виден.
  Ходьба по пути подсказку не гасит: `RouteHintRevealTiming.ApproachMeters` по умолчанию 0 (угасание при
  приближении включается только ненулевым значением).
- `RouteHintRibbonMeshBuilder.Build(polyline, RouteHintRibbonShape, startFloorHeight, RouteHintFloorProbe,
  RouteHintRibbonMeshData)` и `SmoothCorners(...)` — чистое построение ленты: вершины (мировые), `uv0` =
  (поперёк 0…1, метры вдоль), `uv1` = (доля пути 0…1, длина), осевая линия для звёздочек; не больше
  `MaxSamples` точек (при нехватке укрупняется шаг прямых участков, точки углов сохраняются всегда — потолок
  мягкий); жёсткий потолок `HardMaxSamples`, сверх него — `false` и `RouteHintRibbonFailure.TooComplex`
  (перегрузка `Build(..., out RouteHintRibbonFailure)`; `SmoothCorners` возвращает ту же причину); NaN-точки
  отбрасываются; разворот назад (круче ~164°) не скругляется и всегда даёт шов.
  `BuildFromAxis(axis, shape, startFloorHeight, probe, output, out failure)` — то же для готовой оси:
  точки берутся как есть (без скругления и пересэмплирования), добавляется только высота пола; так общая
  линия подсказки попадает на пол без изменений.
- `RouteHintFloorProbe` (делегат луча к полу) + `ResolveRouteHintFloorHeightUseCase.Execute(hit, hitHeight,
  previousHeight, maxStep)` — высота точки относительно ПРЕДЫДУЩЕЙ принятой; `RouteHintFloorRaycastProbe`
  (Infrastructure) — адаптер физики: луч вниз мимо триггеров и коллайдеров игрока, `MaxHits` = 16 попаданий
  по расстоянию, при заполненном буфере — повтор через `RaycastAll`.
- `RouteHintStarField.Sample(centerline, distances, clearances, length, head, reveal, alpha, elapsed, style)` —
  чистая функция потока звёзд (`RouteHintStarStyle` из `LevelMinimapSettings.CreateRouteHintStarStyle()`):
  решётка слотов привязана к расстоянию по линии и дрейфует к цели; размер, мерцание (меньшинство — долгое и
  медленное), качка вдоль пути и вбок — по зерну звезды; боковой разброс зажат свободной шириной линии
  минус `WallMarginMeters`; результат — `RouteHintStar[]` без аллокаций.
- `RouteHintFloorView` (MonoBehaviour корня префаба) — `Show(RouteHintRibbonMeshData, settings)` и
  `Show(path, clearances, settings)`, `UpdatePath(path, clearances)` (перепланирование без перезапуска
  проявления и часов), `SetHead(meters)`, `HeadDistance`,
  `Render(reveal, alpha, time, settings)`, `Hide()`, `HasMaterial`, `HasPath`, `Sparkles`,
  `RevealProgress`, `AnimationTime`, `StarsShaderName`; рисует только Shuriken-звёзды по `Centerline`.
  `HasRibbon` — совместимое имя для `HasPath`, `IsRibbonVisible` всегда `false`: полосы нет.
  `HardParticleCap = 512`, массив переиспользуется; настраиваемый SO-бюджет по умолчанию — 400.
  Плотность — 10 искр/м (шаг 0,1 м), ширина 0,4 м (±0,2), высота 0,05–0,3 м (0,175±0,125).
  Размер — от `RouteHintFloorSparkleSizeMinScale` до `…SizeMaxScale` базового с перекосом `…SizeSkew`
  (большинство мелкие); мерцание — быстрое со спредом и долгое для доли `…LongTwinkleFraction`; у каждой
  звезды своя качка (`…AlongWobbleMeters`, `…LateralWobbleMeters`); скорость дрейфа по умолчанию 0,5 м/с.
  Один ParticleSystem, один материал, до 400 билбордов/800 треугольников по умолчанию; без allocations в кадре.
  Шейдер — `RouteHintFloorStars` по историческому пути
  `Runtime/Shaders/RouteHintFloorRibbon.shader`; готовая текстура — `Epic Toon FX/Textures/sparkle2.png`.
  Все параметры цепочки — `LevelMinimapSettings.RouteHintFloorSparkle*`: шаг, скорость, размер, высота,
  цвет, разброс, мерцание, затухание у цели и фронта, максимум частиц. Полностью убрана подложка пола.
- `LevelMinimapController.RouteHintTrail` (общая линия), `RouteHintRevealPhase`, `RouteHintFloorView`,
  `RouteHintFloorPolyline`/`RouteHintWorldPath` (точки общей линии), `SetRouteHintFloorView(view)`, internal
  `AdvanceRouteHintFloor(hintVisible, now)`, `RouteHintPlanCount` (сколько раз работал планировщик),
  `RouteHintGeometryForTests`, `ProjectRouteHintPoint` (шаги для тестов); настройки —
  `LevelMinimapSettings.RouteHintFloor*`, `RouteHintPath*` (`WallClearanceMeters`, `DoorApproachMeters`,
  `StartOffsetMeters`, `MinVisibleMeters`), `RouteHintReplanDeviationMeters`/`RouteHintReplanIntervalSeconds`,
  `CreateRouteHintRevealTiming()`, `CreateRouteHintRibbonShape()`, `CreateRouteHintSmoothingShape()`,
  `CreateRouteHintPathOptions()`, `CreateRouteHintStarStyle()`.

Project-specific spawn, room module binding, rewards и mission flow остаются в слое интеграции игры-хозяина.

## Editor API

`Neoxider.LevelGeneration.Editor` содержит:

- Room authoring toolkit.
- Room compatibility report.
- Door snap/grid tools для level designer.
- Preview/minimap drawing primitives.

Editor tools должны проверять тот же door frame contract, что runtime generator.

`IInteractiveRoomKindRule.RequiresTemplate` запрещает пропуск контента без совместимого шаблона, даже если он optional.
Обязательный контент требует шаблон для любого правила. `CreateInteractiveContentConfig` сообщает id контента, вид
и tileset в `InvalidOperationException`; хост получает явный дефект конфигурации вместо отсутствующей комнаты.

### Уточнения подсказки маршрута (2026-10-15)

- `RouteHintRevealInput.Now` и `RouteHintFloorView.Render(..., time, ...)` — игровое время в секундах
  (`Time.time`); движение и мерцание вычисляются по тому же аргументу. Shuriken получает готовые частицы
  через `SetParticles`, его автономное движение выключено (`simulationSpeed=0`, `useUnscaledTime=false`).
- `Hide()` немедленно выключает ленту, останавливает и очищает все дочерние звёздочки, даже на паузе.
- Internal-границы тестов: `ProjectPlayerPosition`,
  `BuildRouteHintScreenPath`, `RouteHintScreenPath`; доступ — существующий `InternalsVisibleTo`, без reflection.
- Маркер и безопасное живое начало маршрута проецируются из мировой позиции игрока; центр большой карты
  остаётся центром границ комнат, компактной — позицией игрока.

В `TickFrame` аргумент `now` обслуживает раскрытие комнат; `scaledNow` — анимацию подсказки.
Runtime передаёт соответственно `Time.unscaledTime` и `Time.time`; тест без второго таймера использует `now`.

## Принудительный главный путь

`IForcedMainPathProvider.ForcedMainPath` задаёт упорядоченные комнаты между Start и Exit.
`IForcedMainPathRoom` несёт Kind, ContentId и необязательный PreferredRoomTemplateId.
Пустой список сохраняет обычное планирование. Непустой список заменяет главный путь;
подбор комнат, размещение, закрытие лишних дверей и валидация остаются общими.
`BuildLayoutAsync` копирует этот список на вызывающем потоке вместе с остальным конфигом.

### Скрытие подсказки на паузе (2026-10-15)

- `RouteHintRevealInput.UnscaledNow` — отдельный немасштабируемый таймер для `HintHidden`.
  `Now` остаётся игровым временем проявления и угасания при подходе к цели; `Render(..., time, ...)`
  по-прежнему получает `Now`, поэтому движение, пополнение и мерцание искр не оживают на паузе.
- `UpdateRouteHint(deltaTime, now, playerInControl, unscaledNow = null, unscaledDeltaTime = null)`
  получает оба таймера через `TickFrame`. Старые изолированные стенды без второго таймера используют первый.
- Любое `visible=false` скрывает пол и пунктир за `RouteHintFloorHideFadeSeconds` (0,18 с) по unscaled time.
  `AdvanceRouteHintFloor(hintVisible, now, unscaledNow = null)` завершает скрытие вызовом `FloorView.Hide()`.
