using System.Linq;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application.DependencyHints;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Tests.Generation
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
