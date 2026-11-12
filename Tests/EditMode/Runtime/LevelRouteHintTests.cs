using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Runtime.Application;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Критический путь, решение «куда и когда подсказывать» и таймер простоя. Уровень-образец:
    /// старт → развилка-вопрос → правильная ветка → выход; у развилки тупик неправильного ответа,
    /// у старта — боковой тупик.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelRouteHintTests
    {
        private const float Threshold = 60f;

        [Test]
        public void CriticalPath_FollowsMainPathConnections_AndSkipsDeadEnds()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());

            CollectionAssert.AreEqual(new[] { "start", "quiz", "correct", "exit" }, path.Nodes);
            Assert.AreEqual(3, path.Doors.Count);
            Assert.AreEqual(new CriticalPathDoor("start", "s_n", "quiz", "q_s"), path.Doors[0]);
        }

        [Test]
        public void CriticalPath_OrientsDoorsAlongTheRoute_EvenWhenConnectionIsStoredBackwards()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());

            CriticalPathDoor lastDoor = path.Doors[2];
            Assert.AreEqual("correct", lastDoor.FromNodeId);
            Assert.AreEqual("c_n", lastDoor.FromSocketId);
            Assert.AreEqual("exit", lastDoor.ToNodeId);
            Assert.AreEqual("e_s", lastDoor.ToSocketId);
        }

        [Test]
        public void CriticalPath_WithoutMainPathFlags_FallsBackToShortestRouteOverAllConnections()
        {
            LevelLayout layout = new(
                new[] { Room("a"), Room("b"), Room("side"), Room("z") },
                new[]
                {
                    new DoorConnection("a", "a1", "side", "s1"),
                    new DoorConnection("a", "a2", "b", "b1"),
                    new DoorConnection("b", "b2", "z", "z1")
                },
                "a",
                new[] { "z" });

            LevelCriticalPath path = LevelCriticalPath.Build(layout);

            CollectionAssert.AreEqual(new[] { "a", "b", "z" }, path.Nodes);
        }

        [Test]
        public void CriticalPath_WithoutReachableExit_IsEmpty()
        {
            LevelLayout layout = new(
                new[] { Room("a"), Room("z") },
                System.Array.Empty<DoorConnection>(),
                "a",
                new[] { "z" });

            Assert.IsTrue(LevelCriticalPath.Build(layout).IsEmpty);
        }

        [Test]
        public void Decide_TargetsDoorIntoNextUnopenedCriticalRoom_AndWaitsBeforeThreshold()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());

            RouteHintDecision decision = DecideRouteHintUseCase.Execute(
                path, new HashSet<string> { "start" }, Threshold - 1f, Threshold, false);

            Assert.AreEqual(RouteHintOutcome.Waiting, decision.Outcome);
            Assert.AreEqual("start", decision.TargetDoor.FromNodeId);
            Assert.AreEqual("quiz", decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Decide_ShowsHintOnceIdleReachesThreshold_OrWhenForced()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());
            HashSet<string> opened = new() { "start" };

            Assert.AreEqual(RouteHintOutcome.ShowHint,
                DecideRouteHintUseCase.Execute(path, opened, Threshold, Threshold, false).Outcome);
            Assert.AreEqual(RouteHintOutcome.ShowHint,
                DecideRouteHintUseCase.Execute(path, opened, 0f, Threshold, true).Outcome);
        }

        [Test]
        public void Decide_OpenedDeadEnds_DoNotMoveTheTarget()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());

            RouteHintDecision decision = DecideRouteHintUseCase.Execute(
                path, new HashSet<string> { "start", "side", "quiz", "wrong" }, 0f, Threshold, false);

            Assert.AreEqual("quiz", decision.TargetDoor.FromNodeId);
            Assert.AreEqual("correct", decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Decide_WhenEveryCriticalRoomIsOpened_RouteIsComplete()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());

            RouteHintDecision decision = DecideRouteHintUseCase.Execute(
                path, new HashSet<string> { "start", "quiz", "correct", "exit" }, Threshold * 2f, Threshold, true);

            Assert.AreEqual(RouteHintOutcome.RouteComplete, decision.Outcome);
            Assert.IsFalse(decision.ShowHint);
        }

        [Test]
        public void Decide_WithoutCriticalPath_ReportsContentDefectInsteadOfHint()
        {
            RouteHintDecision decision = DecideRouteHintUseCase.Execute(
                LevelCriticalPath.Empty, new HashSet<string>(), Threshold, Threshold, true);

            Assert.AreEqual(RouteHintOutcome.NoCriticalPath, decision.Outcome);
            Assert.IsFalse(decision.ShowHint);
        }

        [Test]
        public void Session_IdleTimer_RunsOnlyWhilePlayerIsInControl()
        {
            LevelRouteHintSession session = BindSession();

            session.Tick(30f, false);
            Assert.AreEqual(0f, session.IdleSeconds);

            session.Tick(59f, true);
            Assert.AreEqual(RouteHintOutcome.Waiting, session.Decision.Outcome);

            session.Tick(1f, true);
            Assert.AreEqual(RouteHintOutcome.ShowHint, session.Decision.Outcome);
        }

        [Test]
        public void Session_OrdinaryRooms_ShowsNextDoorAfterIdleWithoutInteractiveTask()
        {
            LevelLayout layout = new(
                new[] { Room("start"), Room("ordinary1"), Room("ordinary2"), Room("exit") },
                new[]
                {
                    new DoorConnection("start", "s", "ordinary1", "a", true),
                    new DoorConnection("ordinary1", "b", "ordinary2", "c", true),
                    new DoorConnection("ordinary2", "d", "exit", "e", true)
                }, "start", new[] { "exit" });
            LevelRouteHintSession session = new();
            session.Bind(layout);
            session.MarkRoomVisited("ordinary1");

            session.Tick(session.IdleThresholdSeconds, true);

            Assert.AreEqual(RouteHintOutcome.ShowHint, session.Decision.Outcome);
            Assert.AreEqual("ordinary2", session.Decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Session_ProgressOnCriticalPath_ResetsTimer_AndHidesHint()
        {
            LevelRouteHintSession session = BindSession();
            session.Tick(Threshold, true);
            int versionBefore = session.TargetVersion;

            bool progressed = session.MarkRoomVisited("quiz");

            Assert.IsTrue(progressed);
            Assert.AreEqual(0f, session.IdleSeconds);
            Assert.AreEqual(RouteHintOutcome.Waiting, session.Decision.Outcome);
            Assert.AreEqual("correct", session.Decision.TargetDoor.ToNodeId);
            Assert.AreNotEqual(versionBefore, session.TargetVersion);
        }

        [Test]
        public void Session_NewSideRoom_KeepsTimerWithoutAdvancingCriticalPath()
        {
            LevelRouteHintSession session = BindSession();
            session.MarkRoomVisited("quiz");
            session.Tick(40f, true);

            bool progressed = session.MarkRoomVisited("wrong");

            Assert.IsFalse(progressed);
            Assert.AreEqual(40f, session.IdleSeconds, 0.001f);
            Assert.AreEqual("correct", session.Decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Session_RestoreFromCheckpoint_KeepsProgress_AndRestartsTimer()
        {
            LevelRouteHintSession session = BindSession();
            session.Tick(Threshold, true);

            session.RestoreVisitedRooms(new[] { "start", "quiz", "correct" });

            Assert.AreEqual(0f, session.IdleSeconds);
            Assert.AreEqual("exit", session.Decision.TargetDoor.ToNodeId);
            Assert.AreEqual(RouteHintOutcome.Waiting, session.Decision.Outcome);
        }

        [Test]
        public void Session_Rebind_ForNewLevel_ForgetsOpenedRooms()
        {
            LevelRouteHintSession session = BindSession();
            session.MarkRoomVisited("quiz");

            session.Bind(CreateMazeLayout());

            Assert.IsFalse(session.IsRoomVisited("quiz"));
            Assert.AreEqual("quiz", session.Decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Session_WhenRouteIsComplete_TimerDoesNotRun()
        {
            LevelRouteHintSession session = BindSession();
            session.RestoreVisitedRooms(new[] { "quiz", "correct", "exit" });

            session.Tick(Threshold * 3f, true);

            Assert.AreEqual(0f, session.IdleSeconds);
            Assert.AreEqual(RouteHintOutcome.RouteComplete, session.Decision.Outcome);
        }

        [Test]
        public void Decide_UnsolvedQuizFork_LeadsToNextCriticalDoor()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());
            HashSet<string> opened = new() { "start", "quiz" };

            RouteHintDecision decision = DecideRouteHintUseCase.Execute(
                path, opened, Threshold, Threshold, false);

            Assert.AreEqual(RouteHintOutcome.ShowHint, decision.Outcome);
            Assert.AreEqual("correct", decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Decide_WrongAnswerDeadEnd_StillTargetsNextCriticalDoor()
        {
            LevelCriticalPath path = LevelCriticalPath.Build(CreateMazeLayout());
            HashSet<string> opened = new() { "start", "quiz", "wrong" };

            RouteHintDecision decision = DecideRouteHintUseCase.Execute(
                path, opened, Threshold, Threshold, false);

            Assert.AreEqual("correct", decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Session_RestoreVisitedRooms_ReplacesTheSet_InsteadOfAppending()
        {
            LevelRouteHintSession session = BindSession();
            session.MarkRoomVisited("quiz");
            session.MarkRoomVisited("correct");

            session.RestoreVisitedRooms(new[] { "start" });

            Assert.IsFalse(session.IsRoomVisited("correct"));
            Assert.AreEqual("quiz", session.Decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void Session_BindNewLevel_DropsForcedShow_ButRebindOfSameLevelKeepsIt()
        {
            LevelRouteHintSession session = BindSession();
            LevelLayout layout = CreateMazeLayout();
            session.Bind(layout);
            session.SetForced(true);

            session.Bind(layout, true);
            Assert.IsTrue(session.IsForced);

            session.Bind(CreateMazeLayout());
            Assert.IsFalse(session.IsForced);
        }

        private static LevelRouteHintSession BindSession()
        {
            LevelRouteHintSession session = new() { IdleThresholdSeconds = Threshold };
            session.Bind(CreateMazeLayout());
            return session;
        }

        private static LevelLayout CreateMazeLayout()
        {
            return new LevelLayout(
                new[] { Room("start"), Room("side"), Room("quiz"), Room("wrong"), Room("correct"), Room("exit") },
                new[]
                {
                    new DoorConnection("start", "s_e", "side", "side_w"),
                    new DoorConnection("start", "s_n", "quiz", "q_s", true),
                    new DoorConnection("quiz", "q_w", "wrong", "w_e"),
                    new DoorConnection("quiz", "q_n", "correct", "c_s", true),
                    new DoorConnection("exit", "e_s", "correct", "c_n", true)
                },
                "start",
                new[] { "exit" });
        }

        private static PlacedRoom Room(string nodeId)
        {
            return new PlacedRoom(nodeId, $"template_{nodeId}", new GridPose(0f, 0f, 0),
                new Dictionary<string, DoorState>());
        }
    }
}
