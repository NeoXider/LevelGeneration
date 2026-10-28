using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class MissionGraphBuilder_RequiredDoorCountTests
    {
        [Test]
        public void Build_DerivesRequiredDoorCountFromGraphDegree()
        {
            LevelGraph graph = new MissionGraphBuilder(TestInteractiveRoomKinds.Standard).Build(
                new BranchingMissionConfig(),
                new FakeTileset(),
                new SequenceRng(5, 10, 3, 0, 0, 0, 0));

            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                Assert.AreEqual(CountGraphDegree(graph, node.NodeId), node.RequiredDoorCount, node.NodeId);
            }
        }

        [Test]
        public void DeadEndsRange_ControlsDeadEndNodes()
        {
            LevelGraph graph = new MissionGraphBuilder(TestInteractiveRoomKinds.Standard).Build(
                new BranchingMissionConfig(),
                new FakeTileset(),
                new SequenceRng(5, 10, 3, 0, 0, 0, 0));

            int deadEnds = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (!graph.Nodes[i].IsOnMainPath && graph.Nodes[i].IsDeadEnd)
                {
                    deadEnds++;
                }
            }

            Assert.AreEqual(0, deadEnds);
        }

        private static int CountGraphDegree(LevelGraph graph, string nodeId)
        {
            int degree = 0;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                if (graph.Edges[i].FromNodeId == nodeId)
                {
                    degree++;
                }

                if (graph.Edges[i].ToNodeId == nodeId)
                {
                    degree++;
                }
            }

            return degree;
        }

        private sealed class BranchingMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.WithSideBranches;
            public override OptionalIntRange TotalRoomsRange => new(10, 10);
            public override OptionalIntRange MainPathRoomsRange => new(5, 5);
            public override OptionalIntRange BranchingRange => new(3, 3);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
        }
    }
}
