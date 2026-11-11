using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    internal sealed class SequenceRng : IDeterministicRng
    {
        private readonly int[] _values;
        private int _index;

        public SequenceRng(params int[] values)
        {
            _values = values ?? System.Array.Empty<int>();
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (_values.Length == 0)
            {
                return minInclusive;
            }

            int value = _values[_index < _values.Length ? _index : _values.Length - 1];
            _index++;
            if (value < minInclusive)
            {
                return minInclusive;
            }

            if (value >= maxExclusive)
            {
                return maxExclusive - 1;
            }

            return value;
        }

        public float NextFloat() => 0f;

        public void Shuffle<T>(IList<T> list)
        {
        }
    }

    internal sealed class BoundedSeedRng : IDeterministicRng
    {
        private int _state;

        public BoundedSeedRng(int seed)
        {
            _state = seed;
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            int width = maxExclusive - minInclusive;
            if (width <= 0)
            {
                return minInclusive;
            }

            _state = unchecked(_state * 1103515245 + 12345);
            int positive = _state & 0x7fffffff;
            return minInclusive + positive % width;
        }

        public float NextFloat() => NextInt(0, 10000) / 10000f;

        public void Shuffle<T>(IList<T> list)
        {
        }
    }
}
