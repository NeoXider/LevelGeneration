using System.Collections.Generic;

namespace RedoSchool.Features.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Seeded pseudo-random source. Deterministic for a given seed — all generator stages share one instance.
    /// </summary>
    public interface IDeterministicRng
    {
        /// <summary>Inclusive lower, exclusive upper.</summary>
        public int NextInt(int minInclusive, int maxExclusive);

        /// <summary>[0, 1)</summary>
        public float NextFloat();

        /// <summary>Fisher-Yates in-place shuffle.</summary>
        public void Shuffle<T>(IList<T> list);
    }

    /// <summary>Factory that builds a fresh RNG instance for a given seed.</summary>
    public interface IDeterministicRngFactory
    {
        public IDeterministicRng Create(int seed);
    }
}
