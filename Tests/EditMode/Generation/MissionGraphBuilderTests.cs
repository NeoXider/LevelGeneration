using System.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class MissionGraphBuilderTests
    {
        [Test]
        public void SimpleArchetype_BuildsStartInteractiveAndExit()
        {
            LevelGraph graph = Build(MissionArchetypeKind.Simple);

            Assert.AreEqual(RoomKind.Start, graph.Nodes[0].Kind);
            Assert.IsTrue(graph.Nodes.Any(node => node.Kind == RoomKind.Interactive));
            Assert.IsTrue(graph.Nodes.Any(node => node.Kind == RoomKind.Exit));
            Assert.IsTrue(graph.Edges.Any(edge => edge.FromNodeId == "n_start"));
        }

        [Test]
        public void CollectionArchetype_IncludesMultipleInteractiveRooms()
        {
            LevelGraph graph = Build(MissionArchetypeKind.Collection);

            Assert.GreaterOrEqual(graph.Nodes.Count(node => node.Kind == RoomKind.Interactive), 3);
        }

        [Test]
        public void FinalArchetype_IncludesInteractiveSequence()
        {
            LevelGraph graph = Build(MissionArchetypeKind.Final);

            Assert.GreaterOrEqual(graph.Nodes.Count(node => node.Kind == RoomKind.Interactive), 3);
            Assert.IsTrue(graph.Nodes.Any(node => node.Kind == RoomKind.Passage));
        }

        private static LevelGraph Build(
            MissionArchetypeKind archetype)
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            return builder.Build(new ArchetypeMissionConfig(archetype), new FakeTileset(), new FirstRollRng());
        }

        private sealed class ArchetypeMissionConfig : FakeMissionConfig
        {
            private readonly MissionArchetypeKind _archetype;

            public ArchetypeMissionConfig(MissionArchetypeKind archetype)
            {
                _archetype = archetype;
            }

            public override MissionArchetypeKind ArchetypeKind => _archetype;
        }
    }
}
