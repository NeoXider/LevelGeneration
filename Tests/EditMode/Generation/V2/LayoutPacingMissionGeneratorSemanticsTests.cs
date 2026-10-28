using System;
using System.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Facade;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Planning;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation.V2
{
    /// <summary>
    ///     Coverage for audit finding C3: the runtime V2 path
    ///     (<see cref="LayoutPacingMissionGenerator" />, the planners,
    ///     <see cref="QuizBranchGraphExpander" /> and <see cref="PacingGraphSemanticValidator" />).
    ///     Focuses on areas the existing V2 suites under-cover: full-graph determinism,
    ///     mandatory-on-main-path / optional-in-branch semantics asserted through the
    ///     semantic validator, quiz-branch structure, start/exit reachability, and the
    ///     retry/degradation + clean-failure contracts.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LayoutPacingMissionGeneratorSemanticsTests
    {
        // 1. Determinism: same config + same seed => byte-for-byte identical graph
        //    (node ids, ordering, full per-node properties, edges and labels).
        [TestCase(11)]
        [TestCase(42)]
        [TestCase(633659764)]
        public void SameSeedAndConfig_ProducesFullyIdenticalGraph(int seed)
        {
            LayoutGenerationResult first = GenerateRealistic(seed);
            LayoutGenerationResult second = GenerateRealistic(seed);

            Assert.That(first.Success, Is.True);
            Assert.That(second.Success, Is.True);
            Assert.That(Fingerprint(second.Graph), Is.EqualTo(Fingerprint(first.Graph)));
            Assert.That(second.RetryStage, Is.EqualTo(first.RetryStage));
            Assert.That(second.SuccessfulSeed, Is.EqualTo(first.SuccessfulSeed));
        }

        [Test]
        public void DifferentSeeds_CanProduceDifferentGraphs()
        {
            // Sanity guard: the fingerprint is not constant, so the determinism
            // assertion above is actually meaningful.
            string a = Fingerprint(GenerateRealistic(11).Graph);
            string b = Fingerprint(GenerateRealistic(20240131).Graph);

            Assert.That(a, Is.Not.EqualTo(b));
        }

        // 2. Semantics: every mandatory interactive lands on the main path, every
        //    optional interactive lands off it, and the semantic validator accepts
        //    the produced graph.
        [Test]
        public void MandatoryOnMainPath_OptionalOffMainPath_PassesSemanticValidator()
        {
            MissionContentPlan plan = new(
                new[] { Mandatory("m1"), Mandatory("m2"), Mandatory("m3") },
                new[] { Optional("o1") });
            LayoutGenerationRequest request = new(42, LayoutPacingDefaults.StableRuntime(), plan);

            LayoutGenerationResult result = new LayoutPacingMissionGenerator().Generate(request);

            Assert.That(result.Success, Is.True);

            foreach (string mandatoryId in new[] { "m1", "m2", "m3" })
            {
                PacingGraphNode node = result.Graph.Nodes.Single(n => n.ContentId == mandatoryId);
                Assert.That(node.Kind, Is.EqualTo(RoomKind.Interactive));
                Assert.That(node.IsOnMainPath, Is.True, $"mandatory '{mandatoryId}' must be on the main path");
            }

            PacingGraphNode optional = result.Graph.Nodes.Single(n => n.ContentId == "o1");
            Assert.That(optional.IsOnMainPath, Is.False, "optional content must live in a side branch");
            Assert.That(optional.BranchPurpose, Is.EqualTo(BranchPurpose.OptionalInteractive));

            // The validator the generator already ran must independently agree.
            Assert.That(new PacingGraphSemanticValidator().Validate(result.Graph, plan), Is.True);
        }

        [Test]
        public void SemanticValidator_RejectsMandatoryContentOffMainPath()
        {
            // Hand-build a graph that violates the mandatory-on-main-path rule and
            // confirm the validator (a unit under test) catches it.
            PacingGraphBuilder builder = new();
            builder.AddNode(new PacingGraphNode("n_start", RoomKind.Start, true, false, null, 1, "start"));
            builder.AddNode(new PacingGraphNode("n_exit", RoomKind.Exit, true, false, null, 1, "exit"));
            builder.AddEdge("n_start", "n_exit", true, "main");
            // mandatory content sitting OFF the main path (IsOnMainPath: false)
            builder.AddNode(new PacingGraphNode("n_bad", RoomKind.Interactive, false, false, "m1", 1, "x"));
            builder.AddEdge("n_start", "n_bad", false, "branch");
            PacingGraph graph = builder.Build();

            MissionContentPlan plan = new(new[] { Mandatory("m1") }, Array.Empty<PlannedInteractiveContent>());

            Assert.That(new PacingGraphSemanticValidator().Validate(graph, plan), Is.False);
        }

        [Test]
        public void SemanticValidator_RejectsGraphWithoutReachableExit()
        {
            PacingGraphBuilder builder = new();
            builder.AddNode(new PacingGraphNode("n_start", RoomKind.Start, true, false, null, 1, "start"));
            builder.AddNode(new PacingGraphNode("n_exit", RoomKind.Exit, true, false, null, 1, "exit"));
            // No edge from start to exit => exit unreachable.
            PacingGraph graph = builder.Build();

            Assert.That(
                new PacingGraphSemanticValidator().Validate(graph, EmptyPlan()),
                Is.False);
        }

        // 3. Quiz branch expansion: a single-correct maze branch (4 options) produces
        //    one correct tunnel that continues on the main path and three wrong-answer
        //    terminal dead-ends off the main path.
        [Test]
        public void QuizBranchExpansion_ProducesCorrectAndWrongAnswerStructure()
        {
            LayoutGenerationResult result = GenerateMandatoryOnly(11, Mandatory("q1"));
            PacingGraphNode quiz = result.Graph.Nodes.Single(n => n.ContentId == "q1");

            PacingGraphEdge[] correct = result.Graph.Edges
                .Where(e => e.FromNodeId == quiz.NodeId && e.Label == "correct").ToArray();
            PacingGraphEdge[] wrong = result.Graph.Edges
                .Where(e => e.FromNodeId == quiz.NodeId && e.Label == "wrong").ToArray();

            // Exactly one correct continuation, on the main path.
            Assert.That(correct.Length, Is.EqualTo(1));
            Assert.That(correct[0].IsMainPath, Is.True);
            Assert.That(result.Graph.FindNode(correct[0].ToNodeId).IsOnMainPath, Is.True);

            // OptionCount - 1 = 3 wrong dead-ends, each off-path and terminal.
            Assert.That(wrong.Length, Is.EqualTo(3));
            foreach (PacingGraphEdge edge in wrong)
            {
                PacingGraphNode dead = result.Graph.FindNode(edge.ToNodeId);
                Assert.That(edge.IsMainPath, Is.False);
                Assert.That(dead.IsOnMainPath, Is.False);
                Assert.That(dead.IsTerminal, Is.True);
                Assert.That(dead.BranchPurpose, Is.EqualTo(BranchPurpose.WrongAnswerDeadEnd));
            }

            // Door count accounts for entry + correct + each wrong option.
            Assert.That(quiz.RequiredDoorCount, Is.EqualTo(5));
        }

        [Test]
        public void NonMazeBranchContent_IsNotExpandedIntoAnswerBranches()
        {
            // A plain mandatory (not a single-correct maze branch) should NOT create
            // correct/wrong answer edges - the expander returns the node unchanged.
            PlannedInteractiveContent plain = new(
                "plain", TestInteractiveRoomKinds.LeverPanel, 1, 1, true, AnswerBranchPolicy.None);
            LayoutGenerationResult result = GenerateMandatoryOnly(7, plain);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Graph.Edges.Any(e => e.Label == "correct" || e.Label == "wrong"), Is.False);
            PacingGraphNode node = result.Graph.Nodes.Single(n => n.ContentId == "plain");
            Assert.That(node.RequiredDoorCount, Is.GreaterThanOrEqualTo(2));
        }

        // 4. Start and exit always exist and exit is reachable from start.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        public void StartAndExitExist_AndExitReachableFromStart(int mandatoryCount)
        {
            PlannedInteractiveContent[] mandatory = Enumerable.Range(0, mandatoryCount)
                .Select(i => Mandatory("m" + i)).ToArray();
            LayoutGenerationRequest request = new(
                42,
                LayoutPacingDefaults.StableRuntime(),
                new MissionContentPlan(mandatory, Array.Empty<PlannedInteractiveContent>()));

            LayoutGenerationResult result = new LayoutPacingMissionGenerator().Generate(request);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Graph.FindNode("n_start"), Is.Not.Null);
            Assert.That(result.Graph.FindNode("n_exit"), Is.Not.Null);
            Assert.That(result.Graph.FindNode("n_start").Kind, Is.EqualTo(RoomKind.Start));
            Assert.That(result.Graph.FindNode("n_exit").Kind, Is.EqualTo(RoomKind.Exit));
            Assert.That(result.Graph.IsReachable("n_start", "n_exit"), Is.True);
        }

        // 5. Retry / degradation and clean-failure contracts.

        [Test]
        public void FailFastPolicy_RunsOnlyTheFullStage()
        {
            // With FailFast the generator must not climb the degradation ladder:
            // any success can only come from the Full stage.
            LayoutPacingConfig config = new(
                new PacingIntRange(1, 1),
                new PacingIntRange(0, 0),
                new WeightedBranchLengthConfig(new PacingIntRange(0, 0), 0f),
                0,
                5f,
                LayoutRetryPolicy.FailFast,
                0);
            LayoutGenerationRequest request = new(
                42, config,
                new MissionContentPlan(new[] { Mandatory("q1") }, Array.Empty<PlannedInteractiveContent>()));

            LayoutGenerationResult result = new LayoutPacingMissionGenerator().Generate(request);

            Assert.That(result.Success, Is.True);
            Assert.That(result.RetryStage, Is.EqualTo(LayoutRetryStage.Full));
            Assert.That(result.SuccessfulSeed, Is.EqualTo(42));
            Assert.That(result.Diagnostics.Attempt, Is.EqualTo(0));
        }

        [Test]
        public void ZeroTimeBudget_FailsCleanlyWithTimeoutReason()
        {
            // Degradation/abort path must surface a structured failure rather than throw.
            LayoutGenerationRequest request = new(
                42,
                LayoutPacingDefaults.StableRuntime(),
                new MissionContentPlan(new[] { Mandatory("q1") }, Array.Empty<PlannedInteractiveContent>()),
                0f);

            LayoutGenerationResult result = null;
            Assert.DoesNotThrow(() => result = new LayoutPacingMissionGenerator().Generate(request));

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(LayoutGenerationFailureReason.Timeout));
            Assert.That(result.Graph, Is.Null);
            Assert.That(result.Diagnostics, Is.Not.Null);
        }

        [Test]
        public void TrimOptionalPolicy_StillProducesValidGraphUnderTightFillerBudget()
        {
            // Stress the degradation ladder config-wise (TrimOptional walks
            // Full -> TrimBranches -> DisableOptionalInteractive -> MandatoryOnly).
            // Even a demanding request must end in a semantically valid graph and
            // never throw.
            LayoutPacingConfig config = new(
                new PacingIntRange(2, 4),
                new PacingIntRange(3, 6),
                new WeightedBranchLengthConfig(new PacingIntRange(2, 5), 1f),
                3,
                5f,
                LayoutRetryPolicy.TrimOptional,
                1);
            MissionContentPlan plan = new(
                new[] { Mandatory("m1"), Mandatory("m2") },
                new[] { Optional("o1"), Optional("o2"), Optional("o3") });
            LayoutGenerationRequest request = new(987654321, config, plan);

            LayoutGenerationResult result = null;
            Assert.DoesNotThrow(() => result = new LayoutPacingMissionGenerator().Generate(request));

            Assert.That(result.Success, Is.True);
            Assert.That(new PacingGraphSemanticValidator().Validate(result.Graph, plan), Is.True);
            Assert.That(result.Graph.IsReachable("n_start", "n_exit"), Is.True);
        }

        [Test]
        public void NullRequest_ThrowsArgumentNullException() => Assert.Throws<ArgumentNullException>(() => new LayoutPacingMissionGenerator().Generate(null));

        // --- helpers ---

        private static LayoutGenerationResult GenerateRealistic(int seed)
        {
            return new LayoutPacingMissionGenerator().Generate(new LayoutGenerationRequest(
                seed,
                LayoutPacingDefaults.StableRuntime(),
                new MissionContentPlan(
                    new[] { Mandatory("q1"), Mandatory("q2"), Mandatory("q3"), Mandatory("q4") },
                    new[] { Optional("o1") })));
        }

        private static LayoutGenerationResult GenerateMandatoryOnly(int seed,
            params PlannedInteractiveContent[] mandatory)
        {
            LayoutPacingConfig config = new(
                new PacingIntRange(0, 0),
                new PacingIntRange(0, 0),
                new WeightedBranchLengthConfig(new PacingIntRange(0, 0), 0f),
                0,
                5f,
                LayoutRetryPolicy.FailFast,
                0);
            return new LayoutPacingMissionGenerator().Generate(new LayoutGenerationRequest(
                seed, config, new MissionContentPlan(mandatory, Array.Empty<PlannedInteractiveContent>())));
        }

        private static MissionContentPlan EmptyPlan()
        {
            return new MissionContentPlan(
                Array.Empty<PlannedInteractiveContent>(),
                Array.Empty<PlannedInteractiveContent>());
        }

        private static string Fingerprint(PacingGraph graph)
        {
            string nodes = string.Join(
                "|",
                graph.Nodes.Select(n =>
                    $"{n.NodeId};{n.Kind};main={n.IsOnMainPath};term={n.IsTerminal};" +
                    $"content={n.ContentId};doors={n.RequiredDoorCount};src={n.Source};purpose={n.BranchPurpose}"));
            string edges = string.Join(
                "|",
                graph.Edges.Select(e => $"{e.FromNodeId}>{e.ToNodeId};main={e.IsMainPath};label={e.Label}"));
            return nodes + "##" + edges;
        }

        private static PlannedInteractiveContent Mandatory(string id)
        {
            return new PlannedInteractiveContent(
                id, TestInteractiveRoomKinds.MazeBranch, 4, 1, true, AnswerBranchPolicy.CorrectContinuesWrongTerminal);
        }

        private static PlannedInteractiveContent Optional(string id)
        {
            return new PlannedInteractiveContent(
                id, TestInteractiveRoomKinds.MazeBranch, 4, 1, false, AnswerBranchPolicy.CorrectContinuesWrongTerminal);
        }
    }
}
