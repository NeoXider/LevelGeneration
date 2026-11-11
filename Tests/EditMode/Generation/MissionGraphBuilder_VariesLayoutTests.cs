using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class MissionGraphBuilder_VariesLayoutTests
    {
        [Test]
        public void DifferentRngRolls_CanProduceDifferentSkeletons()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            VariableMissionConfig config = new();
            FakeTileset tileset = new();

            LevelGraph compact = builder.Build(config, tileset, new SequenceRng(4, 0, 0));
            LevelGraph expanded = builder.Build(config, tileset, new SequenceRng(7, 14, 3, 2, 2, 3));

            Assert.AreNotEqual(compact.Nodes.Count, expanded.Nodes.Count);
            Assert.Less(compact.Nodes.Count, expanded.Nodes.Count);
        }

        [Test]
        public void TotalRoomsRange_FillsBeyondMainPathWithOptionalRooms()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new TotalAboveMainMissionConfig(),
                new FakeTileset(),
                new SequenceRng(5, 12, 4, 0, 1, 2, 0, 0, 0, 0));

            Assert.AreEqual(12, graph.Nodes.Count);
            bool hasOptionalEdge = false;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                if (graph.Edges[i].IsMainPath)
                {
                    continue;
                }

                hasOptionalEdge = true;
                break;
            }

            Assert.IsTrue(hasOptionalEdge);
        }

        [Test]
        public void DeadEndsRange_MarksOnlyRequestedOptionalChainTails()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new DeadEndRangeMissionConfig(),
                new FakeTileset(),
                new SequenceRng(4, 8, 4, 0, 1, 2, 0, 0, 0, 0));

            Assert.AreEqual(8, graph.Nodes.Count);

            int optionalDeadEnds = 0;
            int optionalTunnels = 0;
            int optionalCount = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                if (node.IsOnMainPath)
                {
                    continue;
                }

                optionalCount++;
                if (node.IsDeadEnd)
                {
                    optionalDeadEnds++;
                }

                if (!node.IsDeadEnd && node.Kind == RoomKind.Passage)
                {
                    optionalTunnels++;
                }
            }

            Assert.AreEqual(3, optionalCount, "expected three branch-chain rooms");
            Assert.AreEqual(1, optionalDeadEnds,
                "only the tail of one optional chain should be marked as a requested dead end");
            Assert.AreEqual(2, optionalTunnels, "remaining optional rooms should stay passage fillers");
        }

        [Test]
        public void SimpleArchetype_WithOnlyFallbackHosts_AddsBranchRoomsFromMainPath()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new SimpleBranchFallbackMissionConfig(),
                new FakeTileset(),
                new SequenceRng(4, 8, 4, 0, 1, 2, 2, 0, 1));

            Assert.AreEqual(8, graph.Nodes.Count);

            int branchEdges = 0;
            int branchFromMainPath = 0;
            int branchFromInvalidHost = 0;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (edge.IsMainPath)
                {
                    continue;
                }

                if (!edge.ToNodeId.StartsWith("n_branch_", System.StringComparison.Ordinal))
                {
                    continue;
                }

                branchEdges++;
                if (edge.FromNodeId.StartsWith("n_main_", System.StringComparison.Ordinal))
                {
                    branchFromMainPath++;
                }
                else
                {
                    branchFromInvalidHost++;
                }
            }

            Assert.AreEqual(2, branchEdges);
            Assert.AreEqual(2, branchFromMainPath);
            Assert.AreEqual(0, branchFromInvalidHost);
        }

        [Test]
        public void DeadEndsRangeZero_DoesNotMarkOptionalRoomsAsRequestedDeadEnds()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new SimpleNoDeadEndsFallbackMissionConfig(),
                new FakeTileset(),
                new SequenceRng(4, 8, 4, 0, 1, 2, 0, 0, 0, 0));

            int optionalDeadEnds = 0;
            int optionalCount = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                if (node.IsOnMainPath)
                {
                    continue;
                }

                optionalCount++;
                if (node.IsDeadEnd)
                {
                    optionalDeadEnds++;
                }
            }

            Assert.AreEqual(3, optionalCount, "expected three branch-chain rooms");
            Assert.AreEqual(0, optionalDeadEnds, "requested dead ends should come only from DeadEndsRange");
        }

        [Test]
        public void BranchingRange_OneBuildsTunnelWithoutOptionalEdges()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new TunnelBranchingMissionConfig(),
                new FakeTileset(),
                new SequenceRng(4, 10, 1));

            Assert.AreEqual(10, graph.Nodes.Count);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                Assert.IsTrue(graph.Edges[i].IsMainPath, graph.Edges[i].ToNodeId);
            }
        }

        [Test]
        public void BranchingRange_TwoUsesOneBranchPointForOptionalRooms()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new OneBranchPointMissionConfig(),
                new FakeTileset(),
                new SequenceRng(5, 10, 2, 0, 0, 0, 0));

            string branchHost = null;
            int branchRootEdges = 0;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (edge.IsMainPath)
                {
                    continue;
                }

                if (!edge.ToNodeId.StartsWith("n_branch_", System.StringComparison.Ordinal))
                {
                    continue;
                }

                branchRootEdges++;
                if (branchHost == null)
                {
                    branchHost = edge.FromNodeId;
                }

                Assert.AreEqual(branchHost, edge.FromNodeId);
            }

            Assert.AreEqual(1, branchRootEdges);
        }

        [Test]
        public void BranchingRange_ReservesOptionalRoomsWhenTotalEqualsMainPath()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new BranchingTotalEqualsMainMissionConfig(),
                new FakeTileset(),
                new SequenceRng(10, 10, 3, 0, 0, 0, 0));

            int optionalEdges = 0;
            bool hasBranchHostWithThreeDoors = false;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                if (!graph.Edges[i].IsMainPath)
                {
                    optionalEdges++;
                    RoomNode host = FindNode(graph, graph.Edges[i].FromNodeId);
                    hasBranchHostWithThreeDoors |= host != null && host.RequiredDoorCount >= 3;
                }
            }

            Assert.AreEqual(10, graph.Nodes.Count);
            Assert.Greater(optionalEdges, 0);
            Assert.IsTrue(hasBranchHostWithThreeDoors);
        }

        [Test]
        public void AllowLoops_CanReconnectOptionalTunnelToMainPath()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new LoopingBranchMissionConfig(),
                new FakeTileset(),
                new SequenceRng(5, 8, 2, 0, 0, 0, 0, 0));

            bool hasReconnect = false;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (edge.IsMainPath)
                {
                    continue;
                }

                if ((edge.FromNodeId.StartsWith("n_optional_", System.StringComparison.Ordinal)
                     || edge.FromNodeId.StartsWith("n_branch_", System.StringComparison.Ordinal))
                    && edge.ToNodeId.StartsWith("n_main_", System.StringComparison.Ordinal))
                {
                    hasReconnect = true;
                    break;
                }
            }

            Assert.IsTrue(hasReconnect);
        }

        [Test]
        public void RoomKindSpawnRule_CountRangeEnsuresMinimumRooms()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new RoomKindRuleMissionConfig(new RoomKindRule(RoomKind.Interactive, new OptionalIntRange(2, 2))),
                new FakeTileset(),
                new SequenceRng(4, 4, 1, 2, 0, 0, 0));

            Assert.AreEqual(2, CountKind(graph, RoomKind.Interactive));
            Assert.Greater(CountOptionalEdges(graph), 0);
        }

        [Test]
        public void RoomKindSpawnRule_CountRangeDoesNotAddWhenSpineAlreadyMeetsMinimum()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new CollectionRoomKindRuleMissionConfig(new RoomKindRule(RoomKind.Interactive,
                    new OptionalIntRange(2, 2))),
                new FakeTileset(),
                new SequenceRng(6, 6, 1, 2, 0, 0, 0));

            Assert.AreEqual(3, CountKind(graph, RoomKind.Interactive));
            Assert.AreEqual(0, CountOptionalKind(graph, RoomKind.Interactive));
        }

        [Test]
        public void RoomKindSpawnRule_ChanceCanAddOneRoom()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new RoomKindRuleMissionConfig(new RoomKindRule(RoomKind.Interactive, 1f)),
                new FakeTileset(),
                new SequenceRng(4, 4, 1, 0, 0, 0));

            Assert.AreEqual(1, CountOptionalKind(graph, RoomKind.Interactive));
        }

        [Test]
        public void RoomKindSpawnRule_ZeroChanceDoesNotAddRoom()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new RoomKindRuleMissionConfig(new RoomKindRule(RoomKind.Interactive, 0f)),
                new FakeTileset(),
                new SequenceRng(4, 4, 1));

            Assert.AreEqual(0, CountOptionalKind(graph, RoomKind.Interactive));
        }

        [Test]
        public void RoomKindSpawnRule_AutomaticChanceSharesBetweenChanceRules()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new RoomKindRuleMissionConfig(
                    RoomKindRule.AutoChance(RoomKind.Interactive),
                    RoomKindRule.AutoChance(RoomKind.Passage)),
                new FakeTileset(),
                new SequenceRng(4, 4, 1, 0, 0, 0));

            Assert.AreEqual(1, CountOptionalKind(graph, RoomKind.Interactive));
            Assert.AreEqual(1, CountOptionalKind(graph, RoomKind.Passage));
        }

        [Test]
        public void MissionRoomRule_CountRangeAddsPreferredTemplateNodes()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new MissionRoomRuleConfig(new MissionRoomRule("room_bonus_a", RoomKind.Interactive,
                    new OptionalIntRange(2, 2))),
                new FakeTileset(),
                new SequenceRng(4, 4, 1, 2, 0, 0, 0));

            int count = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (graph.Nodes[i].PreferredRoomTemplateId == "room_bonus_a")
                {
                    count++;
                }
            }

            Assert.AreEqual(2, count);
        }

        [Test]
        public void InteractiveContent_AddsOneContentNodePerQuestion()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new InteractiveContentMissionConfig(
                    new InteractiveContent("Q_MAZE", TestInteractiveRoomKinds.MazeBranch, 4, 1),
                    new InteractiveContent("Q_LEVER", TestInteractiveRoomKinds.LeverPanel, 4, 2),
                    new InteractiveContent("Q_PASSWORD", TestInteractiveRoomKinds.PasswordDoor, 1, 1)),
                new FakeTileset(),
                new SequenceRng(4, 4, 1, 0, 0, 0, 0));

            Assert.AreEqual(3, CountContentNodes(graph));
            Assert.IsTrue(HasContentNode(graph, "Q_MAZE"));
            Assert.IsTrue(HasContentNode(graph, "Q_LEVER"));
            Assert.IsTrue(HasContentNode(graph, "Q_PASSWORD"));
        }

        [Test]
        public void MazeBranchContent_StaysOnMainPathAndAddsWrongAnswerDeadEndTunnels()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new InteractiveContentMissionConfig(
                    new InteractiveContent("Q_MAZE", TestInteractiveRoomKinds.MazeBranch, 4, 1)),
                new FakeTileset(),
                new SequenceRng(4, 4, 1, 0, 0, 0, 0));

            RoomNode maze = FindNodeByContent(graph, "Q_MAZE");

            Assert.NotNull(maze);
            Assert.IsTrue(maze.IsOnMainPath);
            Assert.AreEqual(5, maze.RequiredDoorCount);
            Assert.AreEqual(3, CountOptionalEdgesFrom(graph, maze.NodeId));
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (edge.IsMainPath || edge.FromNodeId != maze.NodeId)
                {
                    continue;
                }

                RoomNode wrongTunnel = FindNode(graph, edge.ToNodeId);
                Assert.NotNull(wrongTunnel);
                Assert.AreEqual(RoomKind.Passage, wrongTunnel.Kind);
                Assert.AreEqual("Toonel", wrongTunnel.PreferredRoomTemplateId);
            }
        }

        [Test]
        public void HybridSequence_ExpandsOtherSlotsAndKeepsAnchorOrder()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new HybridSequenceMissionConfig(
                    HybridStep.Other(2, 2),
                    HybridStep.ForKind(RoomKind.Interactive),
                    HybridStep.Other(1, 1),
                    HybridStep.ForKind(RoomKind.Interactive)),
                new FakeTileset(),
                new SequenceRng(3, 3, 1, 2, 1));

            Assert.AreEqual(7, graph.Nodes.Count);
            Assert.AreEqual(RoomKind.Start, graph.Nodes[0].Kind);
            Assert.AreEqual(RoomKind.Passage, graph.Nodes[1].Kind);
            Assert.AreEqual(RoomKind.Passage, graph.Nodes[2].Kind);
            Assert.AreEqual(RoomKind.Interactive, graph.Nodes[3].Kind);
            Assert.AreEqual(RoomKind.Passage, graph.Nodes[4].Kind);
            Assert.AreEqual(RoomKind.Interactive, graph.Nodes[5].Kind);
            Assert.AreEqual(RoomKind.Exit, graph.Nodes[6].Kind);
        }

        [Test]
        public void HybridSequence_RoomPrefabStepCarriesPreferredTemplateId()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new HybridSequenceMissionConfig(
                    HybridStep.Other(1, 1),
                    HybridStep.Prefab("room_key_vault", RoomKind.Interactive),
                    HybridStep.ForKind(RoomKind.Interactive)),
                new FakeTileset(),
                new SequenceRng(3, 3, 1, 1));

            RoomNode preferred = FindNodeByPreferredRoom(graph, "room_key_vault");
            Assert.NotNull(preferred);
            Assert.AreEqual(RoomKind.Interactive, preferred.Kind);
            Assert.AreEqual(2, preferred.RequiredDoorCount);
        }

        [Test]
        public void ScriptedSequence_BuildsExactPathAndIgnoresOtherAndBranches()
        {
            MissionGraphBuilder builder = new(TestInteractiveRoomKinds.Standard);
            LevelGraph graph = builder.Build(
                new ScriptedSequenceMissionConfig(
                    HybridStep.ForKind(RoomKind.Start),
                    HybridStep.Other(4, 4),
                    HybridStep.ForKind(RoomKind.Interactive),
                    HybridStep.Prefab("room_objective_a", RoomKind.Interactive),
                    HybridStep.ForKind(RoomKind.Exit)),
                new FakeTileset(),
                new SequenceRng(20, 20, 5));

            Assert.AreEqual(4, graph.Nodes.Count);
            Assert.AreEqual(RoomKind.Start, graph.Nodes[0].Kind);
            Assert.AreEqual(RoomKind.Interactive, graph.Nodes[1].Kind);
            Assert.AreEqual(RoomKind.Interactive, graph.Nodes[2].Kind);
            Assert.AreEqual("room_objective_a", graph.Nodes[2].PreferredRoomTemplateId);
            Assert.AreEqual(RoomKind.Exit, graph.Nodes[3].Kind);
            Assert.AreEqual(0, CountOptionalEdges(graph));
        }

        private sealed class VariableMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.WithSideBranches;
            public override OptionalIntRange MainPathRoomsRange => new(4, 7);
            public override OptionalIntRange SideBranchesRange => new(0, 2);
            public override OptionalIntRange DeadEndsRange => new(0, 2);
        }

        private sealed class TotalAboveMainMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Collection;
            public override OptionalIntRange TotalRoomsRange => new(12, 12);
            public override OptionalIntRange MainPathRoomsRange => new(5, 5);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
        }

        private sealed class DeadEndRangeMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
            public override OptionalIntRange TotalRoomsRange => new(8, 8);
            public override OptionalIntRange MainPathRoomsRange => new(4, 4);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(3, 3);
        }

        private static RoomNode FindNode(LevelGraph graph, string nodeId)
        {
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (graph.Nodes[i].NodeId == nodeId)
                {
                    return graph.Nodes[i];
                }
            }

            return null;
        }

        private static int CountKind(LevelGraph graph, RoomKind kind)
        {
            int count = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (graph.Nodes[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }


        private static int CountOptionalKind(LevelGraph graph, RoomKind kind)
        {
            int count = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (!graph.Nodes[i].IsOnMainPath && graph.Nodes[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountOptionalEdges(LevelGraph graph)
        {
            int count = 0;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                if (!graph.Edges[i].IsMainPath)
                {
                    count++;
                }
            }

            return count;
        }

        private static RoomNode FindNodeByPreferredRoom(LevelGraph graph, string roomId)
        {
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (graph.Nodes[i].PreferredRoomTemplateId == roomId)
                {
                    return graph.Nodes[i];
                }
            }

            return null;
        }

        private static int CountContentNodes(LevelGraph graph)
        {
            int count = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(graph.Nodes[i].ContentId))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasContentNode(LevelGraph graph, string contentId)
        {
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (graph.Nodes[i].Kind == RoomKind.Interactive
                    && string.Equals(graph.Nodes[i].ContentId, contentId, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static RoomNode FindNodeByContent(LevelGraph graph, string contentId)
        {
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (string.Equals(graph.Nodes[i].ContentId, contentId, System.StringComparison.Ordinal))
                {
                    return graph.Nodes[i];
                }
            }

            return null;
        }

        private static int CountOptionalEdgesFrom(LevelGraph graph, string nodeId)
        {
            int count = 0;
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                if (!graph.Edges[i].IsMainPath
                    && string.Equals(graph.Edges[i].FromNodeId, nodeId, System.StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private sealed class TunnelBranchingMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
            public override OptionalIntRange TotalRoomsRange => new(10, 10);
            public override OptionalIntRange MainPathRoomsRange => new(4, 4);
            public override OptionalIntRange BranchingRange => new(1, 1);
            public override OptionalIntRange SideBranchesRange => new(5, 5);
            public override OptionalIntRange DeadEndsRange => new(5, 5);
        }

        private sealed class OneBranchPointMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Collection;
            public override OptionalIntRange TotalRoomsRange => new(10, 10);
            public override OptionalIntRange MainPathRoomsRange => new(5, 5);
            public override OptionalIntRange BranchingRange => new(2, 2);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
        }

        private sealed class LoopingBranchMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Collection;
            public override OptionalIntRange TotalRoomsRange => new(8, 8);
            public override OptionalIntRange MainPathRoomsRange => new(5, 5);
            public override OptionalIntRange BranchingRange => new(2, 2);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
            public override bool AllowLoops => true;
        }

        private sealed class BranchingTotalEqualsMainMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Collection;
            public override OptionalIntRange TotalRoomsRange => new(10, 10);
            public override OptionalIntRange MainPathRoomsRange => new(10, 10);
            public override OptionalIntRange BranchingRange => new(3, 3);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
        }

        private sealed class SimpleBranchFallbackMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
            public override OptionalIntRange TotalRoomsRange => new(8, 8);
            public override OptionalIntRange MainPathRoomsRange => new(4, 4);
            public override OptionalIntRange SideBranchesRange => new(2, 2);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
        }

        private sealed class SimpleNoDeadEndsFallbackMissionConfig : GraphBuilderTestMissionConfig
        {
            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
            public override OptionalIntRange TotalRoomsRange => new(8, 8);
            public override OptionalIntRange MainPathRoomsRange => new(4, 4);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
        }

        private class RoomKindRuleMissionConfig : GraphBuilderTestMissionConfig
        {
            private readonly System.Collections.Generic.IReadOnlyList<IRoomKindSpawnRule> _rules;

            public RoomKindRuleMissionConfig(params IRoomKindSpawnRule[] rules)
            {
                _rules = rules;
            }

            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
            public override OptionalIntRange TotalRoomsRange => new(4, 4);
            public override OptionalIntRange MainPathRoomsRange => new(4, 4);
            public override OptionalIntRange BranchingRange => new(1, 1);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
            public override System.Collections.Generic.IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => _rules;
        }

        private sealed class MissionRoomRuleConfig : GraphBuilderTestMissionConfig
        {
            private readonly System.Collections.Generic.IReadOnlyList<IMissionRoomRule> _rules;

            public MissionRoomRuleConfig(params IMissionRoomRule[] rules)
            {
                _rules = rules;
            }

            public override OptionalIntRange TotalRoomsRange => new(4, 4);
            public override OptionalIntRange MainPathRoomsRange => new(4, 4);
            public override OptionalIntRange BranchingRange => new(1, 1);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
            public override System.Collections.Generic.IReadOnlyList<IMissionRoomRule> MissionRoomRules => _rules;
        }

        private sealed class InteractiveContentMissionConfig : GraphBuilderTestMissionConfig,
            IPlannedInteractiveMissionContentProvider
        {
            public InteractiveContentMissionConfig(params IInteractiveMissionContentSpec[] content)
            {
                PlannedInteractiveContent = content;
            }

            public override OptionalIntRange TotalRoomsRange => new(4, 4);
            public override OptionalIntRange MainPathRoomsRange => new(4, 4);
            public override OptionalIntRange BranchingRange => new(1, 1);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);

            public System.Collections.Generic.IReadOnlyList<IInteractiveMissionContentSpec> PlannedInteractiveContent { get; }
        }

        private sealed class InteractiveContent : IInteractiveMissionContentSpec
        {
            public InteractiveContent(string contentId, string interactiveKind, int optionCount, int correctAnswerCount)
            {
                ContentId = contentId;
                InteractiveKind = interactiveKind;
                OptionCount = optionCount;
                CorrectAnswerCount = correctAnswerCount;
            }

            public string ContentId { get; }
            public string InteractiveKind { get; }
            public int OptionCount { get; }
            public int CorrectAnswerCount { get; }
            public bool IsMandatory => true;
        }

        private sealed class HybridSequenceMissionConfig : GraphBuilderTestMissionConfig
        {
            private readonly System.Collections.Generic.IReadOnlyList<IHybridSequenceStep> _sequence;

            public HybridSequenceMissionConfig(params IHybridSequenceStep[] sequence)
            {
                _sequence = sequence;
            }

            public override LevelGenerationMode GenerationMode => LevelGenerationMode.Hybrid;
            public override OptionalIntRange TotalRoomsRange => new(3, 3);
            public override OptionalIntRange MainPathRoomsRange => new(3, 3);
            public override OptionalIntRange BranchingRange => new(1, 1);
            public override OptionalIntRange SideBranchesRange => new(0, 0);
            public override OptionalIntRange DeadEndsRange => new(0, 0);
            public override System.Collections.Generic.IReadOnlyList<IHybridSequenceStep> HybridSequence => _sequence;
        }

        private sealed class ScriptedSequenceMissionConfig : GraphBuilderTestMissionConfig
        {
            private readonly System.Collections.Generic.IReadOnlyList<IHybridSequenceStep> _sequence;

            public ScriptedSequenceMissionConfig(params IHybridSequenceStep[] sequence)
            {
                _sequence = sequence;
            }

            public override LevelGenerationMode GenerationMode => LevelGenerationMode.Scripted;
            public override OptionalIntRange TotalRoomsRange => new(20, 20);
            public override OptionalIntRange MainPathRoomsRange => new(20, 20);
            public override OptionalIntRange BranchingRange => new(5, 5);
            public override OptionalIntRange SideBranchesRange => new(8, 8);
            public override OptionalIntRange DeadEndsRange => new(8, 8);
            public override System.Collections.Generic.IReadOnlyList<IHybridSequenceStep> HybridSequence => _sequence;
        }

        private sealed class HybridStep : IHybridSequenceStep
        {
            private HybridStep(
                HybridSequenceSlotKind kind,
                RoomKind roomKind,
                string roomId,
                OptionalIntRange otherCountRange)
            {
                Kind = kind;
                RoomKind = roomKind;
                RoomId = roomId;
                OtherCountRange = otherCountRange;
            }

            public static HybridStep Other(int min, int max)
            {
                return new HybridStep(HybridSequenceSlotKind.Other, RoomKind.Passage, null,
                    new OptionalIntRange(min, max));
            }

            public static HybridStep ForKind(RoomKind kind) => new(HybridSequenceSlotKind.RoomKind, kind, null, default);

            public static HybridStep Prefab(string roomId, RoomKind kind) => new(HybridSequenceSlotKind.RoomPrefab, kind, roomId, default);

            public HybridSequenceSlotKind Kind { get; }
            public RoomKind RoomKind { get; }
            public string RoomId { get; }
            public OptionalIntRange OtherCountRange { get; }
        }

        private sealed class MissionRoomRule : IMissionRoomRule
        {
            public MissionRoomRule(string roomId, RoomKind kind, OptionalIntRange countRange)
            {
                RoomId = roomId;
                Kind = kind;
                CountRange = countRange;
            }

            public string RoomId { get; }
            public RoomKind Kind { get; }
            public bool HasSpawnRule => true;
            public RoomTypeSpawnMode Mode => RoomTypeSpawnMode.CountRange;
            public bool OverrideChance => false;
            public float Chance => 0f;
            public OptionalIntRange CountRange { get; }
            public bool OverrideWeight => false;
            public int Weight => 100;
        }

        private sealed class CollectionRoomKindRuleMissionConfig : RoomKindRuleMissionConfig
        {
            public CollectionRoomKindRuleMissionConfig(params IRoomKindSpawnRule[] rules)
                : base(rules)
            {
            }

            public override MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Collection;
            public override OptionalIntRange TotalRoomsRange => new(6, 6);
            public override OptionalIntRange MainPathRoomsRange => new(6, 6);
        }

        private sealed class RoomKindRule : IRoomKindSpawnRule
        {
            public RoomKindRule(RoomKind kind, float chance)
            {
                Kind = kind;
                Mode = RoomTypeSpawnMode.Chance;
                OverrideChance = true;
                Chance = chance;
                CountRange = default;
            }

            public RoomKindRule(RoomKind kind, OptionalIntRange countRange)
            {
                Kind = kind;
                Mode = RoomTypeSpawnMode.CountRange;
                OverrideChance = false;
                Chance = 0f;
                CountRange = countRange;
            }

            private RoomKindRule(RoomKind kind)
            {
                Kind = kind;
                Mode = RoomTypeSpawnMode.Chance;
                OverrideChance = false;
                Chance = 0f;
                CountRange = default;
            }

            public static RoomKindRule AutoChance(RoomKind kind) => new(kind);

            public RoomKind Kind { get; }
            public RoomTypeSpawnMode Mode { get; }
            public bool OverrideChance { get; }
            public float Chance { get; }
            public OptionalIntRange CountRange { get; }
        }
    }
}
