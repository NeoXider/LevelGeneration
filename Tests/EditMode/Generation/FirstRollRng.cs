using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Application;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
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
