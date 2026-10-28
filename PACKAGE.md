# Neoxider LevelGeneration package boundary

`Assets/ThirdParty/Neoxider/LevelGeneration` - встроенная reusable библиотека внутри текущего Unity project. Ее можно вынести в отдельный package, но сейчас она хранится в репозитории как vendor/library folder.

## Цель

Держать общий генератор, placement, minimap и editor tooling отдельно от RedoSchool gameplay-интеграции.

## Assemblies

| Assembly | Folder | Ответственность |
|---|---|---|
| `RedoSchool.LevelGeneration.Domain` | `Generation/Domain` | Pure C# graph/layout/door domain. |
| `RedoSchool.LevelGeneration` | `Generation/Application` | Pure C# generator pipeline, V2 pacing, selectors, placement и validation APIs. |
| `RedoSchool.LevelGeneration.Unity` | `Generation` excluding nested core asmdefs | Unity-facing descriptors: `RoomTemplate`, `DoorSocket`, `TilesetSO`, `RoomDatabase`. |
| `RedoSchool.LevelGeneration.Runtime.Domain` | `Runtime/Domain` | Pure C# room/door/endpoint runtime events, `noEngineReferences=true`. |
| `RedoSchool.LevelGeneration.Runtime` | `Runtime` | Minimap/session/room-instance/transition primitives. |
| `RedoSchool.LevelGeneration.Editor` | `Editor` | Room authoring, compatibility и door snap tools. |

## Boundary rules

В package нельзя добавлять:

- `MissionMap`, `Quiz`, `Cheats`, `Neo.Shop`, rewards, CoreAI/VContainer integration;
- SciFiAcademy-specific JSON/assets;
- RedoSchool-only mission rules или UI flow;
- concrete room gameplay modules.

Project-specific adapters находятся в:

```text
Assets/_source/Scripts/Epics/World
```

## RedoSchool integration owns

- `MissionConfigSO` pacing/JSON provider и inspector-поля.
- Parser RedoSchool interactive JSON (`mandatory`, `required`, `isMandatory`, `layoutPacing`).
- Binding `RoomGameplayModuleSO` и gameplay modules.
- Runtime mission start/HUB/Cheats glue.
- SciFiAcademy rooms, pools, question JSON и designer docs.

## Room module reference

Библиотека хранит optional room module reference как `ScriptableObject` на `RoomTemplate`. RedoSchool spawner внутри integration layer приводит asset к `RoomGameplayModuleSO` и запускает project-specific runtime logic. Это сохраняет независимость reusable room template от конкретного gameplay module API.
