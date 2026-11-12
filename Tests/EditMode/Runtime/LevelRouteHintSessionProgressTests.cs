using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Runtime.Application;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Сессия подсказки: боковые комнаты, тупики и ветки неверных ответов не трогают таймер
    /// простоя и не гасят показанную подсказку; сбрасывают его только продвижение по пути,
    /// выполнение задания цели и возврат из чекпоинта.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelRouteHintSessionProgressTests
    {
        private const float Threshold = 60f;

        [Test]
        public void SideRoomVisit_KeepsIdleTimer_AndKeepsShownHint()
        {
            LevelRouteHintSession session = BindSession();
            session.Tick(Threshold, true);
            Assert.AreEqual(RouteHintOutcome.ShowHint, session.Decision.Outcome, "Precondition: the hint is shown.");

            bool progressed = session.MarkRoomVisited("side");

            Assert.IsFalse(progressed);
            Assert.AreEqual(Threshold, session.IdleSeconds, 0.001f);
            Assert.AreEqual(RouteHintOutcome.ShowHint, session.Decision.Outcome);
            Assert.AreEqual("quiz", session.Decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void WrongBranchVisit_KeepsIdleTimer_AndKeepsShownHint()
        {
            LevelRouteHintSession session = BindSession();
            session.MarkRoomVisited("quiz");
            session.Tick(Threshold, true);
            Assert.AreEqual(RouteHintOutcome.ShowHint, session.Decision.Outcome, "Precondition: the hint is shown.");
            Assert.AreEqual("correct", session.Decision.TargetDoor.ToNodeId);

            bool progressed = session.MarkRoomVisited("wrong");

            Assert.IsFalse(progressed);
            Assert.AreEqual(Threshold, session.IdleSeconds, 0.001f);
            Assert.AreEqual(RouteHintOutcome.ShowHint, session.Decision.Outcome);
            Assert.AreEqual("correct", session.Decision.TargetDoor.ToNodeId);
        }

        [Test]
        public void NextCriticalRoom_ResetsIdleTimer()
        {
            LevelRouteHintSession session = BindSession();
            session.Tick(Threshold, true);

            bool progressed = session.MarkRoomVisited("quiz");

            Assert.IsTrue(progressed);
            Assert.AreEqual(0f, session.IdleSeconds);
            Assert.AreEqual(RouteHintOutcome.Waiting, session.Decision.Outcome);
        }

        [Test]
        public void TargetTaskCompleted_ResetsIdleTimer_OtherTaskKeepsIt()
        {
            LevelRouteHintSession session = BindSession();
            session.Tick(Threshold, true);

            RouteHintProgress other = session.NoteTargetTaskCompleted(false);

            Assert.AreEqual(RouteHintProgress.NoProgress, other);
            Assert.AreEqual(Threshold, session.IdleSeconds, 0.001f);

            RouteHintProgress verdict = session.NoteTargetTaskCompleted(true);

            Assert.AreEqual(RouteHintProgress.ProgressAdvanced, verdict);
            Assert.AreEqual(0f, session.IdleSeconds);
        }

        [Test]
        public void SideRoomVisit_AfterRouteComplete_IsNotProgress()
        {
            LevelRouteHintSession session = BindSession();
            session.MarkRoomVisited("quiz");
            session.MarkRoomVisited("correct");
            session.MarkRoomVisited("exit");
            Assert.AreEqual(RouteHintOutcome.RouteComplete, session.Decision.Outcome,
                "Precondition: the route is complete.");

            bool progressed = session.MarkRoomVisited("side");

            Assert.IsFalse(progressed, "After the route is complete there is nothing to advance to.");
        }

        [Test]
        public void TaskCompletion_WithoutActiveLevel_FailsClosed()
        {
            LevelRouteHintSession session = new();

            Assert.AreEqual(RouteHintProgress.NoProgress, session.NoteTargetTaskCompleted(true));
        }

        private static LevelRouteHintSession BindSession()
        {
            LevelRouteHintSession session = new() { IdleThresholdSeconds = Threshold };
            session.Bind(new LevelLayout(
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
                new[] { "exit" }));
            return session;
        }

        private static PlacedRoom Room(string nodeId)
        {
            return new PlacedRoom(nodeId, $"template_{nodeId}", new GridPose(0f, 0f, 0),
                new Dictionary<string, DoorState>());
        }
    }
}
