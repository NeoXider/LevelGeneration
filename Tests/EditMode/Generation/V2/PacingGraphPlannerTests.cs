using System.Linq;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application.V2.Domain;
using Neoxider.LevelGeneration.Generation.Application.V2.Facade;
using Neoxider.LevelGeneration.Generation.Domain.Enums;

namespace Neoxider.LevelGeneration.Tests.Generation.V2
{
    [Category("LevelGeneration")]
    public sealed class PacingGraphPlannerTests
    {
        [Test]
        public void FourMandatoryInteractive_CreateFourMainInteractiveNodes()
        {
            LayoutGenerationResult result = Generate(11, Mandatory("q1"), Mandatory("q2"), Mandatory("q3"),
                Mandatory("q4"));

            PacingGraphNode[] interactiveNodes = result.Graph.Nodes
                .Where(node => node.Kind == RoomKind.Interactive && node.IsOnMainPath).ToArray();

            Assert.That(interactiveNodes.Length, Is.EqualTo(4));
            Assert.That(interactiveNodes.Select(node => node.ContentId), Is.EqualTo(new[] { "q1", "q2", "q3", "q4" }));
        }

        [Test]
        public void MainPath_GapsAreWithinConfiguredRange()
        {
            LayoutPacingConfig config = new(
                new PacingIntRange(1, 3),
                new PacingIntRange(0, 0),
                new WeightedBranchLengthConfig(new PacingIntRange(0, 0), 0f),
                0,
                5f,
                LayoutRetryPolicy.FailFast,
                0);

            LayoutGenerationResult result = Generate(42, config, Mandatory("q1"), Mandatory("q2"), Mandatory("q3"));
            PacingGraph graph = result.Graph;
            PacingGraphNode[] mainInteractive =
                graph.Nodes.Where(node => node.Kind == RoomKind.Interactive && node.IsOnMainPath).ToArray();

            for (int i = 0; i < mainInteractive.Length - 1; i++)
            {
                string from = mainInteractive[i].NodeId + "_answer_correct";
                string to = mainInteractive[i + 1].NodeId;
                int gap = CountPassagesBetween(graph, from, to);
                Assert.That(gap, Is.InRange(1, 3));
            }
        }

        [Test]
        public void Exit_IsReachableFromStart()
        {
            LayoutGenerationResult result = Generate(42, Mandatory("q1"));

            Assert.That(result.Graph.IsReachable("n_start", "n_exit"), Is.True);
        }

        [Test]
        public void OptionalContent_IsPlacedOnlyOffMainPath()
        {
            LayoutPacingConfig config = LayoutPacingDefaults.StableRuntime();
            LayoutGenerationRequest request = new(
                42,
                config,
                new MissionContentPlan(new[] { Mandatory("required") }, new[] { Optional("optional") }));
            LayoutGenerationResult result = new LayoutPacingMissionGenerator().Generate(request);

            PacingGraphNode optionalNode = result.Graph.Nodes.Single(node => node.ContentId == "optional");

            Assert.That(optionalNode.IsOnMainPath, Is.False);
            Assert.That(optionalNode.BranchPurpose, Is.EqualTo(BranchPurpose.OptionalInteractive));
        }

        [Test]
        public void QuizBranch_CreatesAnswerTunnels()
        {
            LayoutGenerationResult result = Generate(11, Mandatory("q1"));
            PacingGraphNode quizNode = result.Graph.Nodes.Single(node => node.ContentId == "q1");
            PacingGraphEdge[] answerEdges = result.Graph.Edges.Where(edge =>
                edge.FromNodeId == quizNode.NodeId && (edge.Label == "correct" || edge.Label == "wrong")).ToArray();
            PacingGraphEdge correctEdge = answerEdges.Single(edge => edge.Label == "correct");
            PacingGraphEdge[] wrongEdges = answerEdges.Where(edge => edge.Label == "wrong").ToArray();

            Assert.That(answerEdges.Length, Is.EqualTo(4));
            Assert.That(correctEdge.IsMainPath, Is.True);
            Assert.That(wrongEdges.Length, Is.EqualTo(3));
            Assert.That(wrongEdges.All(edge => edge.IsMainPath == false), Is.True);
            Assert.That(wrongEdges.All(edge => result.Graph.FindNode(edge.ToNodeId).IsTerminal), Is.True);
            Assert.That(
                wrongEdges.All(edge =>
                    result.Graph.FindNode(edge.ToNodeId).BranchPurpose == BranchPurpose.WrongAnswerDeadEnd), Is.True);
            Assert.That(quizNode.RequiredDoorCount, Is.EqualTo(5));
        }

        [Test]
        public void FourOptionQuizBranch_AddsCorrectAnswerBufferBeforeNextMainRoom()
        {
            LayoutPacingConfig config = new(
                new PacingIntRange(0, 0),
                new PacingIntRange(0, 0),
                new WeightedBranchLengthConfig(new PacingIntRange(0, 0), 0f),
                0,
                5f,
                LayoutRetryPolicy.FailFast,
                0);

            LayoutGenerationResult result = Generate(11, config, Mandatory("q1"), Mandatory("q2"));
            PacingGraphEdge firstCorrectEdge =
                result.Graph.Edges.Single(edge => edge.FromNodeId == "n_main_interactive_0" && edge.Label == "correct");
            PacingGraphEdge bufferEdge =
                result.Graph.Edges.Single(edge =>
                    edge.FromNodeId == firstCorrectEdge.ToNodeId && edge.Label == "correct_buffer");
            PacingGraphEdge nextMainEdge =
                result.Graph.Edges.Single(edge => edge.FromNodeId == bufferEdge.ToNodeId && edge.IsMainPath);

            Assert.That(result.Graph.FindNode(firstCorrectEdge.ToNodeId).Source, Is.EqualTo("quiz_correct_tunnel"));
            Assert.That(result.Graph.FindNode(bufferEdge.ToNodeId).Source, Is.EqualTo("quiz_correct_buffer"));
            Assert.That(nextMainEdge.ToNodeId, Is.EqualTo("n_main_interactive_1"));
        }

        [Test]
        public void Branches_AttachOnlyToMainPathPassageNodes()
        {
            LayoutPacingConfig config = new(
                new PacingIntRange(1, 1),
                new PacingIntRange(3, 3),
                new WeightedBranchLengthConfig(new PacingIntRange(1, 1), 0f),
                0,
                5f,
                LayoutRetryPolicy.FailFast,
                0);

            LayoutGenerationResult result = Generate(42, config, Mandatory("q1"), Mandatory("q2"));
            PacingGraphEdge[] branchEdges = result.Graph.Edges.Where(edge => edge.Label == "branch").ToArray();

            Assert.That(branchEdges.Length, Is.EqualTo(3));
            Assert.That(branchEdges.All(edge => result.Graph.FindNode(edge.FromNodeId).Kind == RoomKind.Passage),
                Is.True);
            Assert.That(branchEdges.All(edge => result.Graph.FindNode(edge.FromNodeId).IsOnMainPath), Is.True);
        }

        [Test]
        public void SideBranchCountZero_LeavesGraphValid()
        {
            LayoutPacingConfig config = new(
                new PacingIntRange(1, 1),
                new PacingIntRange(0, 0),
                new WeightedBranchLengthConfig(new PacingIntRange(0, 0), 0f),
                0,
                5f,
                LayoutRetryPolicy.FailFast,
                0);

            LayoutGenerationResult result = Generate(633659764, config, Mandatory("q1"));

            Assert.That(result.Success, Is.True);
            Assert.That(result.Graph.Edges.Any(edge => edge.Label == "branch"), Is.False);
        }

        [Test]
        public void OptionalFillerCap_LimitsOrdinarySideBranchRooms()
        {
            LayoutPacingConfig config = new(
                new PacingIntRange(1, 1),
                new PacingIntRange(3, 3),
                new WeightedBranchLengthConfig(new PacingIntRange(3, 3), 0f),
                0,
                5f,
                LayoutRetryPolicy.FailFast,
                2);

            LayoutGenerationResult result = Generate(42, config, Mandatory("q1"), Mandatory("q2"));
            PacingGraphNode[] fillerBranchRooms = result.Graph.Nodes
                .Where(node => !node.IsOnMainPath && node.BranchPurpose == BranchPurpose.Filler)
                .ToArray();

            Assert.That(fillerBranchRooms.Length, Is.LessThanOrEqualTo(2));
        }

        private static LayoutGenerationResult Generate(int seed, params PlannedInteractiveContent[] mandatory) => Generate(seed, LayoutPacingDefaults.StableRuntime(), mandatory);

        private static LayoutGenerationResult Generate(int seed, LayoutPacingConfig config,
            params PlannedInteractiveContent[] mandatory)
        {
            LayoutGenerationRequest request = new(seed, config,
                new MissionContentPlan(mandatory, new PlannedInteractiveContent[0]));
            return new LayoutPacingMissionGenerator().Generate(request);
        }

        private static PlannedInteractiveContent Mandatory(string id)
        {
            return new PlannedInteractiveContent(id, TestInteractiveRoomKinds.MazeBranch, 4, 1, true,
                AnswerBranchPolicy.CorrectContinuesWrongTerminal);
        }

        private static PlannedInteractiveContent Optional(string id)
        {
            return new PlannedInteractiveContent(id, TestInteractiveRoomKinds.MazeBranch, 4, 1, false,
                AnswerBranchPolicy.CorrectContinuesWrongTerminal);
        }

        private static int CountPassagesBetween(PacingGraph graph, string fromNodeId, string toNodeId)
        {
            string current = fromNodeId;
            int count = 0;
            for (int safety = 0; safety < 32; safety++)
            {
                PacingGraphEdge edge = graph.Edges.SingleOrDefault(candidate =>
                    candidate.FromNodeId == current && candidate.IsMainPath);
                if (edge == null)
                {
                    return count;
                }

                if (edge.ToNodeId == toNodeId)
                {
                    return count;
                }

                PacingGraphNode next = graph.FindNode(edge.ToNodeId);
                if (next.Kind == RoomKind.Passage)
                {
                    count++;
                }

                current = next.NodeId;
            }

            return count;
        }
    }
}
