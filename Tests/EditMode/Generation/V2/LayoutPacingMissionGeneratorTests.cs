using System.Linq;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Application.V2.Facade;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation.V2
{
    [Category("LevelGeneration")]
    public sealed class LayoutPacingMissionGeneratorTests
    {
        [Test]
        public void SuccessfulResult_ContainsGraphAndDiagnostics()
        {
            LayoutGenerationResult result = Generate(42);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Graph, Is.Not.Null);
            Assert.That(result.Diagnostics, Is.Not.Null);
            Assert.That(result.Diagnostics.Seed, Is.EqualTo(42));
            Assert.That(result.Diagnostics.GraphNodeCount, Is.EqualTo(result.Graph.Nodes.Count));
        }

        [Test]
        public void SameSeedAndConfig_IsDeterministic()
        {
            LayoutGenerationResult first = Generate(633659764);
            LayoutGenerationResult second = Generate(633659764);

            Assert.That(second.Graph.Nodes.Select(node => node.NodeId),
                Is.EqualTo(first.Graph.Nodes.Select(node => node.NodeId)));
            Assert.That(second.Graph.Edges.Select(edge => edge.FromNodeId + ">" + edge.ToNodeId + ":" + edge.Label),
                Is.EqualTo(first.Graph.Edges.Select(edge => edge.FromNodeId + ">" + edge.ToNodeId + ":" + edge.Label)));
        }

        [Test]
        public void TinyTimeout_ReturnsStructuredFailure()
        {
            LayoutGenerationRequest request = new(
                42,
                LayoutPacingDefaults.StableRuntime(),
                new MissionContentPlan(new[] { Mandatory("q1") }, new PlannedInteractiveContent[0]),
                0f);

            LayoutGenerationResult result = new LayoutPacingMissionGenerator().Generate(request);

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(LayoutGenerationFailureReason.Timeout));
            Assert.That(result.Diagnostics, Is.Not.Null);
        }

        [TestCase(11)]
        [TestCase(42)]
        [TestCase(633659764)]
        [TestCase(631117451)]
        [TestCase(968249110)]
        [TestCase(1405879906)]
        public void KnownSeeds_GenerateValidCoreGraph(int seed)
        {
            LayoutGenerationResult result = Generate(seed);

            Assert.That(result.Success, Is.True);
            Assert.That(result.ElapsedMilliseconds, Is.LessThan(5000));
            Assert.That(result.Graph.IsReachable("n_start", "n_exit"), Is.True);
            Assert.That(result.Graph.Nodes.Any(node => node.IsTerminal && !node.IsOnMainPath), Is.True);
        }

        private static LayoutGenerationResult Generate(int seed)
        {
            LayoutGenerationRequest request = new(
                seed,
                LayoutPacingDefaults.StableRuntime(),
                new MissionContentPlan(
                    new[] { Mandatory("q1"), Mandatory("q2"), Mandatory("q3"), Mandatory("q4") },
                    new[] { Optional("optional") }));

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
    }
}
