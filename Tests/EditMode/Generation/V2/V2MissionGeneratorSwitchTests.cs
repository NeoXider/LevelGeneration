using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation.V2
{
    [Category("LevelGeneration")]
    public sealed class V2MissionGeneratorSwitchTests
    {
        [Test]
        public void BuildLayout_UsesV2GraphWhenProviderEnabled()
        {
            FakeGraphBuilder graphBuilder = new();
            MissionGenerator generator = CreateGenerator(graphBuilder);

            generator.BuildLayout(new FakeConfig(true), 42);

            Assert.That(graphBuilder.WasCalled, Is.False);
            Assert.That(generator.LastGraph.Nodes.Any(node => node.NodeId == "n_start"), Is.True);
            Assert.That(generator.LastGraph.Nodes.Any(node => node.NodeId == "v1_start"), Is.False);
        }

        [Test]
        public void BuildLayout_UsesV1GraphBuilderWhenProviderDisabled()
        {
            FakeGraphBuilder graphBuilder = new();
            MissionGenerator generator = CreateGenerator(graphBuilder);

            generator.BuildLayout(new FakeConfig(false), 42);

            Assert.That(graphBuilder.WasCalled, Is.True);
            Assert.That(generator.LastGraph.Nodes.Any(node => node.NodeId == "v1_start"), Is.True);
        }

        [Test]
        public void BuildLayout_WhenV2FullStageFailsPlacement_TriesTrimmedStages()
        {
            FakeGraphBuilder graphBuilder = new();
            MissionGenerator generator = CreateGenerator(graphBuilder, roomPlacer: new SideBranchOmittingRoomPlacer());

            generator.BuildLayout(new FakeConfig(
                true,
                LayoutRetryPolicy.TrimOptional,
                new PacingIntRange(2, 2),
                new WeightedBranchLengthConfig(new PacingIntRange(1, 1), 0f),
                4), 42);

            Assert.That(
                generator.LastGraph.Nodes.Any(node => node.NodeId.StartsWith("n_branch", StringComparison.Ordinal)),
                Is.False);
        }

        private static MissionGenerator CreateGenerator(
            IMissionGraphBuilder graphBuilder,
            IRoomSelector roomSelector = null,
            IRoomPlacer roomPlacer = null)
        {
            return new MissionGenerator(
                new FakeRngFactory(),
                graphBuilder,
                roomSelector ?? new FakeRoomSelector(),
                roomPlacer ?? new FakeRoomPlacer(),
                new PassthroughDoorConnector(),
                new AlwaysValidValidator(),
                new FakeRoomDatabase(),
                new FakeTilesetCatalog(),
                TestInteractiveRoomKinds.Standard);
        }

        private sealed class FakeConfig : IMissionConfigView, ILayoutPacingConfigProvider
        {
            private readonly LayoutRetryPolicy retryPolicy;
            private readonly PacingIntRange sideBranchCount;
            private readonly WeightedBranchLengthConfig sideBranchLengthRooms;
            private readonly int optionalFillerCap;

            public FakeConfig(
                bool useV2,
                LayoutRetryPolicy retryPolicy = LayoutRetryPolicy.FailFast,
                PacingIntRange? sideBranchCount = null,
                WeightedBranchLengthConfig? sideBranchLengthRooms = null,
                int optionalFillerCap = 0)
            {
                UseLayoutPacingGeneratorV2 = useV2;
                this.retryPolicy = retryPolicy;
                this.sideBranchCount = sideBranchCount ?? new PacingIntRange(0, 0);
                this.sideBranchLengthRooms = sideBranchLengthRooms ??
                                             new WeightedBranchLengthConfig(new PacingIntRange(0, 0), 0f);
                this.optionalFillerCap = optionalFillerCap;
            }

            public bool UseLayoutPacingGeneratorV2 { get; }

            public LayoutPacingConfig ResolveLayoutPacingConfig()
            {
                return new LayoutPacingConfig(
                    new PacingIntRange(0, 0),
                    sideBranchCount,
                    sideBranchLengthRooms,
                    0,
                    5f,
                    retryPolicy,
                    optionalFillerCap);
            }

            public string MissionId => "mission";
            public string TilesetId => "test";
            public MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
            public int Difficulty => 1;
            public OptionalIntRange TotalRoomsRange => new(1, 3);
            public OptionalIntRange MainPathRoomsRange => new(1, 3);
            public OptionalIntRange BranchingRange => new(0, 0);
            public OptionalIntRange SideBranchesRange => new(0, 0);
            public OptionalIntRange DeadEndsRange => new(0, 0);
            public bool AllowLoops => false;
            public OptionalFloat BonusRoomChance => new(0f);
            public OptionalFloat SecretRoomChance => new(0f);
            public OptionalFloat LogicRoomShare => new(0f);
            public ExtractionMode ExtractionMode => ExtractionMode.SeparateExitRoom;
            public int ExtractionRoomsCount => 0;
            public ExtractionAvailability ExtractionAvailability => ExtractionAvailability.AlwaysAvailable;
            public float ExtractionUnlockDelaySeconds => 0f;
            public IReadOnlyList<IMissionRoomRule> MissionRoomRules => new IMissionRoomRule[0];
            public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => new IRoomWeightOverride[0];
            public LevelGenerationMode GenerationMode => LevelGenerationMode.Procedural;
            public IReadOnlyList<IHybridSequenceStep> HybridSequence => new IHybridSequenceStep[0];
        }

        private sealed class FakeGraphBuilder : IMissionGraphBuilder
        {
            public bool WasCalled { get; private set; }

            public LevelGraph Build(IMissionConfigView config, ITilesetView tileset, IDeterministicRng rng)
            {
                WasCalled = true;
                return new LevelGraph(
                    new[] { new RoomNode("v1_start", RoomKind.Start, 1), new RoomNode("v1_exit", RoomKind.Exit, 1) },
                    new[] { new LevelGraphEdge("v1_start", "v1_exit", true) });
            }
        }

        private sealed class FakeRoomSelector : IRoomSelector
        {
            public IRoomTemplateDescriptor Select(
                RoomNode node,
                IMissionConfigView config,
                ITilesetView tileset,
                IRoomDatabase database,
                IDeterministicRng rng) =>
                new FakeRoom(node.Kind);
        }

        private sealed class SideBranchRejectingRoomSelector : IRoomSelector
        {
            public IRoomTemplateDescriptor Select(
                RoomNode node,
                IMissionConfigView config,
                ITilesetView tileset,
                IRoomDatabase database,
                IDeterministicRng rng)
            {
                if (node.NodeId.StartsWith("n_branch", StringComparison.Ordinal))
                {
                    return null;
                }

                return new FakeRoom(node.Kind);
            }
        }

        private sealed class FakeRoomPlacer : IRoomPlacer
        {
            public LevelLayout Place(
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
                IDeterministicRng rng,
                CancellationToken ct = default)
            {
                List<PlacedRoom> rooms = new(graph.Nodes.Count);
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    rooms.Add(new PlacedRoom(
                        graph.Nodes[i].NodeId,
                        selections[graph.Nodes[i].NodeId].RoomId,
                        new GridPose(i, 0, 0),
                        new Dictionary<string, DoorState>()));
                }

                return new LevelLayout(rooms, new DoorConnection[0], graph.Nodes[0].NodeId,
                    new[] { graph.Nodes[graph.Nodes.Count - 1].NodeId });
            }
        }

        private sealed class SideBranchOmittingRoomPlacer : IRoomPlacer
        {
            public LevelLayout Place(
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
                IDeterministicRng rng,
                CancellationToken ct = default)
            {
                List<PlacedRoom> rooms = new();
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    if (graph.Nodes[i].NodeId.StartsWith("n_branch", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    rooms.Add(new PlacedRoom(
                        graph.Nodes[i].NodeId,
                        selections[graph.Nodes[i].NodeId].RoomId,
                        new GridPose(i, 0, 0),
                        new Dictionary<string, DoorState>()));
                }

                return new LevelLayout(rooms, new DoorConnection[0], graph.Nodes[0].NodeId,
                    new[] { graph.Nodes[graph.Nodes.Count - 1].NodeId });
            }
        }

        private sealed class PassthroughDoorConnector : IDoorConnector
        {
            public LevelLayout Connect(
                LevelLayout layout,
                LevelGraph graph,
                IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections) =>
                layout;
        }

        private sealed class AlwaysValidValidator : ILevelValidator
        {
            public LevelValidationResult Validate(LevelLayout layout, IMissionConfigView config, ITilesetView tileset) => LevelValidationResult.Empty;
        }

        private sealed class FakeTilesetCatalog : ITilesetCatalog
        {
            public ITilesetView FindTileset(string tilesetId) => new FakeTileset();
        }

        private sealed class FakeTileset : ITilesetView
        {
            public string TilesetId => "test";
            public string DisplayName => "Test";
            public IntRange TotalRoomsRange => new(1, 3);
            public IntRange MainPathRoomsRange => new(1, 3);
            public IntRange BranchingRange => new(0, 0);
            public IntRange SideBranchesRange => new(0, 0);
            public IntRange DeadEndsRange => new(0, 0);
            public IntRange DependencyDistanceRange => new(0, 0);
            public float BonusRoomChance => 0f;
            public float SecretRoomChance => 0f;
            public float LogicRoomShare => 0f;
            public ExtractionMode DefaultExtractionMode => ExtractionMode.SeparateExitRoom;
            public ExtractionAvailability DefaultExtractionAvailability => ExtractionAvailability.AlwaysAvailable;
        }

        private sealed class FakeRoomDatabase : IRoomDatabase
        {
            public IReadOnlyList<IRoomTemplateDescriptor> AllRooms => new[] { new FakeRoom(RoomKind.Passage) };

            public IReadOnlyList<IRoomTemplateDescriptor> GetRoomsForTileset(string tilesetId) => AllRooms;
        }

        private sealed class FakeRoom : IRoomTemplateDescriptor
        {
            public FakeRoom(RoomKind kind)
            {
                Kind = kind;
                RoomId = kind + "_room";
            }

            public string RoomId { get; }
            public RoomKind Kind { get; }
            public string TilesetId => "test";
            public int Difficulty => 1;
            public int Weight => 1;
            public bool CanBeMainPath => true;
            public bool CanBeSideBranch => true;
            public bool CanBeStart => true;
            public bool CanBeExit => true;
            public bool CanBeDeadEnd => true;
            public IReadOnlyList<string> Tags => new string[0];
            public int EstimatedDurationSeconds => 1;
            public RoomFootprint Footprint => new(1, 1);
            public IReadOnlyList<IDoorSocketDescriptor> Doors => new IDoorSocketDescriptor[0];
            public string ModuleAssetId => RoomId;
        }

        private sealed class FakeRngFactory : IDeterministicRngFactory
        {
            public IDeterministicRng Create(int seed) => new FakeRng();
        }

        private sealed class FakeRng : IDeterministicRng
        {
            public int NextInt(int minInclusive, int maxExclusive) => minInclusive;

            public float NextFloat() => 0f;

            public void Shuffle<T>(IList<T> list)
            {
            }
        }
    }
}
