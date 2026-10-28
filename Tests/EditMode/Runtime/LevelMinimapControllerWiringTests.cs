using System;
using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using RedoSchool.Features.LevelGeneration.Runtime.Application;
using RedoSchool.Features.LevelGeneration.Runtime.Domain.Events;
using RedoSchool.Features.LevelGeneration.Runtime.Infrastructure;
using RedoSchool.Features.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// Проводка контроллера миникарты: раскрытие при входе, события дверей и
    /// хвостов, и когда подсказка маршрута на самом деле рисуется.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelMinimapControllerWiringTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null)
                {
                    Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();
        }

        [Test]
        public void DoorOpenedForPlayer_InSpawnedRoom_RevealsTheRoomBehind_WithoutVisitingIt()
        {
            LevelMinimapController controller = CreateController();
            (RoomInstance start, DoorSocket east) = CreateRoom("n_start", "east");
            controller.Bind(CreateLayout(), new SimpleRoomEventBus(), Rooms(start));

            Assert.IsTrue(east.TrySetTriggeredOpen(true));

            Assert.IsTrue(controller.Session.IsRevealPending("n_objective"),
                "Комната за открытой дверью обязана проявиться на карте до входа в неё.");
            Assert.AreEqual(1, controller.Session.RevealDueRooms(float.MaxValue));
            Assert.IsTrue(controller.Session.IsRoomRevealed("n_objective"));
            Assert.AreEqual("n_start", controller.Session.CurrentRoomId, "Открыть дверь — не войти.");
            Assert.IsFalse(controller.RouteHint.IsRoomVisited("n_objective"),
                "Для подсказки маршрута комната за дверью остаётся непосещённой.");
        }

        [Test]
        public void DoorOpenedForPlayer_FirstOpeningOfNewRoom_KeepsHintTimer()
        {
            LevelMinimapController controller = CreateController();
            (RoomInstance start, DoorSocket east) = CreateRoom("n_start", "east");
            controller.Bind(CreateLayout(), new SimpleRoomEventBus(), Rooms(start));
            controller.RouteHint.Tick(controller.RouteHint.IdleThresholdSeconds, true);
            Assert.AreEqual(RouteHintOutcome.ShowHint, controller.RouteHint.Decision.Outcome,
                "Precondition: the hint is shown.");

            east.TrySetTriggeredOpen(true);

            Assert.AreEqual(controller.RouteHint.IdleThresholdSeconds, controller.RouteHint.IdleSeconds,
                "Opening a door is not progress: the idle timer keeps running.");
            Assert.AreEqual(RouteHintOutcome.ShowHint, controller.RouteHint.Decision.Outcome);
            Assert.IsTrue(controller.Session.IsRevealPending("n_objective"));
        }

        [Test]
        public void NotifyDoorOpened_RepeatedSignal_KeepsHintTimer()
        {
            LevelMinimapController controller = CreateController();
            controller.Bind(CreateLayout());
            controller.NotifyDoorOpened("n_start", "east");
            controller.RouteHint.Tick(controller.RouteHint.IdleThresholdSeconds, true);

            controller.NotifyDoorOpened("n_start", "east");

            Assert.AreEqual(RouteHintOutcome.ShowHint, controller.RouteHint.Decision.Outcome);
            Assert.AreEqual(controller.RouteHint.IdleThresholdSeconds, controller.RouteHint.IdleSeconds);
        }

        [Test]
        public void TargetTaskAnchor_DisabledWhileIncomplete_DoesNotResetIdleTimer_CompletedResetsIt()
        {
            LevelMinimapController controller = CreateController();
            (RoomInstance start, _) = CreateRoom("n_start", "east");
            RouteHintTaskAnchorStub anchor = CreateTaskAnchor(start, new Vector3(0.5f, 0f, 0.8f), false);
            controller.Bind(CreateLayout(), new SimpleRoomEventBus(), Rooms(start));

            controller.TickFrame(RouteHintPresence.Exploring, false, 10f, 10f, 0f);
            controller.TickFrame(RouteHintPresence.Exploring, false, 10f, 10f, 10f);
            float trackedIdle = controller.RouteHint.IdleSeconds;
            Assert.Greater(trackedIdle, 0f, "Precondition: idle accumulates while the target task is open.");

            anchor.PositionOk = false;
            controller.TickFrame(RouteHintPresence.Exploring, false, 10f, 10f, 20f);

            Assert.AreEqual(trackedIdle + 10f, controller.RouteHint.IdleSeconds, 0.001f,
                "A disabled panel is not a completed task: the idle timer keeps running.");

            anchor.Completed = true;
            controller.TickFrame(RouteHintPresence.Exploring, false, 10f, 10f, 30f);

            Assert.AreEqual(0f, controller.RouteHint.IdleSeconds);
        }

        [Test]
        public void RouteHint_IsDrawnOnlyWhenDue_EnabledAndPlayerInControl()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout());

            controller.UpdateRouteHint(10f, 0f, true);
            Assert.AreEqual(0f, controller.RouteHintAlpha, "до порога простоя подсказки нет");

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 1f, true);
            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Assert.Greater(controller.RouteHintAlpha, 0f);

            float idle = controller.RouteHint.IdleSeconds;
            controller.UpdateRouteHint(5f, 2f, false);
            Assert.AreEqual(0f, controller.RouteHintAlpha, "без управления подсказка гаснет");
            Assert.AreEqual(idle, controller.RouteHint.IdleSeconds, "и таймер стоит");
        }

        [Test]
        public void RouteHint_DisabledInSettings_StaysHidden_UnlessForcedByCheat()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            settings.SetRouteHintEnabled(false);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout());

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds * 2f, 0f, true);
            Assert.AreEqual(0f, controller.RouteHintAlpha);

            controller.SetRouteHintForced(true);
            controller.UpdateRouteHint(0.1f, 1f, true);
            Assert.Greater(controller.RouteHintAlpha, 0f);
        }

        [Test]
        public void RouteHint_WithoutRoomReveal_LeadsOnlyToNextDoor()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout());

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);
            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Assert.AreEqual("n_objective", controller.RouteHint.Decision.TargetDoor.ToNodeId);
            Assert.Greater(controller.RouteHintAlpha, 0f);
            Assert.IsFalse(controller.Session.IsRoomRevealed("n_objective"));
        }

        [Test]
        public void RouteHint_LockedAnswerDoor_StillLeadsToNextCriticalDoor()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.1f, 0f, 0.1f));
            controller.Bind(CreateLayout(lockStartExit: true));

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Assert.AreEqual("n_objective", controller.RouteHint.Decision.TargetDoor.ToNodeId);
            // Путь не обрывается в проёме, а продолжается за дверь (RouteHintPathOptions.DoorLeadOutMeters), поэтому
            // проверяется, что линия проходит центр двери, а не что она в нём кончается.
            Vector2 door = new(LevelMinimapSession.LogicalCellSize, 0.5f);
            Assert.Less(DistanceToPolyline(controller.RouteHintWorldPath, door), 0.01f,
                "цель — дверь следующей комнаты");
        }

        [Test]
        public void RouteHint_LockedLiveSocket_StillLeadsToDoor()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            (RoomInstance start, DoorSocket east) = CreateRoom("n_start", "east");
            east.ApplyState(DoorState.Locked);
            controller.Bind(CreateLayout(), null, Rooms(start));

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Vector2 end = controller.RouteHintWorldPath[controller.RouteHintWorldPath.Count - 1];
            Assert.Less(Vector2.Distance(end, new Vector2(east.transform.position.x, east.transform.position.z)),
                0.01f, "цель — фактический сокет двери, даже когда он заперт");
        }

        [Test]
        public void RouteHint_IncompleteInteractiveRoom_LeadsToPanel()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            (RoomInstance start, DoorSocket _) = CreateRoom("n_start", "east");
            RouteHintTaskAnchorStub panel = CreateTaskAnchor(start, new Vector3(0.5f, 0f, 0.8f), false);
            controller.Bind(CreateLayout(), null, Rooms(start));

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Vector2 end = controller.RouteHintWorldPath[controller.RouteHintWorldPath.Count - 1];
            Assert.Less(Vector2.Distance(end, new Vector2(panel.transform.position.x, panel.transform.position.z)), 0.01f);
        }

        [Test]
        public void RouteHint_CompletedInteractiveRoom_LeadsToNextDoor()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            (RoomInstance start, DoorSocket east) = CreateRoom("n_start", "east");
            CreateTaskAnchor(start, new Vector3(0.5f, 0f, 0.8f), true);
            controller.Bind(CreateLayout(), null, Rooms(start));

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Vector2 end = controller.RouteHintWorldPath[controller.RouteHintWorldPath.Count - 1];
            Assert.Less(Vector2.Distance(end, new Vector2(east.transform.position.x, east.transform.position.z)),
                0.01f, "после завершения задания цель — фактическая дверь");
        }

        [Test]
        public void RouteHint_AuthoredStartPrefab_UsesFloorAtPlacementBounds()
        {
            const string prefabPath =
                "Assets/_source/Prefabs/Rooms/SciFiAcademy/RoomPrefabs/StartToonel.prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.NotNull(prefab);
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _created.Add(instance);
            RoomTemplate template = instance.GetComponent<RoomTemplate>();
            Assert.NotNull(template);
            RoomInstance room = instance.GetComponent<RoomInstance>() ?? instance.AddComponent<RoomInstance>();
            room.Configure("n_start", RoomKind.Start, template);
            DoorSocket east = template.FindSocket("east");
            Assert.NotNull(east);
            Quaternion rotation = instance.transform.rotation;
            Vector3 origin = instance.transform.position;
            RoomWorldPose pose = new(origin.x, origin.y, origin.z,
                rotation.x, rotation.y, rotation.z, rotation.w);
            LevelLayout layout = new(new[]
                {
                    new PlacedRoom("n_start", template.RoomId, new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>(), pose, template.Footprint, template.FootprintShape),
                    Room("n_objective", 2f, new Dictionary<string, DoorState>())
                },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west", true) },
                "n_start", new[] { "n_objective" });
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            Vector3 playerPoint = instance.transform.TransformPoint(new Vector3(
                template.PlacementBoundsMinXMeters + 2.5f, 0f,
                template.PlacementBoundsMinZMeters + 2.5f));
            PlacePlayer(controller, playerPoint);
            controller.Bind(layout, null, Rooms(room));

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Vector3 doorCenter = east.ResolveAuthoringWorldCenter();
            Vector2 end = controller.RouteHintWorldPath[controller.RouteHintWorldPath.Count - 1];
            Assert.Less(Vector2.Distance(end, new Vector2(doorCenter.x, doorCenter.z)), 0.05f);
            Assert.IsFalse(controller.Session.IsRoomRevealed("n_objective"));
        }

        [TestCase("Assets/_source/Prefabs/Rooms/SciFiAcademy/RoomPrefabs/EndToonel.prefab", "entry")]
        public void RouteHint_AuthoredEntrySocket_IsReachableFromRoomInterior(string prefabPath, string socketId)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.NotNull(prefab);
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _created.Add(instance);
            RoomTemplate template = instance.GetComponent<RoomTemplate>();
            Assert.NotNull(template);
            DoorSocket entry = template.FindSocket(socketId);
            Assert.NotNull(entry);
            RoomInstance room = instance.GetComponent<RoomInstance>() ?? instance.AddComponent<RoomInstance>();
            room.Configure("n_start", RoomKind.Start, template);
            Vector3 origin = instance.transform.position;
            Quaternion rotation = instance.transform.rotation;
            RoomWorldPose pose = new(origin.x, origin.y, origin.z,
                rotation.x, rotation.y, rotation.z, rotation.w);
            LevelLayout layout = new(new[]
                {
                    new PlacedRoom("n_start", template.RoomId, new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>(), pose, template.Footprint, template.FootprintShape),
                    Room("n_next", 2f, new Dictionary<string, DoorState>())
                }, new[] { new DoorConnection("n_start", socketId, "n_next", "west", true) },
                "n_start", new[] { "n_next" });
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            Vector3 interior = entry.ResolveAuthoringWorldCenter()
                               - entry.ResolveAuthoringWorldOutward(instance.transform).normalized;
            PlacePlayer(controller, interior);
            controller.Bind(layout, null, Rooms(room));

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome,
                "точка входной двери авторского префаба доступна из комнаты");
            Vector3 center = entry.ResolveAuthoringWorldCenter();
            Vector2 end = controller.RouteHintWorldPath[controller.RouteHintWorldPath.Count - 1];
            Assert.Less(Vector2.Distance(end, new Vector2(center.x, center.z)), 0.05f);
        }

        [Test]
        public void RouteHint_QuizEntryOutsideFootprint_HasReachableDoorApproach()
        {
            const string prefabPath =
                "Assets/_source/Prefabs/Rooms/SciFiAcademy/RoomPrefabs/Quiz/T-tonnel (2 doors exit).prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.NotNull(prefab);
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _created.Add(instance);
            RoomTemplate template = instance.GetComponent<RoomTemplate>();
            Assert.NotNull(template);
            DoorSocket entry = template.FindSocket("entry");
            Assert.NotNull(entry);
            RouteHintGeometry geometry = new();
            List<MinimapCellSnapshot> cells = new();
            IReadOnlyList<RoomFootprintCell> occupied = template.FootprintShape.Cells;
            for (int i = 0; i < occupied.Count; i++)
            {
                cells.Add(new MinimapCellSnapshot(
                    template.PlacementBoundsMinXMeters + occupied[i].X * LevelMinimapSession.CellSizeMeters,
                    template.PlacementBoundsMinZMeters + occupied[i].Z * LevelMinimapSession.CellSizeMeters,
                    LevelMinimapSession.CellSizeMeters, LevelMinimapSession.CellSizeMeters));
            }

            IReadOnlyList<RoomWallSegment2D> authoredWalls = RoomWallSegmentExtractor.Extract(template);
            List<RouteHintWall> walls = new();
            for (int i = 0; i < authoredWalls.Count; i++)
            {
                RoomWallSegment2D wall = authoredWalls[i];
                Vector3 a = instance.transform.TransformPoint(new Vector3(wall.Ax, 0f, wall.Az));
                Vector3 b = instance.transform.TransformPoint(new Vector3(wall.Bx, 0f, wall.Bz));
                walls.Add(new RouteHintWall(new Vector2(a.x, a.z), new Vector2(b.x, b.z), wall.WidthMeters));
            }

            Vector3 origin = instance.transform.position;
            geometry.AddRoom("room", new Vector2(origin.x, origin.z), instance.transform.eulerAngles.y,
                cells, walls);
            Vector3 center = entry.ResolveAuthoringWorldCenter();
            Vector2 door = new(center.x, center.z);
            geometry.AddDoor("room", "entry", "next", "west", door);
            Vector3 interior = center - entry.ResolveAuthoringWorldOutward(instance.transform).normalized;
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true,
                new Vector2(interior.x, interior.z), "room",
                new CriticalPathDoor("room", "entry", "next", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "проём авторского входа доступен из комнаты, даже если сокет выступает за контур пола");
            Assert.Less(Vector2.Distance(path[path.Count - 1], door), 0.05f);
        }

        /// <summary>
        /// Живая дверь связи — дверь From-комнаты; её выключенное состояние закрывает проход.
        /// </summary>
        [Test]
        public void RouteHint_DisabledLiveFromDoor_DoesNotProduceRoute()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout(startExitState: DoorState.Disabled));
            controller.RestoreCheckpoint(new[] { "n_start", "n_objective" }, "n_start",
                new[] { "n_objective" });

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, controller.LastRouteHintPathOutcome);
            Assert.IsEmpty(controller.RouteHintWorldPath);
        }

        /// <summary>
        /// Регрессия: «выключенная» To-сторона связи — это дубль, который игра прячет у КАЖДОЙ связи
        /// (<c>LevelSpawner</c> оставляет живой только дверь From-комнаты). Прежде он делал запертым каждый
        /// проход, и подсказка не находила дороги через уже пройденную комнату. Тест на этом месте раньше
        /// утверждал обратное.
        /// </summary>
        [Test]
        public void RouteHint_DisabledDuplicateToDoor_StillProducesRoute()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout(middleDoorState: DoorState.Disabled));
            controller.RestoreCheckpoint(new[] { "n_start", "n_objective" }, "n_start",
                new[] { "n_objective" });

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual("n_exit", controller.RouteHint.Decision.TargetDoor.ToNodeId,
                "Precondition: the target lies beyond the passed room, across the connection with the duplicate.");
            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Assert.IsNotEmpty(controller.RouteHintWorldPath);
        }

        /// <summary>
        /// То же на сцене, как её собирает игра: в To-комнате дубль двери выключен и спрятан
        /// (<c>DoorState.Disabled</c> + <c>SetActive(false)</c>), живая дверь — в From-комнате. Ученик
        /// возвращается из боковой комнаты (тупика) через такую связь — проход обязан считаться открытым.
        /// </summary>
        [Test]
        public void RouteHint_ConnectionWithHiddenDuplicateToDoor_IsCrossable()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            (RoomInstance start, RoomInstance objective, DoorSocket west) = CreateStartAndObjectiveRooms();
            west.ApplyGeneratedState(DoorState.Disabled);
            west.gameObject.SetActive(false);
            controller.Bind(CreateLayout(), null, Rooms(start, objective));
            controller.RestoreCheckpoint(new[] { "n_start", "n_objective" }, "n_start",
                new[] { "n_objective" }, true);

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual("n_exit", controller.RouteHint.Decision.TargetDoor.ToNodeId,
                "Precondition: the target is beyond n_objective, the route must cross n_start → n_objective.");
            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome,
                "A hidden duplicate on the To side is not a locked passage.");
        }

        [Test]
        public void RouteHint_LockedLiveDoorOnTheToSide_StillClosesThePassage()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            (RoomInstance start, RoomInstance objective, DoorSocket west) = CreateStartAndObjectiveRooms();
            west.ApplyState(DoorState.Locked);
            controller.Bind(CreateLayout(), null, Rooms(start, objective));
            controller.RestoreCheckpoint(new[] { "n_start", "n_objective" }, "n_start",
                new[] { "n_objective" }, true);

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, controller.LastRouteHintPathOutcome,
                "A LIVE locked door on the To side does block the way (both doors are live in this scene).");
        }

        /// <summary>
        /// Регрессия: у связи живая только дверь From-комнаты, и ученик, подошедший к ней из To-комнаты (обходом
        /// или после возврата из чекпоинта), прежде «раскрывал» комнату, где уже стоит, — а комната за дверью
        /// оставалась тёмной.
        /// </summary>
        [Test]
        public void DoorOpenedForPlayer_FromTheToSide_RevealsTheRoomThatOwnsTheDoor()
        {
            LevelMinimapController controller = CreateController();
            (RoomInstance objective, DoorSocket objectiveEast) = CreateRoom("n_objective", "east");
            SimpleRoomEventBus bus = new();
            controller.Bind(CreateLayout(), bus, Rooms(objective));
            bus.Publish(new RoomEnteredEvent("n_exit", RoomKind.Exit));
            Assert.IsFalse(controller.Session.IsRoomRevealed("n_objective"), "Precondition: n_objective is dark.");

            Assert.IsTrue(objectiveEast.TrySetTriggeredOpen(true));

            Assert.IsTrue(controller.Session.IsRevealPending("n_objective"),
                "The door n_objective → n_exit opened in front of a student standing in n_exit: the room "
                + "behind it (the door's owner) must reveal.");
            Assert.AreEqual("n_exit", controller.Session.CurrentRoomId, "Opening a door is not entering.");
        }

        [Test]
        public void DoorMarkers_DoorUnlocked_RebuildsTheGeometryCache()
        {
            LevelMinimapController controller = CreateController(out _);
            SerializedObject serialized = new(controller);
            serialized.FindProperty("drawDoorMarkers").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            (RoomInstance start, DoorSocket east) = CreateRoom("n_start", "east");
            east.ApplyState(DoorState.Locked);
            controller.Bind(CreateLayout(), null, Rooms(start));
            Rect rect = new(0f, 0f, 220f, 160f);
            Assert.IsTrue(controller.WarmStaticMapCacheForTests(rect, false));
            int built = controller.StaticMapRebuildCount;
            controller.TickFrame(RouteHintPresence.Exploring, false, 0.01f, 0.01f, 0.01f);
            Assert.IsTrue(controller.WarmStaticMapCacheForTests(rect, false));
            Assert.AreEqual(built, controller.StaticMapRebuildCount, "Precondition: nothing changed, no rebuild.");

            east.ApplyState(DoorState.Open);
            controller.TickFrame(RouteHintPresence.Exploring, false, 0.01f, 0.01f, 0.02f);
            Assert.IsTrue(controller.WarmStaticMapCacheForTests(rect, false));

            Assert.AreEqual(built + 1, controller.StaticMapRebuildCount,
                "The unlocked door is baked into the cache with its colour: the cache must refresh.");
        }

        [Test]
        public void RouteHint_TransitDoorDisabledAfterPlanning_DiscardsCachedPath()
        {
            Dictionary<string, DoorState> startDoors = new(StringComparer.Ordinal);
            LevelLayout layout = new(new[]
                {
                    Room("n_start", 0f, startDoors),
                    Room("n_objective", LevelMinimapSession.LogicalCellSize,
                        new Dictionary<string, DoorState>()),
                    Room("n_exit", LevelMinimapSession.LogicalCellSize * 2f,
                        new Dictionary<string, DoorState>())
                }, new[]
                {
                    new DoorConnection("n_start", "east", "n_objective", "west", true),
                    new DoorConnection("n_objective", "east", "n_exit", "west", true)
                }, "n_start", new[] { "n_exit" });
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(layout);
            controller.RestoreCheckpoint(new[] { "n_start", "n_objective" }, "n_start",
                new[] { "n_objective" }, true);
            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);
            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome,
                "предусловие: путь через посещённую комнату существует");
            startDoors["east"] = DoorState.Disabled;

            controller.UpdateRouteHint(0f, settings.RouteHintReplanIntervalSeconds + 0.01f, true);

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, controller.LastRouteHintPathOutcome);
            Assert.IsEmpty(controller.RouteHintWorldPath, "закрытый транзит не остаётся в кэше");
        }

        [TestCase(true, 0f)]
        [TestCase(true, 37f)]
        [TestCase(false, 0f)]
        [TestCase(false, 37f)]
        public void MapProjection_DisplacedPlayer_MarkerFollowsHim_DashesStartAtTheSharedTrailHead(bool fullMap,
            float yaw)
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            PlacePlayer(controller, new Vector3(0.2f, 0f, 0.5f));
            GameObject camera = new("PlayerLook");
            camera.transform.SetParent(controller.PlayerTransform, false);
            camera.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            camera.AddComponent<Camera>();
            controller.Bind(CreateLayout());
            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);
            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            // Move within the room after planning: the trail's visible start follows him along the same points.
            controller.PlayerTransform.position = new Vector3(0.35f, 0f, 0.55f);
            controller.UpdateRouteHint(0f, 0.01f, true);
            Rect rect = new(0f, 0f, 800f, 480f);
            Assert.IsTrue(controller.TryBuildProjection(controller.Session.GetAllRooms(), rect, fullMap,
                out LevelMinimapController.MapProjection projection));
            Vector2 world = new(controller.PlayerTransform.position.x, controller.PlayerTransform.position.z);
            Vector2 expected = ToScreen(world, projection, rect, yaw);
            RouteHintTrail trail = controller.RouteHintTrail;
            Vector2 expectedHead = ToScreen(trail.HeadPoint, projection, rect, yaw);

            float length = controller.BuildRouteHintScreenPath(projection, fullMap);

            Assert.Greater(length, 0f, "The shared trail still has a visible part ahead of the student.");
            Assert.Less(Vector2.Distance(expected, controller.ProjectPlayerPosition(projection)), 0.001f,
                "The player marker must use the actual world position in the map projection.");
            Assert.Less(Vector2.Distance(expectedHead, controller.RouteHintScreenPath[0]), 0.001f,
                "The dashes start at the shared trail's visible head, the same point the floor stars start from.");
            Assert.Greater(trail.HeadDistance, trail.ProgressMeters,
                "The visible start is ahead of the student along the path, not at his feet.");
            if (fullMap)
            {
                Assert.Greater(Vector2.Distance(expected, projection.ScreenCenter), 1f,
                    "The player is displaced from the rooms' bounds center.");
            }
            else
            {
                Assert.Less(Vector2.Distance(expected, projection.ScreenCenter), 0.001f,
                    "The compact map stays centered on the player.");
            }
        }

        private static Vector2 ToScreen(Vector2 world, LevelMinimapController.MapProjection projection, Rect rect,
            float yaw)
        {
            Vector2 offset = world - projection.WorldCenter;
            Vector3 rotated = Quaternion.Euler(0f, -yaw, 0f) * new Vector3(offset.x, 0f, offset.y);
            return rect.center + new Vector2(rotated.x, -rotated.z) * projection.Scale;
        }

        private static float DistanceToPolyline(IReadOnlyList<Vector2> path, Vector2 point)
        {
            float best = float.MaxValue;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector2 segment = path[i + 1] - path[i];
                float t = segment.sqrMagnitude > 0.000001f
                    ? Mathf.Clamp01(Vector2.Dot(point - path[i], segment) / segment.sqrMagnitude)
                    : 0f;
                best = Mathf.Min(best, Vector2.Distance(point, path[i] + segment * t));
            }

            return best;
        }

        private LevelMinimapController CreateController() => CreateController(out _);

        private LevelMinimapController CreateController(out LevelMinimapSettings settings)
        {
            GameObject go = new("MinimapWiringTest");
            _created.Add(go);
            settings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
            _created.Add(settings);
            LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
            controller.SetSettings(settings);
            return controller;
        }

        private void PlacePlayer(LevelMinimapController controller, Vector3 position)
        {
            GameObject player = new("MinimapWiringPlayer");
            _created.Add(player);
            player.transform.position = position;
            controller.PlayerTransform = player.transform;
        }

        private (RoomInstance room, DoorSocket door) CreateRoom(string nodeId, string socketId) =>
            CreateRoom(nodeId, socketId, Vector3.zero, Vector3.zero);

        private (RoomInstance room, DoorSocket door) CreateRoom(string nodeId, string socketId, Vector3 roomPosition,
            Vector3 doorLocalPosition)
        {
            GameObject roomGo = new($"Room_{nodeId}");
            _created.Add(roomGo);
            roomGo.transform.position = roomPosition;
            GameObject doorGo = new($"Door_{socketId}");
            doorGo.transform.SetParent(roomGo.transform, false);
            doorGo.transform.localPosition = doorLocalPosition;

            DoorSocket door = doorGo.AddComponent<DoorSocket>();
            SerializedObject doorSo = new(door);
            doorSo.FindProperty("socketId").stringValue = socketId;
            doorSo.ApplyModifiedPropertiesWithoutUndo();
            door.ApplyGeneratedState(DoorState.Closed);

            RoomTemplate template = roomGo.AddComponent<RoomTemplate>();
            RoomInstance instance = roomGo.AddComponent<RoomInstance>();
            instance.Configure(nodeId, RoomKind.Start, template);
            Assert.AreSame(door, instance.FindSocket(socketId), "дверь обязана попасть в комнату");
            return (instance, door);
        }

        /// <summary>
        /// Сцена связи n_start → n_objective из <see cref="CreateLayout"/>: комнаты стоят там же, где в раскладке, а
        /// дверь From-комнаты (восток n_start) и её дубль в To-комнате (запад n_objective) — в одной точке шва, как
        /// генератор стыкует сокеты.
        /// </summary>
        private (RoomInstance start, RoomInstance objective, DoorSocket objectiveWest) CreateStartAndObjectiveRooms()
        {
            const float cell = LevelMinimapSession.LogicalCellSize;
            (RoomInstance start, _) = CreateRoom("n_start", "east", Vector3.zero, new Vector3(cell, 0f, 0.5f));
            (RoomInstance objective, DoorSocket west) = CreateRoom("n_objective", "west",
                new Vector3(cell, 0f, 0f), new Vector3(0f, 0f, 0.5f));
            return (start, objective, west);
        }

        private static RouteHintTaskAnchorStub CreateTaskAnchor(RoomInstance room, Vector3 worldPosition,
            bool completed)
        {
            GameObject anchor = new("TaskAnchor");
            anchor.transform.SetParent(room.transform, false);
            anchor.transform.position = worldPosition;
            RouteHintTaskAnchorStub stub = anchor.AddComponent<RouteHintTaskAnchorStub>();
            stub.Completed = completed;
            return stub;
        }

        private static Dictionary<string, RoomInstance> Rooms(params RoomInstance[] rooms)
        {
            Dictionary<string, RoomInstance> result = new(StringComparer.Ordinal);
            for (int i = 0; i < rooms.Length; i++)
            {
                result[rooms[i].NodeId] = rooms[i];
            }

            return result;
        }

        /// <summary>
        /// Три комнаты в ряд, как их ставит генератор: соседние комнаты примыкают друг к другу, и дверь связи лежит
        /// на общем шве (восточный край одной — западный край другой). Шаг — <see cref="LevelMinimapSession.LogicalCellSize"/>:
        /// прежний шаг 2 остался от сетки, где логическая ячейка была 2, и после её смены на 1 между комнатами
        /// зияла пустая клетка — путь не мог перейти ни одну связь, а тесты «проход закрыт» зеленели и без замка.
        /// </summary>
        private static LevelLayout CreateLayout(bool lockStartExit = false,
            DoorState middleDoorState = DoorState.Open, DoorState? startExitState = null)
        {
            Dictionary<string, DoorState> startDoors = new(StringComparer.Ordinal);
            if (lockStartExit)
            {
                startDoors["east"] = DoorState.Locked;
            }

            if (startExitState.HasValue)
            {
                startDoors["east"] = startExitState.Value;
            }

            return new LevelLayout(
                new[]
                {
                    Room("n_start", 0f, startDoors),
                    Room("n_objective", LevelMinimapSession.LogicalCellSize, new Dictionary<string, DoorState>
                    {
                        ["west"] = middleDoorState
                    }),
                    Room("n_exit", LevelMinimapSession.LogicalCellSize * 2f, new Dictionary<string, DoorState>())
                },
                new[]
                {
                    new DoorConnection("n_start", "east", "n_objective", "west", true),
                    new DoorConnection("n_objective", "east", "n_exit", "west", true)
                },
                "n_start",
                new[] { "n_exit" });
        }

        private static PlacedRoom Room(string nodeId, float x, Dictionary<string, DoorState> doors) =>
            new(nodeId, $"template_{nodeId}", new GridPose(x, 0f, 0), doors);
    }

    public sealed class RouteHintTaskAnchorStub : MonoBehaviour, IRoomTaskAnchor
    {
        public bool Completed { get; set; }
        public bool PositionOk { get; set; } = true;
        public bool IsTaskCompleted => Completed;

        public bool TryGetTaskWorldPosition(out Vector3 position)
        {
            position = transform.position;
            return PositionOk;
        }
    }
}
