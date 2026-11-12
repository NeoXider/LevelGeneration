using System.Diagnostics;
using Neoxider.LevelGeneration.Generation.Application.V2.Domain;

namespace Neoxider.LevelGeneration.Generation.Application.V2.Policies
{
    public sealed class GenerationTimeBudgetPolicy
    {
        private readonly Stopwatch stopwatch;
        private readonly long budgetMilliseconds;

        public GenerationTimeBudgetPolicy(LayoutPacingConfig config, float? overrideSeconds = null)
        {
            float seconds = overrideSeconds.HasValue
                ? System.Math.Max(0f, overrideSeconds.Value)
                : System.Math.Max(0.01f, config.GenerationTimeBudgetSeconds);
            budgetMilliseconds = (long)System.Math.Ceiling(seconds * 1000f);
            stopwatch = Stopwatch.StartNew();
        }

        public bool IsExpired => stopwatch.ElapsedMilliseconds >= budgetMilliseconds;

        public long ElapsedMilliseconds => stopwatch.ElapsedMilliseconds;
    }
}
