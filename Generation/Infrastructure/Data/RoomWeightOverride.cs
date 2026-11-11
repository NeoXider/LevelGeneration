using System;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Generation.Infrastructure.Data
{
    [Serializable]
    public struct RoomWeightOverride
    {
        [Tooltip("Room prefab whose selection weight is overridden.")]
        public GameObject roomPrefab;

        [Range(0, 100)] [Tooltip("Room selection weight. 0 excludes the prefab; 100 is high priority.")]
        public int weight;
    }

    [Serializable]
    public struct RoomKindWeightOverride
    {
        [Tooltip("Room kind affected by this weight multiplier.")]
        public RoomKind kind;

        [Range(0, 200)] [Tooltip("Weight multiplier for this room kind. 100 is baseline, 200 doubles the weight.")]
        public int weight;
    }

    [Serializable]
    public struct RoomKindSpawnRule
    {
        [Tooltip("Room kind affected by this spawn rule.")]
        public RoomKind kind;

        [Tooltip("Spawn mode for this room kind.")]
        public RoomTypeSpawnMode mode;

        [Tooltip("Explicitly override chance for this room kind.")]
        public bool overrideChance;

        [Range(0f, 1f)] [Tooltip("Spawn chance when overrideChance is enabled.")]
        public float chance;

        [Min(0)] [Tooltip("Minimum count for CountRange mode.")]
        public int minCount;

        [Min(0)] [Tooltip("Maximum count for CountRange mode.")]
        public int maxCount;
    }

    [Serializable]
    public struct MissionRoomRule
    {
        [Tooltip("Concrete mission room prefab.")]
        public GameObject roomPrefab;

        [Tooltip("Apply a spawn rule specifically for this prefab.")]
        public bool overrideSpawnRule;

        [Tooltip("Spawn mode for this prefab.")]
        public RoomTypeSpawnMode mode;

        [Tooltip("Explicitly override chance for this prefab.")]
        public bool overrideChance;

        [Range(0f, 1f)] [Tooltip("Spawn chance when overrideChance is enabled.")]
        public float chance;

        [Min(0)] [Tooltip("Minimum count for CountRange mode.")]
        public int minCount;

        [Min(0)] [Tooltip("Maximum count for CountRange mode.")]
        public int maxCount;

        [Tooltip("Override this prefab selection weight.")]
        public bool overrideWeight;

        [Range(0, 100)] [Tooltip("Prefab selection weight when overrideWeight is enabled.")]
        public int weight;
    }

    [Serializable]
    public struct HybridSequenceStep
    {
        [Tooltip("Hybrid sequence slot kind.")]
        public HybridSequenceSlotKind kind;

        [Tooltip("Room kind for a RoomKind slot.")]
        public RoomKind roomKind;

        [Tooltip("Room prefab for a RoomPrefab slot.")]
        public GameObject roomPrefab;

        [Min(0)] [Tooltip("Minimum procedural rooms for an Other slot.")]
        public int minOtherRooms;

        [Min(0)] [Tooltip("Maximum procedural rooms for an Other slot.")]
        public int maxOtherRooms;
    }
}
