using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using RedoSchool.Features.LevelGeneration.Runtime.Application;
using RedoSchool.Features.LevelGeneration.Runtime.Domain.Events;
using RedoSchool.Features.LevelGeneration.Runtime.Infrastructure;
using RedoSchool.Features.LevelGeneration.Runtime.Presentation;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    [Category("LevelGeneration")]
    public sealed class LevelMinimapSessionTests
    {
        [Test]
        public void Bind_RevealsOnlyStartRoom()
        {
            LevelMinimapSession session = new();

            session.Bind(CreateLayout());

            IReadOnlyList<MinimapRoomSnapshot> rooms = session.GetVisibleRooms();
            Assert.AreEqual(1, rooms.Count);
            Assert.AreEqual("n_start", rooms[0].NodeId);
            Assert.IsTrue(rooms[0].IsCurrent);
            Assert.IsFalse(session.IsRoomRevealed("n_objective"));
        }

        [Test]
        public void Bind_WithRevealStartRoomFalse_DoesNotRevealStart()
        {
            LevelMinimapSession session = new();

            session.Bind(CreateLayout(), false);

            Assert.AreEqual(0, session.GetVisibleRooms().Count);
            Assert.IsFalse(session.IsRoomRevealed("n_start"));
            Assert.IsNull(session.CurrentRoomId);
        }

        [Test]
        public void RevealRoom_WithUnknownNodeId_ReturnsFalse()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            bool result = session.RevealRoom("n_unknown");

            Assert.IsFalse(result);
            Assert.AreEqual("n_start", session.CurrentRoomId);
            Assert.AreEqual(1, session.GetVisibleRooms().Count);
            Assert.IsFalse(session.IsRoomRevealed("n_unknown"));
        }

        [Test]
        public void RoomEntered_RevealsRoomAndOnlyVisibleConnections()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            session.RevealRoom(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

            Assert.IsTrue(session.IsRoomRevealed("n_objective"));
            Assert.AreEqual("n_objective", session.CurrentRoomId);
            Assert.IsFalse(session.IsRoomRevealed("n_exit"));
            Assert.AreEqual(2, session.GetVisibleRooms().Count);
            Assert.AreEqual(1, session.GetVisibleConnections().Count);
        }

        [Test]
        public void GetAllRooms_ReturnsExitWithoutRevealingIt()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            IReadOnlyList<MinimapRoomSnapshot> rooms = session.GetAllRooms();

            Assert.AreEqual(3, rooms.Count);
            Assert.IsFalse(session.IsRoomRevealed("n_exit"));
            Assert.IsTrue(ContainsRoom(rooms, "n_exit", true));
        }

        [Test]
        public void GetAllConnections_ReturnsFullGeneratedPathWithoutRevealingRooms()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            IReadOnlyList<MinimapConnectionSnapshot> connections = session.GetAllConnections();

            Assert.AreEqual(2, connections.Count);
            Assert.AreEqual(0, session.GetVisibleConnections().Count);
            Assert.IsFalse(session.IsRoomRevealed("n_objective"));
            Assert.IsFalse(session.IsRoomRevealed("n_exit"));
        }

        [Test]
        public void Bind_WithPreserveRevealedRooms_KeepsVisitedRoomsForSameMission()
        {
            LevelMinimapSession session = new();
            LevelLayout layout = CreateLayout();
            session.Bind(layout);
            session.RevealRoom(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

            session.Bind(layout, preserveRevealedRooms: true);

            Assert.IsTrue(session.IsRoomRevealed("n_start"));
            Assert.IsTrue(session.IsRoomRevealed("n_objective"));
            Assert.IsFalse(session.IsRoomRevealed("n_exit"));
            Assert.AreEqual("n_objective", session.CurrentRoomId);
            Assert.AreEqual(2, session.GetVisibleRooms().Count);
        }

        [Test]
        public void Bind_WithoutPreserveRevealedRooms_ResetsVisitedRoomsForNewMission()
        {
            LevelMinimapSession session = new();
            LevelLayout layout = CreateLayout();
            session.Bind(layout);
            session.RevealRoom(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

            session.Bind(layout);

            Assert.IsTrue(session.IsRoomRevealed("n_start"));
            Assert.IsFalse(session.IsRoomRevealed("n_objective"));
            Assert.AreEqual(1, session.GetVisibleRooms().Count);
        }

        [Test]
        public void VisibleRooms_UseWorldPoseWithoutRevealingUnvisitedRooms()
        {
            LevelMinimapSession session = new();
            Quaternion rotation = Quaternion.Euler(0f, 90f, 0f);
            LevelLayout layout = new(
                new[]
                {
                    Room(
                        "n_start",
                        0,
                        0,
                        new RoomWorldPose(10f, 0f, 20f, rotation.x, rotation.y, rotation.z, rotation.w),
                        new RoomFootprint(2, 3)),
                    Room("n_exit", 4, 0, new RoomWorldPose(40f, 0f, 20f, 0f, 0f, 0f, 1f))
                },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });

            session.Bind(layout);

            IReadOnlyList<MinimapRoomSnapshot> rooms = session.GetVisibleRooms();
            Assert.AreEqual(1, rooms.Count);
            Assert.AreEqual("n_start", rooms[0].NodeId);
            Assert.AreEqual(10f, rooms[0].X);
            Assert.AreEqual(20f, rooms[0].Y);
            Assert.AreEqual(90, rooms[0].RotationDegrees);
            Assert.AreEqual(10f, rooms[0].Width);
            Assert.AreEqual(15f, rooms[0].Depth);
            Assert.IsTrue(rooms[0].UsesWorldPose);
            Assert.IsFalse(session.IsRoomRevealed("n_exit"));
        }

        [Test]
        public void ToggleFullMap_IgnoresCallsWithoutActiveLevel()
        {
            LevelMinimapSession session = new();

            Assert.IsFalse(session.ToggleFullMap());
            Assert.IsFalse(session.IsFullMapOpen);
        }

        [Test]
        public void ToggleFullMap_WorksAfterLevelBound()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            Assert.IsTrue(session.ToggleFullMap());
            Assert.IsTrue(session.IsFullMapOpen);
        }

        [Test]
        public void Controller_CanForceOverlayWithoutActiveLevelForHubCheat()
        {
            GameObject go = new("MinimapControllerTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();

                controller.SetOverlayVisible(true, true);

                Assert.IsTrue(controller.DrawRuntimeGui);
                Assert.IsTrue(controller.ForceVisibleWhenNoLevel);
                Assert.IsFalse(controller.HasActiveLevel);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_RestoreCheckpoint_RevealsCompletedPathAndCentersCurrentRoom()
        {
            GameObject go = new("MinimapControllerResumeTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
                controller.Bind(CreateLayout());

                controller.RestoreCheckpoint(new[] { "n_start", "n_objective" }, "n_objective");

                Assert.AreEqual("n_objective", controller.Session.CurrentRoomId);
                Assert.IsTrue(controller.Session.IsRoomRevealed("n_start"));
                Assert.IsTrue(controller.Session.IsRoomRevealed("n_objective"));
                Assert.AreEqual(2, controller.Session.GetVisibleRooms().Count);
                Assert.AreEqual(1, controller.Session.GetVisibleConnections().Count);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_FullMapBackground_IsMoreTransparentThanCompactMap()
        {
            GameObject go = new("MinimapControllerTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();

                Assert.Less(controller.FullMapBackgroundAlpha, controller.CompactMapBackgroundAlpha);
                Assert.LessOrEqual(controller.FullMapBackgroundAlpha, 0.5f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_DoesNotDrawDoorMarkersByDefault()
        {
            GameObject go = new("MinimapControllerTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();

                Assert.IsFalse(controller.DrawDoorMarkers);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_MapBackgroundAlpha_IsClampedToUnitRange()
        {
            GameObject go = new("MinimapControllerTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();

                FieldInfo compactField = typeof(LevelMinimapController)
                    .GetField("compactMapBackgroundAlpha", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fullField = typeof(LevelMinimapController)
                    .GetField("fullMapBackgroundAlpha", BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.NotNull(compactField);
                Assert.NotNull(fullField);

                compactField.SetValue(controller, 1.2f);
                fullField.SetValue(controller, -0.25f);

                Assert.AreEqual(1f, controller.CompactMapBackgroundAlpha);
                Assert.AreEqual(0f, controller.FullMapBackgroundAlpha);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_BindWithEventBus_RevealsEnteredRooms()
        {
            GameObject go = new("MinimapControllerTest");
            SimpleRoomEventBus bus = new();
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
                controller.Bind(CreateLayout(), bus);

                bus.Publish(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

                Assert.IsTrue(controller.Session.IsRoomRevealed("n_objective"));
                Assert.AreEqual("n_objective", controller.Session.CurrentRoomId);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_StaticMapCache_RebuildsOnlyAfterMapStateChange()
        {
            GameObject go = new("MinimapControllerTest");
            SimpleRoomEventBus bus = new();
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
                controller.Bind(CreateLayout(), bus);

                Assert.IsTrue(WarmStaticMapCache(controller, new Rect(0f, 0f, 220f, 160f), false));
                Assert.AreEqual(1, controller.StaticMapRebuildCount);

                Assert.IsTrue(WarmStaticMapCache(controller, new Rect(0f, 0f, 220f, 160f), false));
                Assert.AreEqual(1, controller.StaticMapRebuildCount);

                bus.Publish(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

                Assert.IsTrue(WarmStaticMapCache(controller, new Rect(0f, 0f, 220f, 160f), false));
                Assert.AreEqual(2, controller.StaticMapRebuildCount);

                Assert.IsTrue(WarmStaticMapCache(controller, new Rect(0f, 0f, 220f, 160f), false));
                Assert.AreEqual(2, controller.StaticMapRebuildCount);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_BindWithSpawnedRooms_KeepsUnvisitedRoomsHidden()
        {
            GameObject go = new("MinimapControllerTest");
            GameObject roomGo = new("RoomInstanceTest");
            SimpleRoomEventBus bus = new();
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
                RoomTemplate template = roomGo.AddComponent<RoomTemplate>();
                RoomInstance instance = roomGo.AddComponent<RoomInstance>();
                instance.Configure("n_start", RoomKind.Start, template);

                Dictionary<string, RoomInstance> rooms = new() { ["n_start"] = instance };

                controller.Bind(CreateLayout(), bus, rooms);

                Assert.IsTrue(controller.Session.IsRoomRevealed("n_start"));
                Assert.IsFalse(controller.Session.IsRoomRevealed("n_objective"));

                bus.Publish(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

                Assert.IsTrue(controller.Session.IsRoomRevealed("n_objective"));
                Assert.IsFalse(controller.Session.IsRoomRevealed("n_exit"));
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(roomGo);
            }
        }

        [Test]
        public void Controller_TryClipLineToRect_ClampsSegmentInsideRect()
        {
            MethodInfo method = typeof(LevelMinimapController).GetMethod("TryClipLineToRect",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Rect rect = new(0f, 0f, 10f, 10f);
            Vector2 a = new(-2f, 5f);
            Vector2 b = new(12f, 5f);
            object[] arguments = new object[] { rect, a, b };

            bool clipped = (bool)method.Invoke(null, arguments);
            Vector2 clippedA = (Vector2)arguments[1];
            Vector2 clippedB = (Vector2)arguments[2];

            Assert.IsTrue(clipped);
            Assert.AreEqual(0f, clippedA.x, 0.001f);
            Assert.AreEqual(10f, clippedB.x, 0.001f);
            Assert.AreEqual(5f, clippedA.y, 0.001f);
            Assert.AreEqual(5f, clippedB.y, 0.001f);
        }

        [Test]
        public void Controller_TryClipLineToRect_RejectsSegmentOutSideRect()
        {
            MethodInfo method = typeof(LevelMinimapController).GetMethod("TryClipLineToRect",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Rect rect = new(0f, 0f, 10f, 10f);
            Vector2 a = new(-5f, -5f);
            Vector2 b = new(-1f, -1f);
            object[] arguments = new object[] { rect, a, b };

            bool clipped = (bool)method.Invoke(null, arguments);

            Assert.IsFalse(clipped);
        }

        [Test]
        public void Controller_ClipLineAxis_UpdatesIntervalForConstraint()
        {
            MethodInfo method =
                typeof(LevelMinimapController).GetMethod("ClipLineAxis", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            float t0 = 0f;
            float t1 = 1f;
            object[] arguments = new object[] { -2f, -1f, t0, t1 };

            bool clipped = (bool)method.Invoke(null, arguments);

            Assert.IsTrue((bool)clipped);
            Assert.AreEqual(0.5f, (float)arguments[2], 0.001f);
            Assert.AreEqual(1f, (float)arguments[3], 0.001f);
        }

        [Test]
        public void ScheduleReveal_RevealsRoomOnlyAfterDelay_WithoutMovingCurrentRoom()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            Assert.IsTrue(session.ScheduleReveal("n_objective", 11f));

            Assert.AreEqual(0, session.RevealDueRooms(10.5f));
            Assert.IsFalse(session.IsRoomRevealed("n_objective"));
            Assert.IsTrue(session.IsRevealPending("n_objective"));

            Assert.AreEqual(1, session.RevealDueRooms(11f));
            Assert.IsTrue(session.IsRoomRevealed("n_objective"));
            Assert.IsFalse(session.HasPendingReveals);
            Assert.AreEqual("n_start", session.CurrentRoomId);
        }

        [Test]
        public void ScheduleReveal_IgnoresRevealedAndUnknownRooms_AndKeepsEarlierDeadline()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            Assert.IsFalse(session.ScheduleReveal("n_start", 1f));
            Assert.IsFalse(session.ScheduleReveal("n_unknown", 1f));

            Assert.IsTrue(session.ScheduleReveal("n_objective", 5f));
            Assert.IsFalse(session.ScheduleReveal("n_objective", 9f));
            Assert.AreEqual(1, session.RevealDueRooms(5f));
        }

        [Test]
        public void EnteringRoom_WithLegacyPendingReveal_RevealsImmediately()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());
            session.ScheduleReveal("n_objective", 100f);

            bool revealed = session.RevealRoom(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

            Assert.IsTrue(revealed);
            Assert.AreEqual("n_objective", session.CurrentRoomId);
            Assert.IsTrue(session.IsRoomRevealed("n_objective"));
            Assert.IsFalse(session.IsRevealPending("n_objective"));
        }

        [Test]
        public void EnteringRoom_WithoutPendingReveal_RevealsImmediately()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            Assert.IsTrue(session.RevealRoom("n_objective", 3f));

            Assert.IsTrue(session.IsRoomRevealed("n_objective"));
            Assert.IsTrue(session.IsRevealAnimating("n_objective"));
        }

        [Test]
        public void FindRoomBehindDoor_ResolvesBothSidesOfConnection()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            Assert.AreEqual("n_objective", session.FindRoomBehindDoor("n_start", "east"));
            Assert.AreEqual("n_start", session.FindRoomBehindDoor("n_objective", "west"));
            Assert.IsNull(session.FindRoomBehindDoor("n_start", "north"));
        }

        [Test]
        public void PersistableRevealedRooms_ExcludeRoomsSeenOnlyThroughAnOpenDoor()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());
            session.ScheduleReveal("n_objective", 1f);
            session.RevealDueRooms(1f);
            Assume.That(session.IsRoomRevealed("n_objective"));

            CollectionAssert.AreEqual(new[] { "n_start" }, session.GetPersistableRevealedRoomIds(),
                "Комната, увиденная через открытую дверь, не посещена: в чекпоинт она не идёт.");

            session.RevealRoom("n_objective");
            CollectionAssert.AreEqual(new[] { "n_start", "n_objective" }, session.GetPersistableRevealedRoomIds());
        }

        [Test]
        public void PersistableRevealedRooms_ExcludeLegacyPendingNeighbours()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());
            session.ScheduleReveal("n_objective", 100f);

            CollectionAssert.AreEqual(new[] { "n_start" }, session.GetPersistableRevealedRoomIds());
            CollectionAssert.DoesNotContain(VisibleRoomIds(session), "n_objective");
        }

        [Test]
        public void RevealAnimation_RunsForDelayedReveal_ButNotForCheckpointRestore()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());
            session.ScheduleReveal("n_objective", 5f);
            session.RevealDueRooms(5f);

            Assert.AreEqual(0.5f, session.GetRevealProgress("n_objective", 5.5f, 1f), 0.001f);
            Assert.AreEqual(0, session.PruneFinishedRevealAnimations(5.5f, 1f));
            Assert.IsTrue(session.IsRevealAnimating("n_objective"));
            Assert.AreEqual(1, session.PruneFinishedRevealAnimations(7f, 1f));
            Assert.IsFalse(session.IsRevealAnimating("n_objective"));
            Assert.AreEqual(1f, session.GetRevealProgress("n_start", 5.5f, 1f));

            session.RestoreRevealedRooms(new[] { "n_start", "n_objective", "n_exit" }, "n_exit");

            Assert.AreEqual(1f, session.GetRevealProgress("n_exit", 5.5f, 1f));
            Assert.AreEqual("n_exit", session.CurrentRoomId);
        }

        [Test]
        public void RestoreRevealedRooms_DropsLegacyPendingNeighbours()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());
            session.ScheduleReveal("n_exit", 100f);

            session.RestoreRevealedRooms(new[] { "n_start", "n_objective" }, "n_objective");

            Assert.IsFalse(session.HasPendingReveals);
            Assert.IsFalse(session.IsRoomRevealed("n_exit"));
            Assert.IsFalse(session.IsRevealAnimating("n_exit"));
        }

        [Test]
        public void RestoreRevealedRooms_ReplacesPreviousPreviewInsteadOfKeepingIt()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());
            session.RevealRoom("n_exit");

            session.RestoreRevealedRooms(new[] { "n_start", "n_objective" }, "n_objective");

            Assert.IsFalse(session.IsRoomRevealed("n_exit"),
                "откат чекпоинта не должен оставлять комнату, посещённую после сохранения");
            Assert.IsTrue(session.IsRoomRevealed("n_objective"));
        }

        [Test]
        public void RebindSameLayout_DropsLegacyPendingReveal()
        {
            LevelMinimapSession session = new();
            LevelLayout layout = CreateLayout();
            session.Bind(layout);
            session.ScheduleReveal("n_objective", 50f);

            session.Bind(layout, preserveRevealedRooms: true);

            Assert.IsFalse(session.IsRevealPending("n_objective"));
            Assert.IsFalse(session.IsRoomRevealed("n_objective"));
        }

        [Test]
        public void VisibleRooms_AreRebuiltOnRevealAndCurrentRoomChange_NotOnEveryCall()
        {
            LevelMinimapSession session = new();
            session.Bind(CreateLayout());

            IReadOnlyList<MinimapRoomSnapshot> first = session.GetVisibleRooms();
            IReadOnlyList<MinimapCellSnapshot> cells = first[0].Cells;
            Assert.AreSame(cells, session.GetVisibleRooms()[0].Cells);

            session.RevealRoom("n_objective");

            Assert.AreEqual(2, session.GetVisibleRooms().Count);
            Assert.IsTrue(session.GetVisibleRooms()[1].IsCurrent);
        }

        [Test]
        public void Controller_RestoreCheckpoint_RestoresRouteHintProgressAndRestartsIdleTimer()
        {
            GameObject go = new("MinimapControllerRouteRestoreTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
                controller.Bind(CreateLayout());
                controller.RouteHint.Tick(1000f, true);

                controller.RestoreCheckpoint(new[] { "n_start", "n_objective", "n_exit" }, "n_objective");

                Assert.AreEqual(0f, controller.RouteHint.IdleSeconds);
                Assert.AreEqual("n_objective", controller.RouteHint.Decision.TargetDoor.FromNodeId);
                Assert.AreEqual("n_exit", controller.RouteHint.Decision.TargetDoor.ToNodeId);
                Assert.IsFalse(controller.RouteHint.IsRoomVisited("n_exit"),
                    "раскрытая на карте дверь не означает посещение выходной комнаты");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_RestoreCheckpoint_InOptionalBranch_KeepsCompletedMainPathProgress()
        {
            LevelLayout layout = new(
                new[]
                {
                    Room("start", 0f, 0f), Room("mandatory", 2f, 0f),
                    Room("optional", 2f, 2f), Room("exit", 4f, 0f)
                },
                new[]
                {
                    new DoorConnection("start", "east", "mandatory", "west", true),
                    new DoorConnection("mandatory", "north", "optional", "south"),
                    new DoorConnection("mandatory", "east", "exit", "west", true)
                }, "start", new[] { "exit" });
            GameObject go = new("MinimapOptionalResumeTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
                controller.Bind(layout);

                controller.RestoreCheckpoint(new[] { "start", "mandatory", "optional", "exit" },
                    "optional", new[] { "mandatory" });

                Assert.IsTrue(controller.RouteHint.IsRoomVisited("mandatory"));
                Assert.IsTrue(controller.RouteHint.IsRoomVisited("optional"));
                Assert.IsFalse(controller.RouteHint.IsRoomVisited("exit"));
                Assert.AreEqual("exit", controller.RouteHint.Decision.TargetDoor.ToNodeId);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Controller_RestoreConfirmedVisitedCorridor_WhenSafeRoomIsOptional_PreservesRoute()
        {
            LevelLayout layout = new(
                new[]
                {
                    Room("start", 0f, 0f), Room("corridor", 2f, 0f),
                    Room("mandatory", 4f, 0f), Room("optional", 4f, 2f), Room("exit", 6f, 0f)
                },
                new[]
                {
                    new DoorConnection("start", "east", "corridor", "west", true),
                    new DoorConnection("corridor", "east", "mandatory", "west", true),
                    new DoorConnection("mandatory", "north", "optional", "south"),
                    new DoorConnection("mandatory", "east", "exit", "west", true)
                }, "start", new[] { "exit" });
            GameObject go = new("MinimapConfirmedCorridorResumeTest");
            try
            {
                LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
                controller.Bind(layout);

                controller.RestoreCheckpoint(new[] { "start", "corridor", "mandatory", "optional" },
                    "optional", new[] { "mandatory" }, visitedSnapshotConfirmed: true);

                Assert.IsTrue(controller.Session.IsRoomRevealed("corridor"));
                Assert.IsTrue(controller.RouteHint.IsRoomVisited("corridor"));
                Assert.AreEqual("exit", controller.RouteHint.Decision.TargetDoor.ToNodeId);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void SetSettings(LevelMinimapController controller, LevelMinimapSettings settings)
        {
            FieldInfo field = typeof(LevelMinimapController)
                .GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(controller, settings);
        }

        private static List<string> VisibleRoomIds(LevelMinimapSession session)
        {
            List<string> ids = new();
            IReadOnlyList<MinimapRoomSnapshot> rooms = session.GetVisibleRooms();
            for (int i = 0; i < rooms.Count; i++)
            {
                ids.Add(rooms[i].NodeId);
            }

            return ids;
        }

        private static LevelLayout CreateLayout()
        {
            return new LevelLayout(
                new[] { Room("n_start", 0, 0), Room("n_objective", 2, 0), Room("n_exit", 4, 0) },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west"), new DoorConnection("n_objective", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
        }

        private static PlacedRoom Room(
            string nodeId,
            float x,
            float y,
            RoomWorldPose? worldPose = null,
            RoomFootprint? footprint = null)
        {
            return new PlacedRoom(
                nodeId,
                $"template_{nodeId}",
                new GridPose(x, y, 0),
                new Dictionary<string, DoorState>(),
                worldPose,
                footprint);
        }

        private static bool ContainsRoom(
            IReadOnlyList<MinimapRoomSnapshot> rooms,
            string nodeId,
            bool isExit)
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].NodeId == nodeId && rooms[i].IsExit == isExit)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool WarmStaticMapCache(LevelMinimapController controller, Rect rect, bool fullMap)
        {
            MethodInfo method = typeof(LevelMinimapController).GetMethod("WarmStaticMapCache",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (bool)method.Invoke(controller, new object[] { rect, fullMap });
        }

        [Test]
        public void Controller_MapRotation_FollowsPlayerCameraLookYaw_NotRigYaw()
        {
            GameObject controllerGo = new("MinimapControllerTest");
            GameObject playerGo = new("MinimapPlayerLookTest");
            GameObject cameraGo = new("MinimapCameraLookTest");
            try
            {
                playerGo.transform.position = new Vector3(10f, 0f, 20f);
                playerGo.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
                cameraGo.transform.SetParent(playerGo.transform, false);
                cameraGo.transform.localPosition = new Vector3(0f, 1.5f, 0f);
                cameraGo.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                cameraGo.AddComponent<Camera>();

                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);

                Assert.AreEqual(12000, InvokeBuildProjectionPoseKeyYaw(controller));
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        [Test]
        public void Controller_ProjectionYaw_UsesCameraLookYaw_OnCompactAndFullMap()
        {
            GameObject controllerGo = new("MinimapControllerTest");
            GameObject playerGo = new("MinimapPlayerLookTest");
            GameObject cameraGo = new("MinimapCameraLookTest");
            try
            {
                playerGo.transform.position = Vector3.zero;
                playerGo.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
                cameraGo.transform.SetParent(playerGo.transform, false);
                cameraGo.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                cameraGo.AddComponent<Camera>();

                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);
                controller.Bind(CreateLayout());

                IReadOnlyList<MinimapRoomSnapshot> rooms = controller.Session.GetAllRooms();
                Rect rect = new(0f, 0f, 220f, 160f);
                Assert.AreEqual(120f, InvokeTryBuildProjectionYaw(controller, rect, false, rooms), 0.001f);
                Assert.AreEqual(120f, InvokeTryBuildProjectionYaw(controller, rect, true, rooms), 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        [Test]
        public void Controller_RotationDisabled_ProjectionYawIsZero()
        {
            GameObject controllerGo = new("MinimapControllerTest");
            GameObject playerGo = new("MinimapPlayerLookTest");
            GameObject cameraGo = new("MinimapCameraLookTest");
            try
            {
                playerGo.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
                cameraGo.transform.SetParent(playerGo.transform, false);
                cameraGo.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                cameraGo.AddComponent<Camera>();

                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);
                SetRotateWithPlayer(controller, false);

                Assert.AreEqual(0, InvokeBuildProjectionPoseKeyYaw(controller));
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        [Test]
        public void Controller_FullMap_FitsWholeRevealedExtentWhenLevelIsLargerThanViewport()
        {
            GameObject controllerGo = new("MinimapControllerTest");
            GameObject playerGo = new("MinimapPlayerTest");
            try
            {
                playerGo.transform.position = Vector3.zero;

                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);
                controller.Bind(CreateWideLayout());

                float compactScale = InvokeCompactMapScalePixelsPerMeter(controller);
                float fullScale = InvokeTryBuildProjectionScale(
                    controller,
                    new Rect(0f, 0f, 1136f, 680f),
                    true,
                    controller.Session.GetAllRooms());

                // Большую карту открывают, чтобы увидеть пройденное целиком: она вписывает весь
                // показанный набор комнат, поэтому на уровне шире вьюпорта её содержимое мельче
                // прежнего фиксированного зума.
                Assert.Less(
                    fullScale,
                    compactScale / LevelMinimapController.FullMapZoomDivider,
                    "Большая карта обязана вписать весь показанный уровень — иначе его края не видно, "
                    + "а рисунок мельчит одна далёкая комната вместо честного фита.");
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        [Test]
        public void Controller_FullMap_ZoomsIntoWholeLevelWhenItFitsAtCompactZoom()
        {
            GameObject controllerGo = new("MinimapControllerTest");
            GameObject playerGo = new("MinimapPlayerTest");
            try
            {
                playerGo.transform.position = Vector3.zero;

                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);
                controller.Bind(CreateSmallLayout());

                float compactScale = InvokeCompactMapScalePixelsPerMeter(controller);
                float fullScale = InvokeTryBuildProjectionScale(
                    controller,
                    new Rect(0f, 0f, 1136f, 680f),
                    true,
                    controller.Session.GetAllRooms());

                Assert.Greater(fullScale, compactScale);
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        private static LevelLayout CreateWideLayout()
        {
            return new LevelLayout(
                new[] { WorldRoom("n_start", 0f, 20f), WorldRoom("n_objective", 300f, 20f), WorldRoom("n_exit", -300f, -80f) },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west"), new DoorConnection("n_objective", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
        }

        /// <summary>
        /// Уровень, заведомо влезающий в большую карту с запасом: вписанный прямоугольник комнат
        /// с полями даёт увеличение, и кламп в три компактных масштаба его не режет. Комнаты стоят
        /// вплотную к старту, поэтому центр фита почти совпадает с игроком.
        /// </summary>
        private static LevelLayout CreateSmallLayout()
        {
            return new LevelLayout(
                new[] { WorldRoom("n_start", 0f, 0f), WorldRoom("n_objective", 10f, 0f), WorldRoom("n_exit", 20f, 0f) },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west"), new DoorConnection("n_objective", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
        }

        private static PlacedRoom WorldRoom(string nodeId, float x, float z) => Room(nodeId, 0f, 0f, new RoomWorldPose(x, 0f, z, 0f, 0f, 0f, 1f));

        private static void SetPlayerTransform(LevelMinimapController controller, Transform transform)
        {
            FieldInfo field = typeof(LevelMinimapController).GetField("playerTransform",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(controller, transform);
        }

        private static void SetRotateWithPlayer(LevelMinimapController controller, bool enabled)
        {
            FieldInfo field = typeof(LevelMinimapController).GetField("rotateWithPlayer",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(controller, enabled);
        }

        private static int InvokeBuildProjectionPoseKeyYaw(LevelMinimapController controller)
        {
            MethodInfo method = typeof(LevelMinimapController).GetMethod("BuildProjectionPoseKey",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            object poseKey = method.Invoke(controller, null);
            FieldInfo field = poseKey.GetType().GetField("_yawCentidegrees",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (int)field.GetValue(poseKey);
        }

        private static float InvokeTryBuildProjectionScale(
            LevelMinimapController controller,
            Rect rect,
            bool fullMap,
            IReadOnlyList<MinimapRoomSnapshot> rooms) =>
            InvokeTryBuildProjectionValue(controller, rect, fullMap, rooms, "Scale");

        private static float InvokeTryBuildProjectionYaw(
            LevelMinimapController controller,
            Rect rect,
            bool fullMap,
            IReadOnlyList<MinimapRoomSnapshot> rooms) =>
            InvokeTryBuildProjectionValue(controller, rect, fullMap, rooms, "YawDegrees");

        private static float InvokeTryBuildProjectionValue(
            LevelMinimapController controller,
            Rect rect,
            bool fullMap,
            IReadOnlyList<MinimapRoomSnapshot> rooms,
            string fieldName)
        {
            MethodInfo method = typeof(LevelMinimapController).GetMethod("TryBuildProjection",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            object[] arguments = new object[] { rooms, rect, fullMap, null };
            bool built = (bool)method.Invoke(controller, arguments);
            Assert.IsTrue(built);
            object projection = arguments[3];
            FieldInfo field = projection.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (float)field.GetValue(projection);
        }

        private static float InvokeCompactMapScalePixelsPerMeter(LevelMinimapController controller)
        {
            MethodInfo method = typeof(LevelMinimapController).GetMethod("CompactMapScalePixelsPerMeter",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (float)method.Invoke(controller, null);
        }
    }
}
