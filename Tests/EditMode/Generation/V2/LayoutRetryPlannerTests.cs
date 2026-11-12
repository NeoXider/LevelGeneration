using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application.V2.Domain;
using Neoxider.LevelGeneration.Generation.Application.V2.Policies;

namespace Neoxider.LevelGeneration.Tests.Generation.V2
{
    [Category("LevelGeneration")]
    public sealed class LayoutRetryPlannerTests
    {
        [Test]
        public void RetryStages_NeverIncreaseComplexity()
        {
            LayoutRetryPlanner planner = new();
            LayoutPacingConfig full = LayoutPacingDefaults.StableRuntime();
            LayoutPacingConfig trim = planner.Apply(full, LayoutRetryStage.TrimBranches);
            LayoutPacingConfig disableOptional = planner.Apply(full, LayoutRetryStage.DisableOptionalInteractive);
            LayoutPacingConfig mandatoryOnly = planner.Apply(full, LayoutRetryStage.MandatoryOnly);

            Assert.That(trim.ComplexityScore, Is.LessThanOrEqualTo(full.ComplexityScore));
            Assert.That(disableOptional.ComplexityScore, Is.LessThanOrEqualTo(trim.ComplexityScore));
            Assert.That(mandatoryOnly.ComplexityScore, Is.LessThanOrEqualTo(disableOptional.ComplexityScore));
        }

        [Test]
        public void MandatoryOnly_DisablesOptionalBranches()
        {
            LayoutRetryPlanner planner = new();
            LayoutPacingConfig mandatoryOnly =
                planner.Apply(LayoutPacingDefaults.StableRuntime(), LayoutRetryStage.MandatoryOnly);

            Assert.That(mandatoryOnly.SideBranchCount.Max, Is.EqualTo(0));
            Assert.That(mandatoryOnly.MaxOptionalInteractiveBranches, Is.EqualTo(0));
            Assert.That(mandatoryOnly.OptionalFillerCap, Is.EqualTo(0));
        }
    }
}
