using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Runtime.Application;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// Прогресс подсказки маршрута: сброс таймера простоя — только за продвижение по критическому
    /// пути, выполнение задания цели или завершение маршрута. Боковые комнаты, тупики, ветки
    /// неверных ответов, повторные визиты и открытия дверей — не прогресс.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class DecideRouteHintProgressUseCaseTests
    {
        [Test]
        public void NextCriticalRoom_IsProgress()
        {
            LevelCriticalPath path = MazePath();

            RouteHintProgress verdict = DecideRouteHintProgressUseCase.Execute(
                path, 0, new HashSet<string> { "start", "quiz" });

            Assert.AreEqual(RouteHintProgress.ProgressAdvanced, verdict);
        }

        [Test]
        public void SideRoom_IsNoProgress()
        {
            LevelCriticalPath path = MazePath();

            RouteHintProgress verdict = DecideRouteHintProgressUseCase.Execute(
                path, 0, new HashSet<string> { "start", "side" });

            Assert.AreEqual(RouteHintProgress.NoProgress, verdict);
        }

        [Test]
        public void WrongAnswerBranch_IsNoProgress()
        {
            LevelCriticalPath path = MazePath();

            RouteHintProgress verdict = DecideRouteHintProgressUseCase.Execute(
                path, 1, new HashSet<string> { "start", "quiz", "wrong" });

            Assert.AreEqual(RouteHintProgress.NoProgress, verdict);
        }

        [Test]
        public void Revisit_WithoutNewRooms_IsNoProgress()
        {
            LevelCriticalPath path = MazePath();

            RouteHintProgress verdict = DecideRouteHintProgressUseCase.Execute(
                path, 1, new HashSet<string> { "start", "quiz" });

            Assert.AreEqual(RouteHintProgress.NoProgress, verdict);
        }

        [Test]
        public void AlreadyAhead_IsNoProgress()
        {
            LevelCriticalPath path = MazePath();

            RouteHintProgress verdict = DecideRouteHintProgressUseCase.Execute(
                path, 1, new HashSet<string> { "start" });

            Assert.AreEqual(RouteHintProgress.NoProgress, verdict);
        }

        [Test]
        public void ExitRoom_IsRouteComplete()
        {
            LevelCriticalPath path = MazePath();

            RouteHintProgress verdict = DecideRouteHintProgressUseCase.Execute(
                path, 2, new HashSet<string> { "start", "quiz", "correct", "exit" });

            Assert.AreEqual(RouteHintProgress.RouteComplete, verdict);
        }

        [Test]
        public void MissingPath_FailsClosedWithoutProgress()
        {
            RouteHintProgress verdict = DecideRouteHintProgressUseCase.Execute(
                LevelCriticalPath.Empty, 0, new HashSet<string> { "start" });

            Assert.AreEqual(RouteHintProgress.NoProgress, verdict);
        }

        [Test]
        public void TargetTaskCompleted_IsProgress_OtherTaskIsNot()
        {
            Assert.AreEqual(
                RouteHintProgress.ProgressAdvanced,
                DecideRouteHintProgressUseCase.ExecuteTaskCompleted(true));
            Assert.AreEqual(
                RouteHintProgress.NoProgress,
                DecideRouteHintProgressUseCase.ExecuteTaskCompleted(false));
        }

        private static LevelCriticalPath MazePath()
        {
            LevelLayout layout = new(
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
            LevelCriticalPath path = LevelCriticalPath.Build(layout);
            Assert.AreEqual(new[] { "start", "quiz", "correct", "exit" }, path.Nodes);
            return path;
        }

        private static PlacedRoom Room(string nodeId)
        {
            return new PlacedRoom(nodeId, $"template_{nodeId}", new GridPose(0f, 0f, 0),
                new Dictionary<string, DoorState>());
        }
    }
}
