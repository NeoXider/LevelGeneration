using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class MissionGraphBuilder_SplitterMergerSymmetryTests
    {
        [Test]
        public void SplitterSideBranches_StartPhysicalBranchChains()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new SplitterMissionConfig(),
                new FakeTileset(),
                new SequenceRng(5, 1, 3, 0));

            Dictionary<string, RoomNode> byId = new();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                byId[graph.Nodes[i].NodeId] = graph.Nodes[i];
            }

            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                if (node.RequiredDoorCount < 3)
                {
                    continue;
                }

                bool hasCheckedBranch = false;
                for (int e = 0; e < graph.Edges.Count; e++)
                {
                    LevelGraphEdge edge = graph.Edges[e];
                    if (edge.FromNodeId != node.NodeId || edge.IsMainPath)
                    {
                        continue;
                    }

                    hasCheckedBranch = true;
                    Assert.IsFalse(byId[edge.ToNodeId].IsOnMainPath, edge.ToNodeId);
                }

                Assert.IsTrue(hasCheckedBranch, "Expected at least one side branch from the splitter.");
            }
        }

        private sealed class SplitterMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.WithSideBranches;
            public override OptionalIntRange MainPathRoomsRange => new(5, 5);
            public override OptionalIntRange SideBranchesRange => new(1, 1);
        }
    }
}
