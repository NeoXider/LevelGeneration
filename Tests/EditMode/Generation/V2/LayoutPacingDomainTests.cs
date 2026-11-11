using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation.V2
{
    [Category("LevelGeneration")]
    public sealed class LayoutPacingDomainTests
    {
        [Test]
        public void Defaults_AreValid()
        {
            LayoutPacingConfig config = LayoutPacingDefaults.StableRuntime();

            Assert.That(config.MainPathGapRooms.Min, Is.EqualTo(1));
            Assert.That(config.MainPathGapRooms.Max, Is.EqualTo(3));
            Assert.That(config.SideBranchCount.Min, Is.EqualTo(2));
            Assert.That(config.SideBranchCount.Max, Is.EqualTo(4));
            Assert.That(config.SideBranchLengthRooms.Range.Min, Is.EqualTo(0));
            Assert.That(config.SideBranchLengthRooms.Range.Max, Is.EqualTo(3));
            Assert.That(config.SideBranchLengthRooms.Bias, Is.EqualTo(2f));
            Assert.That(config.MaxOptionalInteractiveBranches, Is.EqualTo(1));
            Assert.That(config.GenerationTimeBudgetSeconds, Is.EqualTo(5f));
            Assert.That(config.RetryPolicy, Is.EqualTo(LayoutRetryPolicy.TrimOptional));
            Assert.That(config.OptionalFillerCap, Is.EqualTo(4));
        }

        [Test]
        public void Range_ClampsInvalidValues()
        {
            PacingIntRange range = new(-5, -1);

            Assert.That(range.Min, Is.EqualTo(0));
            Assert.That(range.Max, Is.EqualTo(0));

            range = new PacingIntRange(3, 1);

            Assert.That(range.Min, Is.EqualTo(3));
            Assert.That(range.Max, Is.EqualTo(3));
        }

        [Test]
        public void BiasedBranchLengthSampler_PrefersShortBranches()
        {
            WeightedBranchLengthConfig sampler = new(new PacingIntRange(0, 3), 2f);
            DeterministicRandom random = new(42);
            int[] counts = new int[4];

            for (int i = 0; i < 20000; i++)
            {
                counts[sampler.Sample(random)]++;
            }

            Assert.That(counts[0], Is.GreaterThan(counts[1]));
            Assert.That(counts[1], Is.GreaterThan(counts[2]));
            Assert.That(counts[2], Is.GreaterThan(counts[3]));
        }

        [Test]
        public void ZeroBias_UsesUniformSampler()
        {
            WeightedBranchLengthConfig sampler = new(new PacingIntRange(0, 3), 0f);
            CyclingRandom random = new();

            Assert.That(sampler.Sample(random), Is.EqualTo(0));
            Assert.That(sampler.Sample(random), Is.EqualTo(1));
            Assert.That(sampler.Sample(random), Is.EqualTo(2));
            Assert.That(sampler.Sample(random), Is.EqualTo(3));
        }

        private sealed class CyclingRandom : IDeterministicRandom
        {
            private int value;

            public int Next(int minInclusive, int maxExclusive)
            {
                int result = minInclusive + value % (maxExclusive - minInclusive);
                value++;
                return result;
            }

            public double NextDouble() => 0d;
        }
    }
}
