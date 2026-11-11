using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Application-side view of a mission config. Lets the pipeline read config without UnityEngine.
    /// </summary>
    public interface IMissionConfigView
    {
        public string MissionId { get; }
        public string TilesetId { get; }
        public MissionArchetypeKind ArchetypeKind { get; }
        public int Difficulty { get; }

        public OptionalIntRange TotalRoomsRange { get; }
        public OptionalIntRange MainPathRoomsRange { get; }
        public OptionalIntRange BranchingRange { get; }
        public OptionalIntRange SideBranchesRange { get; }
        public OptionalIntRange DeadEndsRange { get; }
        public bool AllowLoops { get; }

        public OptionalFloat BonusRoomChance { get; }
        public OptionalFloat SecretRoomChance { get; }
        public OptionalFloat LogicRoomShare { get; }

        public ExtractionMode ExtractionMode { get; }
        public int ExtractionRoomsCount { get; }
        public ExtractionAvailability ExtractionAvailability { get; }
        public float ExtractionUnlockDelaySeconds { get; }

        public IReadOnlyList<RoomKind> RequiredRoomKinds => System.Array.Empty<RoomKind>();
        public IReadOnlyList<RoomKind> OptionalRoomKinds => System.Array.Empty<RoomKind>();
        public IReadOnlyList<IMissionRoomRule> MissionRoomRules { get; }
        public IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => System.Array.Empty<IRoomKindSpawnRule>();

        public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides { get; }
        public IReadOnlyList<IRoomKindWeightOverride> RoomKindWeightOverrides => System.Array.Empty<IRoomKindWeightOverride>();

        public LevelGenerationMode GenerationMode { get; }
        public IReadOnlyList<IHybridSequenceStep> HybridSequence { get; }
    }

    public interface IQuizBranchMissionConfigView
    {
        public int QuizBranchAnswerCount { get; }
    }

    public interface IInteractiveMissionContentProvider
    {
        public IReadOnlyList<IInteractiveMissionContentSpec> InteractiveContent { get; }
    }

    public interface IPlannedInteractiveMissionContentProvider
    {
        public IReadOnlyList<IInteractiveMissionContentSpec> PlannedInteractiveContent { get; }
    }

    public interface IInteractiveMissionContentSpec
    {
        public string ContentId { get; }
        public string InteractiveKind { get; }
        public int OptionCount { get; }
        public int CorrectAnswerCount { get; }
        public bool IsMandatory { get; }
    }

    /// <summary>
    /// Interactive part of a room template. <see cref="InteractiveKind"/> is the kind the room DECLARES
    /// (authored on the prefab); which room type it actually hosts is resolved through
    /// <see cref="IInteractiveRoomKindRules"/>, never by comparing against a concrete kind here.
    /// </summary>
    public interface IInteractiveRoomTemplateDescriptor
    {
        /// <summary>Declared interactive kind (empty when the room does not declare one).</summary>
        public string InteractiveKind { get; }

        /// <summary>
        /// Type and asset name of the room's module, for rooms that do not declare a kind (legacy inference);
        /// <see langword="null"/> means the room module asset id is used.
        /// </summary>
        public string InteractiveModuleHint => null;

        public int InteractiveOptionCapacity { get; }
    }

    public readonly struct OptionalIntRange
    {
        public readonly bool IsSet;
        public readonly int Min;
        public readonly int Max;

        public OptionalIntRange(int min, int max)
        {
            IsSet = true;
            Min = min;
            Max = max;
        }
    }

    public readonly struct OptionalFloat
    {
        public readonly bool IsSet;
        public readonly float Value;

        public OptionalFloat(float value)
        {
            IsSet = true;
            Value = value;
        }
    }

    public interface IRoomWeightOverride
    {
        public string RoomId { get; }
        public int Weight { get; }
    }

    public interface IRoomKindWeightOverride
    {
        public RoomKind Kind { get; }
        public int Weight { get; }
    }

    public interface IRoomKindSpawnRule
    {
        public RoomKind Kind { get; }
        public RoomTypeSpawnMode Mode { get; }
        public bool OverrideChance { get; }
        public float Chance { get; }
        public OptionalIntRange CountRange { get; }
    }

    public interface IMissionRoomRule
    {
        public string RoomId { get; }
        public RoomKind Kind { get; }
        public bool HasSpawnRule { get; }
        public RoomTypeSpawnMode Mode { get; }
        public bool OverrideChance { get; }
        public float Chance { get; }
        public OptionalIntRange CountRange { get; }
        public bool OverrideWeight { get; }
        public int Weight { get; }
    }

    public interface IHybridSequenceStep
    {
        public HybridSequenceSlotKind Kind { get; }
        public RoomKind RoomKind { get; }
        public string RoomId { get; }
        public OptionalIntRange OtherCountRange { get; }
    }
}
