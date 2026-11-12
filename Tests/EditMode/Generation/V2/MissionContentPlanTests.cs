using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application.V2.Domain;

namespace Neoxider.LevelGeneration.Tests.Generation.V2
{
    [Category("LevelGeneration")]
    public sealed class MissionContentPlanTests
    {
        [Test]
        public void MandatoryList_PreservesOrder()
        {
            PlannedInteractiveContent first = Mandatory("q1");
            PlannedInteractiveContent second = Mandatory("q2");
            MissionContentPlan plan = new(new[] { first, second }, new PlannedInteractiveContent[0]);

            Assert.That(plan.Mandatory[0].ContentId, Is.EqualTo("q1"));
            Assert.That(plan.Mandatory[1].ContentId, Is.EqualTo("q2"));
        }

        [Test]
        public void Optional_DoesNotMixWithMandatory()
        {
            MissionContentPlan plan = new(new[] { Mandatory("required") }, new[] { Optional("bonus") });

            Assert.That(plan.Mandatory.Count, Is.EqualTo(1));
            Assert.That(plan.Optional.Count, Is.EqualTo(1));
            Assert.That(plan.Mandatory[0].ContentId, Is.EqualTo("required"));
            Assert.That(plan.Optional[0].ContentId, Is.EqualTo("bonus"));
        }

        [Test]
        public void MazeBranch_SingleCorrect_IsBranchInteractive()
        {
            PlannedInteractiveContent content = new(
                "q1",
                TestInteractiveRoomKinds.MazeBranch,
                4,
                1,
                true,
                AnswerBranchPolicy.CorrectContinuesWrongTerminal);

            Assert.That(content.IsSingleCorrectDoorBranch, Is.True);
        }

        [Test]
        public void MazeBranch_MultiCorrect_IsNotBranchInteractive()
        {
            PlannedInteractiveContent content = new(
                "q1",
                TestInteractiveRoomKinds.MazeBranch,
                4,
                2,
                true,
                AnswerBranchPolicy.CorrectContinuesWrongTerminal);

            Assert.That(content.IsSingleCorrectDoorBranch, Is.False);
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
