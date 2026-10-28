using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Generation.Infrastructure;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class MissionGeneratorTests
    {
        [Test]
        public void BuildLayout_ThrowsWhenTilesetMissing()
        {
            SequenceRngFactory rngFactory = new();
            StaticGraphBuilder graphBuilder = new(TestGraph());
            StaticRoomSelector roomSelector = new();
            StaticRoomPlacer roomPlacer = new();
            StaticDoorConnector doorConnector = new();
            SequenceValidator validator = new(new LevelValidationResult[] { LevelValidationResult.Empty });
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[0]);
            MissingTilesetCatalog tilesetCatalog = new();

            MissionGenerator generator = new(
                rngFactory,
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => generator.BuildLayout(new FakeMissionConfig(), 17));
            Assert.AreEqual("Tileset 'test' not found.", ex.Message);
        }

        [Test]
        public void BuildLayout_ReturnsConnectedLayoutForValidLayout()
        {
            SequenceRngFactory rngFactory = new();
            LevelGraph graph = TestGraph();
            StaticGraphBuilder graphBuilder = new(graph);
            StaticRoomSelector roomSelector = new(StaticSelection());
            StaticRoomPlacer roomPlacer = new();
            LevelLayout expectedLayout = BuildLayoutFromSelections(graph, roomSelector.Selections, true);
            StaticDoorConnector doorConnector = new(expectedLayout);
            SequenceValidator validator = new(LevelValidationResult.Empty);
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) });
            FakeTilesetCatalog tilesetCatalog = new();

            MissionGenerator generator = new(
                rngFactory,
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            LevelLayout layout = generator.BuildLayout(new FakeMissionConfig(), 7);

            Assert.AreSame(expectedLayout, layout);
            Assert.AreEqual(1, graphBuilder.CallCount);
            Assert.AreEqual(graph.Nodes.Count, roomSelector.CallCount);
            Assert.AreEqual(1, roomPlacer.CallCount);
            Assert.AreEqual(1, doorConnector.CallCount);
            Assert.AreEqual(1, validator.CallCount);
            Assert.AreEqual(1, rngFactory.CreatedSeeds.Count);
            Assert.AreEqual(7, rngFactory.CreatedSeeds[0]);
        }

        [Test]
        public async Task BuildLayoutCooperativeAsync_YieldsBeforeCompletingAndReturnsValidLayout()
        {
            SequenceRngFactory rngFactory = new();
            LevelGraph graph = TestGraph();
            StaticGraphBuilder graphBuilder = new(graph);
            StaticRoomSelector roomSelector = new(StaticSelection());
            StaticRoomPlacer roomPlacer = new();
            LevelLayout expectedLayout = BuildLayoutFromSelections(graph, roomSelector.Selections, true);
            StaticDoorConnector doorConnector = new(expectedLayout);
            SequenceValidator validator = new(LevelValidationResult.Empty);
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) });
            FakeTilesetCatalog tilesetCatalog = new();

            MissionGenerator generator = new(
                rngFactory,
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            Task<LevelLayout> task = generator.BuildLayoutCooperativeAsync(new FakeMissionConfig(), 7);

            Assert.IsFalse(
                task.IsCompleted,
                "Cooperative generation must return control before the layout is fully built.");

            LevelLayout layout = await task;

            Assert.AreSame(expectedLayout, layout);
            Assert.AreEqual(1, graphBuilder.CallCount);
            Assert.AreEqual(graph.Nodes.Count, roomSelector.CallCount);
            Assert.AreEqual(1, roomPlacer.CallCount);
            Assert.AreEqual(1, doorConnector.CallCount);
            Assert.AreEqual(1, validator.CallCount);
            Assert.AreEqual(1, rngFactory.CreatedSeeds.Count);
            Assert.AreEqual(7, rngFactory.CreatedSeeds[0]);
        }

        [Test]
        public void BuildLayout_UsesDependencyHintsToReplaceSourceSelection()
        {
            LevelGraph graph = TestGraph();
            FakeRoomTemplate sourceRoom = new("source_room", RoomKind.Start);
            FakeRoomTemplate targetRoom = new("target_room", RoomKind.Interactive);
            StaticRoomSelector roomSelector = new(new Dictionary<string, IRoomTemplateDescriptor> { ["n_start"] = targetRoom, ["n_objective"] = targetRoom });
            StaticGraphBuilder graphBuilder = new(graph);
            StaticRoomPlacer roomPlacer = new();
            StaticDoorConnector doorConnector = new();
            SequenceValidator validator = new(LevelValidationResult.Empty);
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[] { sourceRoom, targetRoom });
            FakeTilesetCatalog tilesetCatalog = new();
            IRoomDependencyHintExtractor[] hints = new IRoomDependencyHintExtractor[] { new HintExtractor(new RoomDependencyHint("source_room", "target_room", 0, 2)) };

            MissionGenerator generator = new(
                new SingleRngFactory(),
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard,
                hints);

            LevelLayout layout = generator.BuildLayout(new FakeMissionConfig(), 10);

            PlacedRoom startRoom = FindRoom(layout, "n_start");
            Assert.AreEqual("source_room", startRoom.RoomTemplateId);
            PlacedRoom objective = FindRoom(layout, "n_objective");
            Assert.AreEqual("target_room", objective.RoomTemplateId);
            Assert.AreEqual("source_room", doorConnector.LastSelections["n_start"].RoomId);
            Assert.AreEqual("target_room", doorConnector.LastSelections["n_objective"].RoomId);
        }

        [Test]
        public void BuildLayout_FiltersUnsupportedInteractiveContentAndRecordsWarnings()
        {
            LevelGraph graph = TestGraph();
            CapturingGraphBuilder graphBuilder = new(graph);
            StaticRoomSelector roomSelector = new(StaticSelection());
            StaticRoomPlacer roomPlacer = new();
            StaticDoorConnector doorConnector = new();
            SequenceValidator validator = new(LevelValidationResult.Empty);
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "password_room",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.PasswordDoor,
                    doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("locked", initialState: DoorState.Locked) })
            });
            FakeTilesetCatalog tilesetCatalog = new();
            InteractiveContentMissionConfig config = new(
                new InteractiveContent("Q_PASSWORD", TestInteractiveRoomKinds.PasswordDoor, 1, 1),
                new InteractiveContent("Q_LEVER", TestInteractiveRoomKinds.LeverPanel, 4, 2, isMandatory: false));

            MissionGenerator generator = new(
                new SingleRngFactory(),
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            generator.BuildLayout(config, 12);

            Assert.AreEqual(1, graphBuilder.LastInteractiveContent.Count);
            Assert.AreEqual("Q_PASSWORD", graphBuilder.LastInteractiveContent[0].ContentId);
            Assert.AreEqual(1, generator.LastWarnings.Count);
            StringAssert.Contains("Q_LEVER", generator.LastWarnings[0]);
            StringAssert.Contains("LeverPanel", generator.LastWarnings[0]);
        }

        [Test]
        public void BuildLayout_CountsSupportedInteractiveContentOnTopOfBaseRoomRangeByDefault()
        {
            LevelGraph graph = TestGraph();
            CapturingGraphBuilder graphBuilder = new(graph);
            StaticRoomSelector roomSelector = new(StaticSelection());
            StaticRoomPlacer roomPlacer = new();
            StaticDoorConnector doorConnector = new();
            SequenceValidator validator = new(LevelValidationResult.Empty);
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "password_room",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.PasswordDoor,
                    doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("locked", initialState: DoorState.Locked) })
            });
            FakeTilesetCatalog tilesetCatalog = new();
            InteractiveContentMissionConfig config = new(
                new InteractiveContent("Q_PASSWORD_1", TestInteractiveRoomKinds.PasswordDoor, 1, 1),
                new InteractiveContent("Q_PASSWORD_2", TestInteractiveRoomKinds.PasswordDoor, 1, 1));

            MissionGenerator generator = new(
                new SingleRngFactory(),
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            generator.BuildLayout(config, 12);

            Assert.IsTrue(graphBuilder.LastTotalRoomsRange.IsSet);
            Assert.AreEqual(3, graphBuilder.LastTotalRoomsRange.Min);
            Assert.AreEqual(12, graphBuilder.LastTotalRoomsRange.Max);
        }

        [Test]
        public void BuildLayout_RetriesUntilFirstValidLayoutAndReturnsIt()
        {
            SequenceRngFactory rngFactory = new();
            LevelGraph graph = TestGraph();
            StaticGraphBuilder graphBuilder = new(graph);
            StaticRoomSelector roomSelector = new(StaticSelection());
            StaticRoomPlacer roomPlacer = new();
            LevelLayout expectedLayout = BuildLayoutFromSelections(graph, roomSelector.Selections, true);
            StaticDoorConnector doorConnector = new(expectedLayout);
            SequenceValidator validator = new(
                new LevelValidationResult[] { ValidationError("Transient"), LevelValidationResult.Empty });
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) });
            FakeTilesetCatalog tilesetCatalog = new();

            MissionGenerator generator = new(
                rngFactory,
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            LevelLayout layout = generator.BuildLayout(new FakeMissionConfig(), 9);

            Assert.AreSame(expectedLayout, layout);
            Assert.AreEqual(2, graphBuilder.CallCount);
            Assert.AreEqual(graph.Nodes.Count * 2, roomSelector.CallCount);
            Assert.AreEqual(2, roomPlacer.CallCount);
            Assert.AreEqual(2, doorConnector.CallCount);
            Assert.AreEqual(2, validator.CallCount);
            Assert.AreEqual(2, rngFactory.CreatedSeeds.Count);
            Assert.AreEqual(9, rngFactory.CreatedSeeds[0]);
            Assert.AreEqual(10, rngFactory.CreatedSeeds[1]);
        }

        [Test]
        public void BuildLayout_AcceptsMazeBranchWrongAnswersWithoutDeadEnds_NoRetry()
        {
            SequenceRngFactory rngFactory = new();
            LevelGraph graph = MazeBranchGraph();
            StaticGraphBuilder graphBuilder = new(graph);
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections = MazeBranchSelections();
            StaticRoomSelector roomSelector = new(selections);
            StaticRoomPlacer roomPlacer = new();
            LevelLayout invalidLayout = BuildMazeBranchLayout(graph, selections, false);
            LevelLayout validLayout = BuildMazeBranchLayout(graph, selections, true);
            SequencedDoorConnector doorConnector = new(invalidLayout, validLayout);
            SequenceValidator validator = new(LevelValidationResult.Empty);
            FakeRoomDatabase roomDatabase = new(new[] { selections["n_quiz"] });
            FakeTilesetCatalog tilesetCatalog = new();
            InteractiveContentMissionConfig config = new(
                new InteractiveContent("Q_MAZE", TestInteractiveRoomKinds.MazeBranch, 3, 1));

            MissionGenerator generator = new(
                rngFactory,
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            LevelLayout layout = generator.BuildLayout(config, 21);

            // Maze wrong-answer dead-ends are best-effort + non-fatal: a cap that does not fit without overlap is not
            // placed (never force-placed), its door is sealed, and the leftover topology gap is a WARNING, not a
            // seed-rejecting error. So the generator does not retry when the first layout's wrong answers are not
            // dead-ends — it accepts the first layout and everything runs exactly once on seed 21.
            Assert.IsNotNull(layout);
            Assert.AreEqual(1, graphBuilder.CallCount);
            Assert.AreEqual(graph.Nodes.Count, roomSelector.CallCount);
            Assert.AreEqual(1, roomPlacer.CallCount);
            Assert.AreEqual(1, doorConnector.CallCount);
            Assert.AreEqual(1, validator.CallCount);
            Assert.AreEqual(1, rngFactory.CreatedSeeds.Count);
            Assert.AreEqual(21, rngFactory.CreatedSeeds[0]);
            Assert.AreSame(graph, generator.LastGraph);
            Assert.AreEqual(21, generator.LastSuccessfulSeed);
        }

        [Test]
        public void BuildLayout_ThrowsAfterMaxRetries()
        {
            LevelGraph graph = TestGraph();
            StaticGraphBuilder graphBuilder = new(graph);
            StaticRoomSelector roomSelector = new(StaticSelection());
            StaticRoomPlacer roomPlacer = new();
            StaticDoorConnector doorConnector = new();
            SequenceValidator validator = new(ValidationError("NoPath"));
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) });
            FakeTilesetCatalog tilesetCatalog = new();
            SequenceRngFactory rngFactory = new();

            MissionGenerator generator = new(
                rngFactory,
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => generator.BuildLayout(new FakeMissionConfig(), 11));
            Assert.IsTrue(ex.Message.StartsWith("Generator exhausted 48 attempts."));
            Assert.IsTrue(ex.Message.Contains("Last seed=58"));
            Assert.IsTrue(ex.Message.Contains("Error:NoPath:No valid layout"));
            Assert.IsTrue(ex.Message.Contains("Debug:"));
            Assert.AreEqual(48, generator.LastDebugCounters.Attempts);
            Assert.AreEqual(58, generator.LastDebugCounters.LastAttemptSeed);
            Assert.AreEqual("validate-layout", generator.LastDebugCounters.LastStage);
            Assert.AreEqual(48, graphBuilder.CallCount);
            Assert.AreEqual(48, validator.CallCount);
        }

        [Test]
        public void BuildLayout_TracksLastGraphAndLastSuccessfulSeedAcrossAttempts()
        {
            LevelGraph initialGraph = TestGraph();
            LevelGraph replacementGraph = TestGraph();
            AlternatingGraphBuilder graphBuilder = new(initialGraph, replacementGraph);
            StaticRoomSelector roomSelector = new(StaticSelection());
            StaticRoomPlacer roomPlacer = new();
            StaticDoorConnector doorConnector = new();
            SequenceValidator validator = new(ValidationError("NoPath"), LevelValidationResult.Empty);
            FakeRoomDatabase roomDatabase = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) });
            FakeTilesetCatalog tilesetCatalog = new();
            SequenceRngFactory rngFactory = new();

            MissionGenerator generator = new(
                rngFactory,
                graphBuilder,
                roomSelector,
                roomPlacer,
                doorConnector,
                validator,
                roomDatabase,
                tilesetCatalog,
                TestInteractiveRoomKinds.Standard);

            LevelLayout layout = generator.BuildLayout(new FakeMissionConfig(), 9);

            Assert.AreSame(replacementGraph, graphBuilder.LastGraph);
            Assert.AreSame(replacementGraph, generator.LastGraph);
            Assert.AreEqual(2, graphBuilder.CallCount);
            Assert.AreEqual(10, generator.LastSuccessfulSeed);
            Assert.AreEqual(2, generator.LastDebugCounters.Attempts);
            Assert.AreEqual(10, generator.LastDebugCounters.LastAttemptSeed);
            Assert.AreEqual("validate-layout", generator.LastDebugCounters.LastStage);
            Assert.IsNotNull(layout);
            Assert.AreEqual(replacementGraph.Nodes.Count * 2, roomSelector.CallCount);
        }

        [Test]
        public void CreateInteractiveContentConfig_FiltersUnsupportedInteractiveContent()
        {
            List<string> warnings = new();
            IMissionConfigView wrappedConfig = MissionGenerator.CreateInteractiveContentConfig(
                new InteractiveContentMissionConfig(
                    new InteractiveContent("Q_PASSWORD", TestInteractiveRoomKinds.PasswordDoor, 1, 1),
                    new InteractiveContent("Q_UNKNOWN", TestInteractiveRoomKinds.LeverPanel, 3, 1, isMandatory: false)),
                new FakeTileset(),
                new FakeRoomDatabase(new[]
                {
                    new FakeRoomTemplate(
                        "password_room",
                        RoomKind.Interactive,
                        interactiveKind: TestInteractiveRoomKinds.PasswordDoor,
                        doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("locked", initialState: DoorState.Locked) })
                }),
                TestInteractiveRoomKinds.Standard,
                warnings);

            Assert.AreEqual(1, warnings.Count);
            Assert.AreEqual(1, (wrappedConfig as IInteractiveMissionContentProvider)?.InteractiveContent.Count);
            Assert.AreEqual("Q_PASSWORD",
                (wrappedConfig as IInteractiveMissionContentProvider)?.InteractiveContent[0].ContentId);
            StringAssert.Contains("Q_UNKNOWN", warnings[0]);
        }

        [Test]
        public async Task BuildLayout_SameSeedProducesSameLayout_RegardlessOfTimeBudgetAndMultiplier()
        {
            // Размещение сида 31 «тратит» 1.1 с по часам прогона (подставные часы — без реального сна). Раньше лимит
            // стадии размещения (max(500 мс, 0.4 бюджета)) при бюджете 2 с обрывал его и генератор уходил на сид 32, а
            // при множителе ×10 тот же сид успевал. Теперь часы только прерывают прогон — исход сида одинаков в
            // синхронном и кооперативном пути при любом бюджете и множителе.
            const int seed = 31;
            (int successfulSeed, string fingerprint) syncTight = await BuildWithVirtualClockAsync(seed, 2, 1f, false);
            (int successfulSeed, string fingerprint) syncGenerous = await BuildWithVirtualClockAsync(seed, 2, 10f, false);
            (int successfulSeed, string fingerprint) coopTight = await BuildWithVirtualClockAsync(seed, 2, 1f, true);
            (int successfulSeed, string fingerprint) coopGenerous = await BuildWithVirtualClockAsync(seed, 30, 1f, true);

            Assert.AreEqual(seed, syncTight.successfulSeed,
                "A slow but valid seed must not be skipped because of a placement stage time cap.");
            Assert.AreEqual(seed, syncGenerous.successfulSeed);
            Assert.AreEqual(seed, coopTight.successfulSeed);
            Assert.AreEqual(seed, coopGenerous.successfulSeed);
            Assert.AreEqual(syncTight.fingerprint, syncGenerous.fingerprint);
            Assert.AreEqual(syncTight.fingerprint, coopTight.fingerprint);
            Assert.AreEqual(syncTight.fingerprint, coopGenerous.fingerprint);
        }

        [Test]
        public async Task BuildLayout_RunOverItsTimeBudget_AbortsWithTimeout_InsteadOfSwitchingSeed()
        {
            // Тот же сид при бюджете 1 с: часы прогона только прерывают его TimeoutException — ни синхронный, ни
            // кооперативный путь не подменяет раскладку раскладкой другого сида.
            // Тест асинхронный, а не Assert.ThrowsAsync: тот синхронно ждёт задачу на главном потоке, а кооперативный
            // путь продолжается после Task.Yield в контексте Unity на том же потоке — прогон Unity висел навсегда.
            const int seed = 31;
            foreach (bool cooperative in new[] { false, true })
            {
                TimeoutException timeout = null;
                try
                {
                    await BuildWithVirtualClockAsync(seed, 1, 1f, cooperative);
                }
                catch (TimeoutException exception)
                {
                    timeout = exception;
                }

                Assert.IsNotNull(timeout,
                    $"The {(cooperative ? "cooperative" : "synchronous")} path must abort with TimeoutException.");
            }
        }

        [Test]
        public void BuildLayout_WrongAnswerCapThatDoesNotFit_IsSealedWithWarning_NeverOverlapped()
        {
            LevelGraph graph = SocketBacktrackingRoomPlacerTests.DenseMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections =
                SocketBacktrackingRoomPlacerTests.UnfittableCapMazeSelections();
            FakeRoomDatabase roomDatabase = new(selections.Values.ToList());
            MissionGenerator generator = new(
                new SystemRandomRngFactory(),
                new StaticGraphBuilder(graph),
                new StaticRoomSelector(selections),
                new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard),
                new DoorConnector(),
                new LevelValidator(roomDatabase),
                roomDatabase,
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard);

            LevelLayout layout = generator.BuildLayout(new FakeMissionConfig(), 7);

            Assert.IsFalse(layout.Rooms.Any(room => room.NodeId == "n_wrong_1"),
                "A cap that only fits with overlap must not be placed at all.");
            Assert.IsTrue(layout.Rooms.Any(room => room.NodeId == "n_wrong_2"));
            PlacedRoom maze = layout.Rooms.Single(room => room.NodeId == "n_maze");
            string[] answerDoors = { "correct", "wrong_1", "wrong_2" };
            string[] sealedDoors = answerDoors.Where(id => maze.DoorStates[id] == DoorState.Disabled).ToArray();
            Assert.AreEqual(1, sealedDoors.Length, "Exactly the answer door of the missing cap is sealed into a wall.");
            string warning = generator.LastWarnings.Single(text => text.Contains("'n_wrong_1'"));
            StringAssert.Contains("'n_maze'", warning);
            StringAssert.Contains($"'{sealedDoors[0]}' is sealed", warning,
                "The warning must name the answer door of this particular cap.");
            foreach (string otherDoor in answerDoors.Where(id => id != sealedDoors[0]))
            {
                StringAssert.DoesNotContain($"'{otherDoor}'", warning, "Only this cap's door belongs in its warning.");
            }
            LevelValidationResult recheck = new LevelValidator(roomDatabase).Validate(
                layout,
                generator.LastGraph,
                new FakeMissionConfig(),
                new FakeTileset());
            Assert.IsFalse(recheck.Issues.Any(issue => issue.Code == "RoomsOverlap"));

            // Предупреждения — состояние экземпляра: другой генератор без лабиринта их не видит.
            MissionGenerator other = new(
                new SequenceRngFactory(),
                new StaticGraphBuilder(TestGraph()),
                new StaticRoomSelector(StaticSelection()),
                new StaticRoomPlacer(),
                new StaticDoorConnector(),
                new SequenceValidator(LevelValidationResult.Empty),
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) }),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard);
            other.BuildLayout(new FakeMissionConfig(), 7);
            Assert.IsFalse(other.LastWarnings.Any(warning => warning.Contains("n_wrong_1")));
        }

        [Test]
        public void BuildLayout_DependencyHintNeverPutsKeyBehindItsOwnLock()
        {
            // n_before (до замка) и n_after (за замком) оба в одном переходе от замка. Раньше узел выбирался по
            // неориентированному расстоянию, и при равенстве по ordinal выигрывал n_after — ключ за своей дверью.
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start), new RoomNode("n_before", RoomKind.Interactive), new RoomNode("n_lock", RoomKind.Interactive), new RoomNode("n_after", RoomKind.Interactive) },
                new[] { new LevelGraphEdge("n_start", "n_before", true), new LevelGraphEdge("n_before", "n_lock", true), new LevelGraphEdge("n_lock", "n_after", true) });
            FakeRoomTemplate start = new("start_room", RoomKind.Start);
            FakeRoomTemplate plain = new("plain_room", RoomKind.Interactive);
            FakeRoomTemplate lockRoom = new("lock_room", RoomKind.Interactive);
            FakeRoomTemplate keyRoom = new("key_room", RoomKind.Interactive);
            StaticRoomSelector roomSelector = new(new Dictionary<string, IRoomTemplateDescriptor> { ["n_start"] = start, ["n_before"] = plain, ["n_lock"] = lockRoom, ["n_after"] = plain });
            StaticDoorConnector doorConnector = new();
            MissionGenerator generator = new(
                new SingleRngFactory(),
                new StaticGraphBuilder(graph),
                roomSelector,
                new StaticRoomPlacer(),
                doorConnector,
                new SequenceValidator(LevelValidationResult.Empty),
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { start, plain, lockRoom, keyRoom }),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard,
                new IRoomDependencyHintExtractor[] { new HintExtractor(new RoomDependencyHint("key_room", "lock_room", 1, 1)) });

            generator.BuildLayout(new FakeMissionConfig(), 5);

            Assert.AreEqual("key_room", doorConnector.LastSelections["n_before"].RoomId,
                "The key must be placed where it is reachable from Start without entering its lock room.");
            Assert.AreEqual("plain_room", doorConnector.LastSelections["n_after"].RoomId,
                "A node reachable only through the lock must never receive its key.");
        }

        [Test]
        public void BuildLayout_RejectsLockWhoseOnlyKeyCandidateIsBehindIt()
        {
            // Единственный узел подходящего вида (n_after) — за замком. Раньше ключ ставился туда; теперь узла под
            // ключ нет, и стадия отклоняется явной ошибкой, а не уходит в игру замком без ключа.
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start), new RoomNode("n_lock", RoomKind.Interactive), new RoomNode("n_after", RoomKind.Interactive) },
                new[] { new LevelGraphEdge("n_start", "n_lock", true), new LevelGraphEdge("n_lock", "n_after", true) });
            FakeRoomTemplate start = new("start_room", RoomKind.Start);
            FakeRoomTemplate plain = new("plain_room", RoomKind.Interactive);
            FakeRoomTemplate lockRoom = new("lock_room", RoomKind.Interactive);
            FakeRoomTemplate keyRoom = new("key_room", RoomKind.Interactive);
            StaticRoomPlacer roomPlacer = new();
            MissionGenerator generator = new(
                new SingleRngFactory(),
                new StaticGraphBuilder(graph),
                new StaticRoomSelector(new Dictionary<string, IRoomTemplateDescriptor> { ["n_start"] = start, ["n_lock"] = lockRoom, ["n_after"] = plain }),
                roomPlacer,
                new StaticDoorConnector(),
                new SequenceValidator(LevelValidationResult.Empty),
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { start, plain, lockRoom, keyRoom }),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard,
                new IRoomDependencyHintExtractor[] { new HintExtractor(new RoomDependencyHint("key_room", "lock_room", 1, 3)) })
            {
                MaxGenerationAttempts = 2
            };

            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => generator.BuildLayout(new FakeMissionConfig(), 5));

            StringAssert.Contains("KeyDependencyUnsatisfied", ex.Message);
            StringAssert.Contains("'n_lock'", ex.Message);
            Assert.AreEqual(0, roomPlacer.CallCount, "A lock without a reachable key must be rejected before placement.");
        }

        [Test]
        public void BuildLayout_DependencyHintNeverPutsKeyOnWrongAnswerDeadEnd()
        {
            // Заглушка неверного ответа (n_cap) ближе к замку, чем n_passage, и раньше получала ключ. Но заглушку могут
            // убрать из раскладки (не поместилась) — ключ исчез бы вместе с ней. Ключ ставится только на обязательный узел.
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start), new RoomNode("n_quiz", RoomKind.Interactive), new RoomNode("n_cap", RoomKind.Passage, isOnMainPath: false, isDeadEnd: true), new RoomNode("n_lock", RoomKind.Interactive), new RoomNode("n_passage", RoomKind.Passage) },
                new[] { new LevelGraphEdge("n_start", "n_quiz", true), new LevelGraphEdge("n_quiz", "n_lock", true), new LevelGraphEdge("n_quiz", "n_cap", false, EdgeBranchRole.MazeWrongDeadEnd), new LevelGraphEdge("n_start", "n_passage", false) });
            FakeRoomTemplate start = new("start_room", RoomKind.Start);
            FakeRoomTemplate quiz = new("quiz_room", RoomKind.Interactive);
            FakeRoomTemplate cap = new("cap_room", RoomKind.Passage);
            FakeRoomTemplate lockRoom = new("lock_room", RoomKind.Interactive);
            FakeRoomTemplate passage = new("passage_room", RoomKind.Passage);
            FakeRoomTemplate keyRoom = new("key_room", RoomKind.Passage);
            StaticDoorConnector doorConnector = new();
            MissionGenerator generator = new(
                new SingleRngFactory(),
                new StaticGraphBuilder(graph),
                new StaticRoomSelector(new Dictionary<string, IRoomTemplateDescriptor> { ["n_start"] = start, ["n_quiz"] = quiz, ["n_cap"] = cap, ["n_lock"] = lockRoom, ["n_passage"] = passage }),
                new StaticRoomPlacer(),
                doorConnector,
                new SequenceValidator(LevelValidationResult.Empty),
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { start, quiz, cap, lockRoom, passage, keyRoom }),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard,
                new IRoomDependencyHintExtractor[] { new HintExtractor(new RoomDependencyHint("key_room", "lock_room", 1, 3)) });

            generator.BuildLayout(new FakeMissionConfig(), 5);

            Assert.AreEqual("key_room", doorConnector.LastSelections["n_passage"].RoomId,
                "The key belongs on a mandatory node reachable before the lock.");
            Assert.AreEqual("cap_room", doorConnector.LastSelections["n_cap"].RoomId,
                "A wrong-answer dead-end can be pruned from the layout and must never carry the key.");
        }

        [Test]
        public void BuildLayout_RejectsDuplicateConnectableSocketIdsBeforePlacement()
        {
            FakeRoomTemplate duplicate = new(
                "dup_room",
                RoomKind.Interactive,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("door", direction: DoorDirection.East), new FakeDoorSocket("door", direction: DoorDirection.West) });
            StaticRoomPlacer roomPlacer = new();
            MissionGenerator generator = new(
                new SequenceRngFactory(),
                new StaticGraphBuilder(TestGraph()),
                new StaticRoomSelector(new Dictionary<string, IRoomTemplateDescriptor> { ["n_start"] = duplicate, ["n_objective"] = duplicate }),
                roomPlacer,
                new StaticDoorConnector(),
                new SequenceValidator(LevelValidationResult.Empty),
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { duplicate }),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard)
            {
                MaxGenerationAttempts = 2
            };

            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => generator.BuildLayout(new FakeMissionConfig(), 3));

            StringAssert.Contains(DoorSocketIdRules.DuplicateIssueCode, ex.Message);
            StringAssert.Contains("'dup_room'", ex.Message);
            StringAssert.Contains("'door'", ex.Message);
            Assert.AreEqual(0, roomPlacer.CallCount, "A template whose doors would share one state must not be placed.");
        }

        [Test]
        public void BuildLayout_WarnsWhenDoorConnectorConnectsIncidentalContactsOutsideGraph()
        {
            MissionGenerator generator = new(
                new SequenceRngFactory(),
                new StaticGraphBuilder(TestGraph()),
                new StaticRoomSelector(StaticSelection()),
                new StaticRoomPlacer(),
                new DoorConnector(true),
                new SequenceValidator(LevelValidationResult.Empty),
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) }),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard);

            generator.BuildLayout(new FakeMissionConfig(), 4);

            Assert.IsTrue(
                generator.LastWarnings.Any(warning => warning.Contains("IncidentalDoorContactMode.Seal")),
                "Connecting incidental door contacts outside the graph only burns seeds; the generator must say so.");
        }

        private static async Task<(int successfulSeed, string fingerprint)> BuildWithVirtualClockAsync(
            int seed,
            int timeoutSeconds,
            float multiplier,
            bool cooperative)
        {
            ManualGenerationClock clock = new();
            MissionGenerator generator = new(
                new SeedTaggingRngFactory(),
                new StaticGraphBuilder(TestGraph()),
                new StaticRoomSelector(StaticSelection()),
                new SlowSeedRoomPlacer(seed, TimeSpan.FromMilliseconds(1100), clock),
                new StaticDoorConnector(),
                new SequenceValidator(LevelValidationResult.Empty),
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("interactive_room", RoomKind.Interactive) }),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard)
            {
                GenerationTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                GenerationTimeBudgetMultiplier = multiplier,
                GenerationClockFactory = clock.Restart
            };

            LevelLayout layout = cooperative
                ? await generator.BuildLayoutCooperativeAsync(new FakeMissionConfig(), seed)
                : generator.BuildLayout(new FakeMissionConfig(), seed);
            return (generator.LastSuccessfulSeed, SocketBacktrackingRoomPlacerTests.Fingerprint(layout));
        }

        private static LevelLayout BuildLayoutFromSelections(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            bool includeWorldPos)
        {
            PlacedRoom[] rooms = new PlacedRoom[graph.Nodes.Count];
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                IRoomTemplateDescriptor template = selections[node.NodeId];
                rooms[i] = includeWorldPos
                    ? new PlacedRoom(
                        node.NodeId,
                        template.RoomId,
                        new GridPose(i * 2f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        null,
                        new RoomFootprint(2, 2))
                    : new PlacedRoom(
                        node.NodeId,
                        template.RoomId,
                        new GridPose(i * 2f, 0f, 0),
                        new Dictionary<string, DoorState>());
            }

            DoorConnection[] connections = new[] { new DoorConnection("n_start", "east", "n_objective", "west") };

            return new LevelLayout(rooms, connections, "n_start", new[] { "n_objective" });
        }

        private static LevelLayout BuildMazeBranchLayout(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            bool includeWrongDeadEnds)
        {
            PlacedRoom[] rooms = new PlacedRoom[graph.Nodes.Count];
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                IRoomTemplateDescriptor template = selections[node.NodeId];
                rooms[i] = new PlacedRoom(
                    node.NodeId,
                    template.RoomId,
                    new GridPose(i * 3f, 0f, 0),
                    new Dictionary<string, DoorState>(),
                    null,
                    new RoomFootprint(1, 1),
                    contentId: node.ContentId);
            }

            List<DoorConnection> connections = new() { new DoorConnection("n_start", "out", "n_quiz", "entry", true), new DoorConnection("n_quiz", "answer_correct", "n_correct", "in", true) };

            if (includeWrongDeadEnds)
            {
                connections.Add(new DoorConnection("n_quiz", "answer_wrong_1", "n_wrong_1", "in", false));
                connections.Add(new DoorConnection("n_quiz", "answer_wrong_2", "n_wrong_2", "in", false));
            }

            return new LevelLayout(rooms, connections, "n_start", new[] { "n_correct" });
        }

        private static LevelGraph TestGraph()
        {
            return new LevelGraph(
                new[] { new RoomNode("n_start", RoomKind.Start), new RoomNode("n_objective", RoomKind.Interactive) },
                new[] { new LevelGraphEdge("n_start", "n_objective", true) });
        }

        private static LevelGraph MazeBranchGraph()
        {
            return new LevelGraph(
                new[]
                {
                    new RoomNode("n_start", RoomKind.Start), new RoomNode("n_quiz", RoomKind.Interactive, 4, contentId: "Q_MAZE"), new RoomNode("n_correct", RoomKind.Passage, 2, isOnMainPath: true), new RoomNode("n_wrong_1", RoomKind.Passage, 1, isOnMainPath: false,
                        isDeadEnd: true),
                    new RoomNode("n_wrong_2", RoomKind.Passage, 1, isOnMainPath: false,
                        isDeadEnd: true)
                },
                new[] { new LevelGraphEdge("n_start", "n_quiz", true), new LevelGraphEdge("n_quiz", "n_correct", true), new LevelGraphEdge("n_quiz", "n_wrong_1", false), new LevelGraphEdge("n_quiz", "n_wrong_2", false) });
        }

        private static IReadOnlyDictionary<string, IRoomTemplateDescriptor> StaticSelection()
        {
            FakeRoomTemplate template = new("interactive_room", RoomKind.Interactive);
            return new Dictionary<string, IRoomTemplateDescriptor> { ["n_start"] = template, ["n_objective"] = template };
        }

        private static IReadOnlyDictionary<string, IRoomTemplateDescriptor> MazeBranchSelections()
        {
            FakeRoomTemplate start = new(
                "start_room",
                RoomKind.Start,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("out") });
            FakeRoomTemplate quiz = new(
                "maze_branch_room",
                RoomKind.Interactive,
                interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("entry"), new FakeDoorSocket("answer_correct", initialState: DoorState.Locked), new FakeDoorSocket("answer_wrong_1", initialState: DoorState.Locked), new FakeDoorSocket("answer_wrong_2", initialState: DoorState.Locked) });
            FakeRoomTemplate correct = new(
                "correct_tunnel",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("in"), new FakeDoorSocket("out") });
            FakeRoomTemplate wrong = new(
                "wrong_dead_end",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("in") });

            return new Dictionary<string, IRoomTemplateDescriptor>
            {
                ["n_start"] = start,
                ["n_quiz"] = quiz,
                ["n_correct"] = correct,
                ["n_wrong_1"] = wrong,
                ["n_wrong_2"] = wrong
            };
        }

        private static LevelValidationResult ValidationError(string code) => new(new[] { new LevelValidationIssue(code, "No valid layout", LevelValidationSeverity.Error) });

        private static PlacedRoom FindRoom(LevelLayout layout, string nodeId)
        {
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (room.NodeId == nodeId)
                {
                    return room;
                }
            }

            return null;
        }

        private sealed class SequenceRngFactory : IDeterministicRngFactory
        {
            private readonly IDeterministicRng _rng = new SequenceRng();
            public readonly List<int> CreatedSeeds = new();

            public IDeterministicRng Create(int seed)
            {
                CreatedSeeds.Add(seed);
                return _rng;
            }
        }

        private sealed class SingleRngFactory : IDeterministicRngFactory
        {
            private readonly IDeterministicRng _rng = new SequenceRng();

            public IDeterministicRng Create(int seed) => _rng;
        }

        private sealed class StaticGraphBuilder : IMissionGraphBuilder
        {
            private readonly LevelGraph _graph;
            public int CallCount;

            public StaticGraphBuilder(LevelGraph graph)
            {
                _graph = graph;
            }

            public LevelGraph Build(IMissionConfigView config, ITilesetView tileset, IDeterministicRng rng)
            {
                CallCount++;
                return _graph;
            }
        }

        private sealed class CapturingGraphBuilder : IMissionGraphBuilder
        {
            private readonly LevelGraph _graph;

            public IReadOnlyList<IInteractiveMissionContentSpec> LastInteractiveContent =
                Array.Empty<IInteractiveMissionContentSpec>();

            public OptionalIntRange LastTotalRoomsRange;

            public CapturingGraphBuilder(LevelGraph graph)
            {
                _graph = graph;
            }

            public LevelGraph Build(IMissionConfigView config, ITilesetView tileset, IDeterministicRng rng)
            {
                LastTotalRoomsRange = config.TotalRoomsRange;
                if (config is IPlannedInteractiveMissionContentProvider plannedProvider)
                {
                    LastInteractiveContent = plannedProvider.PlannedInteractiveContent;
                }
                else if (config is IInteractiveMissionContentProvider provider)
                {
                    LastInteractiveContent = provider.InteractiveContent;
                }

                return _graph;
            }
        }

        private sealed class StaticRoomSelector : IRoomSelector
        {
            public readonly Dictionary<string, IRoomTemplateDescriptor> Selections;
            public int CallCount;
            public IReadOnlyDictionary<string, IRoomTemplateDescriptor> SelectionsView => Selections;

            public StaticRoomSelector(IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections = null)
            {
                Selections =
                    new Dictionary<string, IRoomTemplateDescriptor>(selections ??
                                                                    new Dictionary<string, IRoomTemplateDescriptor>());
            }

            public IRoomTemplateDescriptor Select(
                RoomNode node,
                IMissionConfigView config,
                ITilesetView tileset,
                IRoomDatabase database,
                IDeterministicRng rng)
            {
                CallCount++;
                return Selections.TryGetValue(node.NodeId, out IRoomTemplateDescriptor template) ? template : null;
            }
        }

        private sealed class StaticRoomPlacer : IRoomPlacer
        {
            public int CallCount;
            public LevelLayout LastLayout;

            public LevelLayout Place(
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
                IDeterministicRng rng,
                System.Threading.CancellationToken ct = default)
            {
                CallCount++;
                LastLayout = BuildLayoutFromSelections(graph, selections, false);
                return LastLayout;
            }
        }

        private sealed class AlternatingGraphBuilder : IMissionGraphBuilder
        {
            private readonly LevelGraph[] _graphs;
            public int CallCount;
            public LevelGraph LastGraph;

            public AlternatingGraphBuilder(params LevelGraph[] graphs)
            {
                _graphs = graphs;
            }

            public LevelGraph Build(IMissionConfigView config, ITilesetView tileset, IDeterministicRng rng)
            {
                int index = CallCount < _graphs.Length ? CallCount : _graphs.Length - 1;
                LastGraph = _graphs[index];
                CallCount++;
                return LastGraph;
            }
        }

        private sealed class StaticDoorConnector : IDoorConnector
        {
            private readonly LevelLayout _layout;
            public int CallCount;
            public Dictionary<string, IRoomTemplateDescriptor> LastSelections;

            public StaticDoorConnector(LevelLayout layout = null)
            {
                _layout = layout;
            }

            public LevelLayout Connect(
                LevelLayout layout,
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections)
            {
                CallCount++;
                LastSelections = new Dictionary<string, IRoomTemplateDescriptor>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, IRoomTemplateDescriptor> pair in selections)
                {
                    LastSelections[pair.Key] = pair.Value;
                }

                return _layout ?? layout;
            }
        }

        private sealed class SequencedDoorConnector : IDoorConnector
        {
            private readonly LevelLayout[] _layouts;
            public int CallCount;

            public SequencedDoorConnector(params LevelLayout[] layouts)
            {
                _layouts = layouts;
            }

            public LevelLayout Connect(
                LevelLayout layout,
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections)
            {
                int index = Math.Min(CallCount, _layouts.Length - 1);
                CallCount++;
                return _layouts[index] ?? layout;
            }
        }

        private sealed class SequenceValidator : ILevelValidator
        {
            private readonly List<LevelValidationResult> _results = new();
            private int _index;
            public int CallCount;

            public SequenceValidator(params LevelValidationResult[] results)
            {
                _results.AddRange(results);
                if (_results.Count == 0)
                {
                    _results.Add(LevelValidationResult.Empty);
                }
            }

            public LevelValidationResult Validate(LevelLayout layout, IMissionConfigView config, ITilesetView tileset)
            {
                CallCount++;
                LevelValidationResult result = _results[Math.Min(_index, _results.Count - 1)];
                _index++;
                return result;
            }
        }

        private sealed class HintExtractor : IRoomDependencyHintExtractor
        {
            private readonly RoomDependencyHint _hint;

            public HintExtractor(RoomDependencyHint hint)
            {
                _hint = hint;
            }

            public IReadOnlyList<RoomDependencyHint> Extract(
                IReadOnlyList<IRoomTemplateDescriptor> rooms,
                ITilesetView tileset) =>
                new[] { _hint };
        }

        private sealed class MissingTilesetCatalog : ITilesetCatalog
        {
            public ITilesetView FindTileset(string tilesetId) => null;
        }

        private sealed class FakeTilesetCatalog : ITilesetCatalog
        {
            private readonly ITilesetView _tileset = new FakeTileset();

            public ITilesetView FindTileset(string tilesetId) => _tileset;
        }

        private sealed class InteractiveContentMissionConfig : FakeMissionConfig, IInteractiveMissionContentProvider
        {
            public InteractiveContentMissionConfig(params IInteractiveMissionContentSpec[] content)
            {
                InteractiveContent = content;
            }

            public IReadOnlyList<IInteractiveMissionContentSpec> InteractiveContent { get; }
        }

        private sealed class InteractiveContent : IInteractiveMissionContentSpec
        {
            public InteractiveContent(string contentId, string interactiveKind, int optionCount, int correctAnswerCount, bool isMandatory = true)
            {
                ContentId = contentId;
                InteractiveKind = interactiveKind;
                OptionCount = optionCount;
                CorrectAnswerCount = correctAnswerCount;
                IsMandatory = isMandatory;
            }

            public string ContentId { get; }
            public string InteractiveKind { get; }
            public int OptionCount { get; }
            public int CorrectAnswerCount { get; }
            public bool IsMandatory { get; }
        }

        private sealed class SeedTaggedRng : IDeterministicRng
        {
            private readonly SystemRandomRng _inner;

            public SeedTaggedRng(int seed)
            {
                Seed = seed;
                _inner = new SystemRandomRng(seed);
            }

            public int Seed { get; }

            public int NextInt(int minInclusive, int maxExclusive) => _inner.NextInt(minInclusive, maxExclusive);

            public float NextFloat() => _inner.NextFloat();

            public void Shuffle<T>(IList<T> list) => _inner.Shuffle(list);
        }

        private sealed class SeedTaggingRngFactory : IDeterministicRngFactory
        {
            public IDeterministicRng Create(int seed) => new SeedTaggedRng(seed);
        }

        // Часы прогона, которыми управляет тест: время идёт только когда его «тратит» подставное размещение.
        private sealed class ManualGenerationClock : IGenerationClock
        {
            public TimeSpan Elapsed { get; private set; }

            public IGenerationClock Restart()
            {
                Elapsed = TimeSpan.Zero;
                return this;
            }

            public void Advance(TimeSpan duration) => Elapsed += duration;
        }

        // Размещение, которое для одного сида «работает» заметно долго по часам прогона (без реального сна), а позы
        // комнат берёт из ГСЧ сида — по отпечатку видно, какой сид дал раскладку.
        private sealed class SlowSeedRoomPlacer : IRoomPlacer
        {
            private readonly int _slowSeed;
            private readonly TimeSpan _slowDuration;
            private readonly ManualGenerationClock _clock;

            public SlowSeedRoomPlacer(int slowSeed, TimeSpan slowDuration, ManualGenerationClock clock)
            {
                _slowSeed = slowSeed;
                _slowDuration = slowDuration;
                _clock = clock;
            }

            public LevelLayout Place(
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
                IDeterministicRng rng,
                System.Threading.CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (rng is SeedTaggedRng tagged && tagged.Seed == _slowSeed)
                {
                    _clock.Advance(_slowDuration);
                }

                PlacedRoom[] rooms = new PlacedRoom[graph.Nodes.Count];
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    RoomNode node = graph.Nodes[i];
                    rooms[i] = new PlacedRoom(
                        node.NodeId,
                        selections[node.NodeId].RoomId,
                        new GridPose(rng.NextInt(0, 1000), rng.NextInt(0, 1000), 0),
                        new Dictionary<string, DoorState>());
                }

                return new LevelLayout(
                    rooms,
                    new[] { new DoorConnection("n_start", "east", "n_objective", "west") },
                    "n_start",
                    new[] { "n_objective" });
            }
        }
    }
}
