using System;
using UnityEngine;

namespace Neoxider.LevelGeneration.Generation.Infrastructure.Data
{
    /// <summary>
    /// Serializable integer range (inclusive). Used in <c>TilesetSO</c> defaults.
    /// </summary>
    [Serializable]
    public struct IntRangeSerialized
    {
        [Tooltip("Минимальное значение диапазона.")]
        public int min;

        [Tooltip("Максимальное значение диапазона.")]
        public int max;

        public IntRangeSerialized(int min, int max)
        {
            this.min = min;
            this.max = max;
        }
    }

    /// <summary>
    /// Serializable optional range. <c>isSet=false</c> tells the generator to fall back to tileset default.
    /// </summary>
    [Serializable]
    public struct OptionalIntRangeSerialized
    {
        [Tooltip("Флаг явного задания диапазона.")]
        public bool isSet;

        [Tooltip("Минимальное значение диапазона.")]
        public int min;

        [Tooltip("Максимальное значение диапазона.")]
        public int max;
    }

    /// <summary>Serializable optional float for chance / share overrides.</summary>
    [Serializable]
    public struct OptionalFloatSerialized
    {
        [Tooltip("Флаг явного задания процента.")]
        public bool isSet;

        [Tooltip("Значение процента/коэффициента.")]
        public float value;
    }
}
