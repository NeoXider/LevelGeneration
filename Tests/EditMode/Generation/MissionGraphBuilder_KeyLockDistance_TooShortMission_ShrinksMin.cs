using System.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class MissionGraphBuilder_KeyLockDistance_TooShortMission_ShrinksMin
    {
        [Test]
        public void ValidatorReportsWarningWhenShortMissionCannotReachConfiguredMinDistance()
        {
            LevelLayout layout = KeyLockLayoutFactory.Create(2);
            LevelValidator validator = KeyLockLayoutFactory.CreateValidator(3, 6);

            LevelValidationResult result = validator.Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsTrue(result.IsValid);
            Assert.IsTrue(result.Issues.Any(issue => issue.Code == "DependencyDistanceShrunk"));
        }
    }
}
