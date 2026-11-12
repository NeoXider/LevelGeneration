# Neoxider LevelGeneration package boundary

UPM-пакет `com.neoxider.level-generation` (репозиторий `NeoXider/LevelGeneration`), подключается по git-тегу.

## Цель

Держать общий генератор, placement, minimap и editor tooling отдельно от gameplay-интеграции игры-хозяина.

## Assemblies

| Assembly | Folder | Ответственность |
|---|---|---|
| `Neoxider.LevelGeneration.Domain` | `Generation/Domain` | Pure C# graph/layout/door domain. |
| `Neoxider.LevelGeneration` | `Generation/Application` | Pure C# generator pipeline, V2 pacing, selectors, placement и validation APIs. |
| `Neoxider.LevelGeneration.Unity` | `Generation` excluding nested core asmdefs | Unity-facing descriptors: `RoomTemplate`, `DoorSocket`, `TilesetSO`, `RoomDatabase`. |
| `Neoxider.LevelGeneration.Runtime.Domain` | `Runtime/Domain` | Pure C# room/door/endpoint runtime events, `noEngineReferences=true`. |
| `Neoxider.LevelGeneration.Runtime` | `Runtime` | Minimap/session/room-instance/transition primitives. |
| `Neoxider.LevelGeneration.Editor` | `Editor` | Room authoring, compatibility и door snap tools. |

## Boundary rules

В package нельзя добавлять:

- `MissionMap`, `Quiz`, `Cheats`, `Neo.Shop`, rewards, CoreAI/VContainer integration;
- контент и ассеты конкретной игры (комнаты, тайлсеты, префабы, id шаблонов);
- правила миссий или UI-поток конкретной игры;
- concrete room gameplay modules.

Адаптеры конкретного проекта живут в самой игре.

## Что остаётся у игры-хозяина

- `MissionConfigSO` pacing/JSON provider и inspector-поля.
- Разбор своего JSON интерактивного контента (`mandatory`, `required`, `isMandatory`, `layoutPacing`).
- Binding `RoomGameplayModuleSO` и gameplay modules.
- Runtime mission start/HUB/Cheats glue.
- Свои комнаты, пулы, тайлсеты и роли шаблонов (`RoomTemplateRoles`), настройки карты со своим префабом подсказки.

## Room module reference

Библиотека хранит optional room module reference как `ScriptableObject` на `RoomTemplate`. Спавнер игры в слое интеграции приводит asset к `RoomGameplayModuleSO` и запускает project-specific runtime logic. Это сохраняет независимость reusable room template от конкретного gameplay module API.
