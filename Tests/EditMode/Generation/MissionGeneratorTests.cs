using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

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

            // Maze wrong-answer dead-ends are now best-effort + non-fatal: the placer force-places them (so the
            // contract is honoured) and any leftover topology gap is a WARNING, not a seed-rejecting error. So the
            // generator no longer retries when the first layout's wrong answers are not dead-ends — it accepts the
            // first layout and everything runs exactly once on seed 21.
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
    }
}
