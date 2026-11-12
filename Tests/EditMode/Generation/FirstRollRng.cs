using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    internal sealed class FirstRollRng : IDeterministicRng
    {
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive;

        public float NextFloat() => 0f;

        public void Shuffle<T>(IList<T> list)
        {
        }
    }
}
