using System;
using System.Collections.Generic;

namespace Neoxider.LevelGeneration.Generation.Application.V2.Domain
{
    public readonly struct PacingIntRange
    {
        public readonly int Min;
        public readonly int Max;

        public PacingIntRange(int min, int max)
        {
            Min = Math.Max(0, min);
            Max = Math.Max(Min, max);
        }

        public int Sample(IDeterministicRandom random)
        {
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            if (Min == Max)
            {
                return Min;
            }

            return random.Next(Min, Max + 1);
        }

        public PacingIntRange WithMax(int max) => new(Min, max);
    }

    public readonly struct WeightedBranchLengthConfig
    {
        public readonly PacingIntRange Range;
        public readonly float Bias;

        public WeightedBranchLengthConfig(PacingIntRange range, float bias)
        {
            Range = range;
            Bias = Math.Max(0f, bias);
        }

        public int Sample(IDeterministicRandom random)
        {
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            if (Bias <= 0.0001f || Range.Min == Range.Max)
            {
                return Range.Sample(random);
            }

            double total = 0d;
            for (int value = Range.Min; value <= Range.Max; value++)
            {
                total += Weight(value);
            }

            double roll = random.NextDouble() * total;
            for (int value = Range.Min; value <= Range.Max; value++)
            {
                roll -= Weight(value);
                if (roll <= 0d)
                {
                    return value;
                }
            }

            return Range.Max;
        }

        private double Weight(int value)
        {
            int distanceFromMax = Range.Max - value + 1;
            return Math.Pow(distanceFromMax, Bias);
        }
    }

    public sealed class LayoutPacingConfig
    {
        public LayoutPacingConfig(
            PacingIntRange mainPathGapRooms,
            PacingIntRange sideBranchCount,
            WeightedBranchLengthConfig sideBranchLengthRooms,
            int maxOptionalInteractiveBranches,
            float generationTimeBudgetSeconds,
            LayoutRetryPolicy retryPolicy,
            int optionalFillerCap)
        {
            MainPathGapRooms = mainPathGapRooms;
            SideBranchCount = sideBranchCount;
            SideBranchLengthRooms = sideBranchLengthRooms;
            MaxOptionalInteractiveBranches = Math.Max(0, maxOptionalInteractiveBranches);
            GenerationTimeBudgetSeconds = Math.Max(0.01f, generationTimeBudgetSeconds);
            RetryPolicy = retryPolicy;
            OptionalFillerCap = Math.Max(0, optionalFillerCap);
        }

        public PacingIntRange MainPathGapRooms { get; }
        public PacingIntRange SideBranchCount { get; }
        public WeightedBranchLengthConfig SideBranchLengthRooms { get; }
        public int MaxOptionalInteractiveBranches { get; }
        public float GenerationTimeBudgetSeconds { get; }
        public LayoutRetryPolicy RetryPolicy { get; }
        public int OptionalFillerCap { get; }

        public int ComplexityScore =>
            MainPathGapRooms.Max + SideBranchCount.Max + SideBranchLengthRooms.Range.Max +
            MaxOptionalInteractiveBranches + OptionalFillerCap;
    }

    public interface ILayoutPacingConfigProvider
    {
        public bool UseLayoutPacingGeneratorV2 { get; }
        public LayoutPacingConfig ResolveLayoutPacingConfig();
    }

    public static class LayoutPacingDefaults
    {
        public static LayoutPacingConfig StableRuntime()
        {
            return new LayoutPacingConfig(
                new PacingIntRange(1, 3),
                new PacingIntRange(2, 4),
                new WeightedBranchLengthConfig(new PacingIntRange(0, 3), 2f),
                1,
                5f,
                LayoutRetryPolicy.TrimOptional,
                4);
        }
    }

    public enum LayoutRetryPolicy
    {
        None = 0,
        TrimOptional = 1,
        FailFast = 2
    }

    public enum LayoutRetryStage
    {
        Full = 0,
        TrimBranches = 1,
        DisableOptionalInteractive = 2,
        MandatoryOnly = 3
    }

    public enum LayoutGenerationFailureReason
    {
        None = 0,
        Timeout = 1,
        InvalidRequest = 2,
        NoValidLayout = 3
    }

    public enum BranchPurpose
    {
        OptionalInteractive = 0,
        Reward = 1,
        Secret = 2,
        Scenic = 3,
        WrongAnswerDeadEnd = 4,
        ShortcutLoop = 5,
        Challenge = 6,
        Filler = 7
    }

    public enum AnswerBranchPolicy
    {
        None = 0,
        CorrectContinuesWrongTerminal = 1
    }

    public sealed class PlannedInteractiveContent
    {
        /// <param name="interactiveKind">Canonical content kind; informational for the planner.</param>
        /// <param name="answerBranchPolicy">
        /// <see cref="AnswerBranchPolicy.CorrectContinuesWrongTerminal"/> for content whose answer options are
        /// doors (the room type rule decides, see <c>InteractiveRoomAnswerLayout.DoorPerOption</c>).
        /// </param>
        public PlannedInteractiveContent(
            string contentId,
            string interactiveKind,
            int optionCount,
            int correctAnswerCount,
            bool isMandatory,
            AnswerBranchPolicy answerBranchPolicy)
        {
            ContentId = string.IsNullOrWhiteSpace(contentId) ? Guid.NewGuid().ToString("N") : contentId;
            InteractiveKind = interactiveKind ?? string.Empty;
            OptionCount = Math.Max(0, optionCount);
            CorrectAnswerCount = Math.Max(0, correctAnswerCount);
            IsMandatory = isMandatory;
            AnswerBranchPolicy = answerBranchPolicy;
        }

        public string ContentId { get; }
        public string InteractiveKind { get; }
        public int OptionCount { get; }
        public int CorrectAnswerCount { get; }
        public bool IsMandatory { get; }
        public AnswerBranchPolicy AnswerBranchPolicy { get; }

        /// <summary>
        /// Door-per-option content with one correct answer: the correct door continues the path, every wrong
        /// door gets a dead-end branch.
        /// </summary>
        public bool IsSingleCorrectDoorBranch =>
            AnswerBranchPolicy == AnswerBranchPolicy.CorrectContinuesWrongTerminal && CorrectAnswerCount == 1 &&
            OptionCount > 1;
    }

    public sealed class MissionContentPlan
    {
        public MissionContentPlan(
            IReadOnlyList<PlannedInteractiveContent> mandatory,
            IReadOnlyList<PlannedInteractiveContent> optional)
        {
            Mandatory = mandatory ?? Array.Empty<PlannedInteractiveContent>();
            Optional = optional ?? Array.Empty<PlannedInteractiveContent>();
        }

        public IReadOnlyList<PlannedInteractiveContent> Mandatory { get; }
        public IReadOnlyList<PlannedInteractiveContent> Optional { get; }
    }

    public interface IDeterministicRandom
    {
        public int Next(int minInclusive, int maxExclusive);
        public double NextDouble();
    }

    public sealed class DeterministicRandom : IDeterministicRandom
    {
        private readonly Random random;

        public DeterministicRandom(int seed)
        {
            random = new Random(seed);
        }

        public int Next(int minInclusive, int maxExclusive) => random.Next(minInclusive, maxExclusive);

        public double NextDouble() => random.NextDouble();
    }
}
