using System;
using Neoxider.LevelGeneration.Generation.Application.V2.Domain;

namespace Neoxider.LevelGeneration.Generation.Application.V2.Policies
{
    public sealed class LayoutRetryPlanner
    {
        public LayoutPacingConfig Apply(LayoutPacingConfig config, LayoutRetryStage stage)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            switch (stage)
            {
                case LayoutRetryStage.TrimBranches:
                    return new LayoutPacingConfig(
                        config.MainPathGapRooms,
                        new PacingIntRange(config.SideBranchCount.Min == 0 ? 0 : 1,
                            Math.Max(0, config.SideBranchCount.Max / 2)),
                        new WeightedBranchLengthConfig(
                            new PacingIntRange(config.SideBranchLengthRooms.Range.Min,
                                Math.Max(0, config.SideBranchLengthRooms.Range.Max - 1)),
                            config.SideBranchLengthRooms.Bias),
                        config.MaxOptionalInteractiveBranches,
                        config.GenerationTimeBudgetSeconds,
                        config.RetryPolicy,
                        config.OptionalFillerCap);

                case LayoutRetryStage.DisableOptionalInteractive:
                    return new LayoutPacingConfig(
                        config.MainPathGapRooms,
                        new PacingIntRange(0, Math.Min(1, config.SideBranchCount.Max)),
                        config.SideBranchLengthRooms,
                        0,
                        config.GenerationTimeBudgetSeconds,
                        config.RetryPolicy,
                        config.OptionalFillerCap);

                case LayoutRetryStage.MandatoryOnly:
                    return new LayoutPacingConfig(
                        config.MainPathGapRooms,
                        new PacingIntRange(0, 0),
                        new WeightedBranchLengthConfig(new PacingIntRange(0, 0), config.SideBranchLengthRooms.Bias),
                        0,
                        config.GenerationTimeBudgetSeconds,
                        config.RetryPolicy,
                        0);

                default:
                    return config;
            }
        }
    }
}
