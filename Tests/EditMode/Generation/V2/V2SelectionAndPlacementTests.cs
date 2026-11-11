using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Application.V2.Placement;
using RedoSchool.LevelGeneration.Generation.Application.V2.Selection;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation.V2
{
    [Category("LevelGeneration")]
    public sealed class V2SelectionAndPlacementTests
    {
        [Test]
        public void SelectionAdapter_PassageNodeGetsPassageRoom()
        {
            PacingGraph graph = SingleNodeGraph(new PacingGraphNode("n1", RoomKind.Passage, true, false, null, 2));
            FakeRoomDatabase database = new(
                Room("passage", RoomKind.Passage, 2),
                Room("interactive", RoomKind.Interactive, 5, 4));

            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selected =
                new V2RoomSelectionAdapter(new RoomSelector(TestInteractiveRoomKinds.Standard)).SelectRooms(graph, new FakeMissionConfig(), new FakeTileset(), database,
                    new FakeRng(0));

            Assert.That(selected["n1"].RoomId, Is.EqualTo("passage"));
        }

        [Test]
        public void SelectionAdapter_MazeBranchRequiresCompatibleFourAnswerRoom()
        {
            PacingGraphNode node = new("quiz", RoomKind.Interactive, true, false, "q1", 5);
            PacingGraph graph = SingleNodeGraph(node);
            FakeMissionConfig config = new(Mandatory("q1"));
            FakeRoomDatabase database = new(
                Room("quiz_2", RoomKind.Interactive, 3, 2),
                Room("quiz_4", RoomKind.Interactive, 5, 4));

            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selected =
                new V2RoomSelectionAdapter(new RoomSelector(TestInteractiveRoomKinds.Standard)).SelectRooms(graph, config, new FakeTileset(), database, new FakeRng(0));

            Assert.That(selected["quiz"].RoomId, Is.EqualTo("quiz_4"));
        }

        [Test]
        public void SelectionAdapter_SideBranchDoesNotChooseForbiddenTemplate()
        {
            PacingGraph graph = SingleNodeGraph(new PacingGraphNode("branch", RoomKind.Passage, false, true, null, 1));
            FakeRoomDatabase database = new(
                Room("forbidden", RoomKind.Passage, 1, 0, false),
                Room("allowed", RoomKind.Passage, 1, 0, true));

            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selected =
                new V2RoomSelectionAdapter(new RoomSelector(TestInteractiveRoomKinds.Standard)).SelectRooms(graph, new FakeMissionConfig(), new FakeTileset(), database,
                    new FakeRng(0));

            Assert.That(selected["branch"].RoomId, Is.EqualTo("allowed"));
        }

        [Test]
        public void SelectionAdapter_RoomWeightOverrideInfluencesChoice()
        {
            PacingGraph graph = SingleNodeGraph(new PacingGraphNode("n1", RoomKind.Passage, true, false, null, 1));
            FakeMissionConfig config = new(roomWeightOverrides: new[] { new FakeRoomWeightOverride("b", 100) });
            FakeRoomDatabase database = new(
                Room("a", RoomKind.Passage, 1, weight: 1),
                Room("b", RoomKind.Passage, 1, weight: 1));

            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selected =
                new V2RoomSelectionAdapter(new RoomSelector(TestInteractiveRoomKinds.Standard)).SelectRooms(graph, config, new FakeTileset(), database, new FakeRng(50));

            Assert.That(selected["n1"].RoomId, Is.EqualTo("b"));
        }

        [Test]
        public void PlacementCandidateMemory_SkipsRepeatedRejectedCandidate()
        {
            PlacementCandidateMemory memory = new();
            PlacementCandidateKey key = CandidateKey(0, "east");

            memory.RememberRejected(key);

            Assert.That(memory.WasRejected(key), Is.True);
        }

        [Test]
        public void PlacementCandidateMemory_ClearsBetweenAttempts()
        {
            PlacementCandidateMemory memory = new();
            PlacementCandidateKey key = CandidateKey(0, "east");

            memory.RememberRejected(key);
            memory.Clear();

            Assert.That(memory.WasRejected(key), Is.False);
        }

        [Test]
        public void PlacementCandidateMemory_DifferentYawIsDifferentCandidate()
        {
            PlacementCandidateMemory memory = new();

            memory.RememberRejected(CandidateKey(0, "east"));

            Assert.That(memory.WasRejected(CandidateKey(90, "east")), Is.False);
        }

        [Test]
        public void PlacementCandidateMemory_DifferentParentSocketIsDifferentCandidate()
        {
            PlacementCandidateMemory memory = new();

            memory.RememberRejected(CandidateKey(0, "east"));

            Assert.That(memory.WasRejected(CandidateKey(0, "west")), Is.False);
        }

        [Test]
        public void PlacementRunner_RespectsPreCancelledToken()
        {
            PacingGraph graph = SingleNodeGraph(new PacingGraphNode("n1", RoomKind.Start, true, false, null, 1));
            FakeRoomPlacer placer = new();
            CancellationTokenSource cts = new();
            cts.Cancel();

            PacingPlacementResult result = new PacingSocketPlacementRunner(placer).Place(
                graph,
                new Dictionary<string, IRoomTemplateDescriptor>(),
                new FakeRng(0),
                cts.Token);

            Assert.That(result.Success, Is.False);
            Assert.That(placer.WasCalled, Is.False);
            Assert.That(result.Diagnostics.UnplacedNodeCount, Is.EqualTo(1));
        }

        [Test]
        public void PlacementRunner_PopulatesSuccessDiagnostics()
        {
            PacingGraph graph = SingleNodeGraph(new PacingGraphNode("n1", RoomKind.Start, true, false, null, 1));
            FakeRoomPlacer placer = new();

            PacingPlacementResult result = new PacingSocketPlacementRunner(placer).Place(
                graph,
                new Dictionary<string, IRoomTemplateDescriptor>(),
                new FakeRng(0));

            Assert.That(result.Success, Is.True);
            Assert.That(result.Diagnostics.CandidateCount, Is.EqualTo(1));
            Assert.That(result.Diagnostics.UnplacedNodeCount, Is.EqualTo(0));
        }

        [Test]
        public void ToLevelGraph_CarriesMazeBranchEdgeRoles()
        {
            PacingGraph graph = new(
                new[]
                {
                    new PacingGraphNode("start", RoomKind.Start, true), new PacingGraphNode("maze", RoomKind.Interactive, true, contentId: "Q_MAZE"), new PacingGraphNode("correct", RoomKind.Passage, true), new PacingGraphNode("wrong", RoomKind.Passage, false, true, source: "quiz_wrong_terminal",
                        branchPurpose: BranchPurpose.WrongAnswerDeadEnd)
                },
                new[] { new PacingGraphEdge("start", "maze", true), new PacingGraphEdge("maze", "correct", true, "correct"), new PacingGraphEdge("maze", "wrong", false, "wrong") });

            LevelGraph levelGraph = PacingSocketPlacementRunner.ToLevelGraph(graph);

            Assert.That(levelGraph.Edges.Single(e => e.FromNodeId == "start").BranchRole,
                Is.EqualTo(EdgeBranchRole.MazeEntry));
            Assert.That(levelGraph.Edges.Single(e => e.ToNodeId == "correct").BranchRole,
                Is.EqualTo(EdgeBranchRole.MazeCorrectExit));
            Assert.That(levelGraph.Edges.Single(e => e.ToNodeId == "wrong").BranchRole,
                Is.EqualTo(EdgeBranchRole.MazeWrongDeadEnd));
        }

        private static PacingGraph SingleNodeGraph(PacingGraphNode node) => new(new[] { node }, new PacingGraphEdge[0]);

        private static PlacementCandidateKey CandidateKey(int yaw, string parentSocket)
        {
            return new PlacementCandidateKey("node", "room", "parent", parentSocket, "candidate", yaw,
                new GridPose(1, 2, yaw));
        }

        private static PlannedInteractiveContent Mandatory(string id)
        {
            return new PlannedInteractiveContent(id, TestInteractiveRoomKinds.MazeBranch, 4, 1, true,
                AnswerBranchPolicy.CorrectContinuesWrongTerminal);
        }

        private static FakeRoom Room(string id, RoomKind kind, int doorCount, int interactiveOptionCapacity = 0, bool canBeSideBranch = true, int weight = 10) => new(id, kind, doorCount, interactiveOptionCapacity, canBeSideBranch, weight);

        private sealed class FakeRoomDatabase : IRoomDatabase
        {
            private readonly IReadOnlyList<IRoomTemplateDescriptor> rooms;

            public FakeRoomDatabase(params IRoomTemplateDescriptor[] rooms)
            {
                this.rooms = rooms;
            }

            public IReadOnlyList<IRoomTemplateDescriptor> AllRooms => rooms;

            public IReadOnlyList<IRoomTemplateDescriptor> GetRoomsForTileset(string tilesetId) => rooms.Where(room => room.TilesetId == tilesetId).ToArray();
        }

        private sealed class FakeRoom : IRoomTemplateDescriptor, IInteractiveRoomTemplateDescriptor
        {
            private readonly IReadOnlyList<IDoorSocketDescriptor> doors;

            public FakeRoom(string id, RoomKind kind, int doorCount, int interactiveOptionCapacity,
                bool canBeSideBranch, int weight)
            {
                RoomId = id;
                Kind = kind;
                Weight = weight;
                CanBeSideBranch = canBeSideBranch;
                InteractiveOptionCapacity = interactiveOptionCapacity;
                doors = Enumerable.Range(0, doorCount)
                    .Select(index =>
                        (IDoorSocketDescriptor)new FakeDoor("d" + index, index < interactiveOptionCapacity))
                    .ToArray();
            }

            public string RoomId { get; }
            public RoomKind Kind { get; }
            public string TilesetId => "test";
            public int Difficulty => 1;
            public int Weight { get; }
            public bool CanBeMainPath => true;
            public bool CanBeSideBranch { get; }
            public bool CanBeStart => Kind == RoomKind.Start;
            public bool CanBeExit => Kind == RoomKind.Exit;
            public bool CanBeDeadEnd => true;
            public IReadOnlyList<string> Tags => new string[0];
            public int EstimatedDurationSeconds => 1;
            public RoomFootprint Footprint => new(1, 1);
            public IReadOnlyList<IDoorSocketDescriptor> Doors => doors;
            public string ModuleAssetId => RoomId;

            public string InteractiveKind =>
                InteractiveOptionCapacity > 0 ? TestInteractiveRoomKinds.MazeBranch : string.Empty;

            public int InteractiveOptionCapacity { get; }
        }

        private sealed class FakeDoor : IDoorSocketDescriptor, IDoorSocketInitialStateDescriptor
        {
            private readonly bool locked;

            public FakeDoor(string id, bool locked)
            {
                SocketId = id;
                this.locked = locked;
            }

            public string SocketId { get; }
            public DoorSize Size => DoorSize.Medium;
            public DoorDirection Direction => DoorDirection.North;
            public int GridOffsetSteps => 0;
            public int LocalXSteps => 0;
            public int LocalZSteps => 0;
            public float LocalYawDegrees => 0f;
            public bool CanBeMainPath => true;
            public bool CanBeSidePath => true;
            public bool CanBeLocked => true;
            public DoorState InitialState => locked ? DoorState.Locked : DoorState.Open;
        }

        private sealed class FakeTileset : ITilesetView
        {
            public string TilesetId => "test";
            public string DisplayName => "Test";
            public IntRange TotalRoomsRange => new(1, 10);
            public IntRange MainPathRoomsRange => new(1, 10);
            public IntRange BranchingRange => new(0, 4);
            public IntRange SideBranchesRange => new(0, 4);
            public IntRange DeadEndsRange => new(0, 4);
            public IntRange DependencyDistanceRange => new(1, 3);
            public float BonusRoomChance => 0f;
            public float SecretRoomChance => 0f;
            public float LogicRoomShare => 0f;
            public ExtractionMode DefaultExtractionMode => ExtractionMode.SeparateExitRoom;
            public ExtractionAvailability DefaultExtractionAvailability => ExtractionAvailability.AlwaysAvailable;
        }

        private sealed class FakeMissionConfig : IMissionConfigView, IInteractiveMissionContentProvider,
            IPlannedInteractiveMissionContentProvider
        {
            private readonly IReadOnlyList<IInteractiveMissionContentSpec> content;
            private readonly IReadOnlyList<IRoomWeightOverride> roomWeightOverrides;

            public FakeMissionConfig(
                PlannedInteractiveContent planned = null,
                IReadOnlyList<IRoomWeightOverride> roomWeightOverrides = null)
            {
                content = planned == null
                    ? new IInteractiveMissionContentSpec[0]
                    : new IInteractiveMissionContentSpec[] { new FakeContentSpec(planned) };
                this.roomWeightOverrides = roomWeightOverrides ?? new IRoomWeightOverride[0];
            }

            public string MissionId => "mission";
            public string TilesetId => "test";
            public MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
            public int Difficulty => 1;
            public OptionalIntRange TotalRoomsRange => new(1, 10);
            public OptionalIntRange MainPathRoomsRange => new(1, 10);
            public OptionalIntRange BranchingRange => new(0, 4);
            public OptionalIntRange SideBranchesRange => new(0, 4);
            public OptionalIntRange DeadEndsRange => new(0, 4);
            public bool AllowLoops => false;
            public OptionalFloat BonusRoomChance => new(0f);
            public OptionalFloat SecretRoomChance => new(0f);
            public OptionalFloat LogicRoomShare => new(0f);
            public ExtractionMode ExtractionMode => ExtractionMode.SeparateExitRoom;
            public int ExtractionRoomsCount => 0;
            public ExtractionAvailability ExtractionAvailability => ExtractionAvailability.AlwaysAvailable;
            public float ExtractionUnlockDelaySeconds => 0f;
            public IReadOnlyList<IMissionRoomRule> MissionRoomRules => new IMissionRoomRule[0];
            public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => roomWeightOverrides;
            public LevelGenerationMode GenerationMode => LevelGenerationMode.Procedural;
            public IReadOnlyList<IHybridSequenceStep> HybridSequence => new IHybridSequenceStep[0];
            public IReadOnlyList<IInteractiveMissionContentSpec> InteractiveContent => content;
            public IReadOnlyList<IInteractiveMissionContentSpec> PlannedInteractiveContent => content;
        }

        private sealed class FakeContentSpec : IInteractiveMissionContentSpec
        {
            public FakeContentSpec(PlannedInteractiveContent content)
            {
                ContentId = content.ContentId;
                InteractiveKind = TestInteractiveRoomKinds.MazeBranch;
                OptionCount = content.OptionCount;
                CorrectAnswerCount = content.CorrectAnswerCount;
                IsMandatory = content.IsMandatory;
            }

            public string ContentId { get; }
            public string InteractiveKind { get; }
            public int OptionCount { get; }
            public int CorrectAnswerCount { get; }
            public bool IsMandatory { get; }
        }

        private sealed class FakeRoomWeightOverride : IRoomWeightOverride
        {
            public FakeRoomWeightOverride(string roomId, int weight)
            {
                RoomId = roomId;
                Weight = weight;
            }

            public string RoomId { get; }
            public int Weight { get; }
        }

        private sealed class FakeRng : IDeterministicRng
        {
            private readonly int value;

            public FakeRng(int value)
            {
                this.value = value;
            }

            public int NextInt(int minInclusive, int maxExclusive) => System.Math.Min(maxExclusive - 1, minInclusive + value);

            public float NextFloat() => 0f;

            public void Shuffle<T>(IList<T> list)
            {
            }
        }

        private sealed class
            FakeRoomPlacer : RedoSchool.LevelGeneration.Generation.Application.Pipeline.IRoomPlacer
        {
            public bool WasCalled { get; private set; }

            public LevelLayout Place(
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
                IDeterministicRng rng,
                CancellationToken ct = default)
            {
                WasCalled = true;
                return new LevelLayout(
                    new[] { new PlacedRoom("n1", "room", new GridPose(0, 0, 0), new Dictionary<string, DoorState>()) },
                    new DoorConnection[0],
                    "n1",
                    new string[0]);
            }
        }
    }
}
