using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application;

namespace Neoxider.LevelGeneration.Generation.Infrastructure
{
    /// <summary>
    /// <see cref="IDeterministicRng"/> backed by <see cref="System.Random"/>.
    /// Cheap to allocate, deterministic for a given seed, plenty good enough for layout generation.
    /// </summary>
    public sealed class SystemRandomRng : IDeterministicRng
    {
        private readonly Random _random;

        public SystemRandomRng(int seed)
        {
            _random = new Random(seed);
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
            {
                return minInclusive;
            }

            return _random.Next(minInclusive, maxExclusive);
        }

        public float NextFloat() => (float)_random.NextDouble();

        public void Shuffle<T>(IList<T> list)
        {
            if (list == null)
            {
                return;
            }

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    public sealed class SystemRandomRngFactory : IDeterministicRngFactory
    {
        public SystemRandomRngFactory()
        {
        }

        public IDeterministicRng Create(int seed) => new SystemRandomRng(seed);
    }
}
