using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Plain C# snapshots used by <see cref="MissionGenerator.BuildLayoutAsync"/> so worker-thread generation
    /// does not read UnityEngine objects after scheduling.
    /// </summary>
    internal sealed class MissionConfigViewSnapshot : IMissionConfigView, IQuizBranchMissionConfigView,
        IInteractiveMissionContentProvider, IPlannedInteractiveMissionContentProvider, IForcedMainPathProvider
    {
        private readonly IReadOnlyList<RoomKind> _requiredRoomKinds;
        private readonly IReadOnlyList<RoomKind> _optionalRoomKinds;
        private readonly IReadOnlyList<IMissionRoomRule> _missionRoomRules;
        private readonly IReadOnlyList<IRoomKindSpawnRule> _roomKindSpawnRules;
        private readonly IReadOnlyList<IRoomWeightOverride> _roomWeightOverrides;
        private readonly IReadOnlyList<IRoomKindWeightOverride> _roomKindWeightOverrides;
        private readonly IReadOnlyList<IHybridSequenceStep> _hybridSequence;
        private readonly IReadOnlyList<IInteractiveMissionContentSpec> _interactiveContent;
        private readonly IReadOnlyList<IInteractiveMissionContentSpec> _plannedInteractiveContent;

        private MissionConfigViewSnapshot(IMissionConfigView source)
        {
            if (source is IForcedMainPathProvider forced && forced.ForcedMainPath != null)
            {
                List<IForcedMainPathRoom> rooms = new(forced.ForcedMainPath.Count);
                foreach (IForcedMainPathRoom room in forced.ForcedMainPath)
                {
                    rooms.Add(room == null ? null : new ForcedMainPathRoom(
                        room.Kind, room.ContentId, room.PreferredRoomTemplateId));
                }

                ForcedMainPath = rooms;
            }

            MissionId = source.MissionId;
            TilesetId = source.TilesetId;
            ArchetypeKind = source.ArchetypeKind;
            Difficulty = source.Difficulty;
            TotalRoomsRange = source.TotalRoomsRange;
            MainPathRoomsRange = source.MainPathRoomsRange;
            BranchingRange = source.BranchingRange;
            SideBranchesRange = source.SideBranchesRange;
            DeadEndsRange = source.DeadEndsRange;
            AllowLoops = source.AllowLoops;
            BonusRoomChance = source.BonusRoomChance;
            SecretRoomChance = source.SecretRoomChance;
            LogicRoomShare = source.LogicRoomShare;
            ExtractionMode = source.ExtractionMode;
            ExtractionRoomsCount = source.ExtractionRoomsCount;
            ExtractionAvailability = source.ExtractionAvailability;
            ExtractionUnlockDelaySeconds = source.ExtractionUnlockDelaySeconds;
            GenerationMode = source.GenerationMode;
            QuizBranchAnswerCount = source is IQuizBranchMissionConfigView quiz
                ? quiz.QuizBranchAnswerCount
                : 0;

            _requiredRoomKinds = CopyEnumList(source.RequiredRoomKinds);
            _optionalRoomKinds = CopyEnumList(source.OptionalRoomKinds);
            _missionRoomRules = CopyMissionRoomRules(source.MissionRoomRules);
            _roomKindSpawnRules = CopyRoomKindSpawnRules(source.RoomKindSpawnRules);
            _roomWeightOverrides = CopyRoomWeightOverrides(source.RoomWeightOverrides);
            _roomKindWeightOverrides = CopyRoomKindWeightOverrides(source.RoomKindWeightOverrides);
            _hybridSequence = CopyHybridSequence(source.HybridSequence);
            _interactiveContent = source is IInteractiveMissionContentProvider interactive
                ? CopyInteractiveContent(interactive.InteractiveContent)
                : Array.Empty<IInteractiveMissionContentSpec>();
            _plannedInteractiveContent = source is IPlannedInteractiveMissionContentProvider planned
                ? CopyInteractiveContent(planned.PlannedInteractiveContent)
                : _interactiveContent;
        }

        public static MissionConfigViewSnapshot Capture(IMissionConfigView source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new MissionConfigViewSnapshot(source);
        }

        public IReadOnlyList<IForcedMainPathRoom> ForcedMainPath { get; }

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
        public IReadOnlyList<RoomKind> RequiredRoomKinds => _requiredRoomKinds;
        public IReadOnlyList<RoomKind> OptionalRoomKinds => _optionalRoomKinds;
        public IReadOnlyList<IMissionRoomRule> MissionRoomRules => _missionRoomRules;
        public IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => _roomKindSpawnRules;
        public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => _roomWeightOverrides;
        public IReadOnlyList<IRoomKindWeightOverride> RoomKindWeightOverrides => _roomKindWeightOverrides;
        public LevelGenerationMode GenerationMode { get; }
        public IReadOnlyList<IHybridSequenceStep> HybridSequence => _hybridSequence;
        public int QuizBranchAnswerCount { get; }
        public IReadOnlyList<IInteractiveMissionContentSpec> InteractiveContent => _interactiveContent;
        public IReadOnlyList<IInteractiveMissionContentSpec> PlannedInteractiveContent => _plannedInteractiveContent;

        private static IReadOnlyList<T> CopyEnumList<T>(IReadOnlyList<T> source)
        {
            if (source == null)
            {
                return Array.Empty<T>();
            }

            return new List<T>(source);
        }

        private static IReadOnlyList<IMissionRoomRule> CopyMissionRoomRules(IReadOnlyList<IMissionRoomRule> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<IMissionRoomRule>();
            }

            List<IMissionRoomRule> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(new MissionRoomRuleSnapshot(source[i]));
                }
            }

            return result;
        }

        private static IReadOnlyList<IRoomKindSpawnRule> CopyRoomKindSpawnRules(
            IReadOnlyList<IRoomKindSpawnRule> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<IRoomKindSpawnRule>();
            }

            List<IRoomKindSpawnRule> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(new RoomKindSpawnRuleSnapshot(source[i]));
                }
            }

            return result;
        }

        private static IReadOnlyList<IRoomWeightOverride> CopyRoomWeightOverrides(
            IReadOnlyList<IRoomWeightOverride> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<IRoomWeightOverride>();
            }

            List<IRoomWeightOverride> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(new RoomWeightOverrideSnapshot(source[i]));
                }
            }

            return result;
        }

        private static IReadOnlyList<IRoomKindWeightOverride> CopyRoomKindWeightOverrides(
            IReadOnlyList<IRoomKindWeightOverride> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<IRoomKindWeightOverride>();
            }

            List<IRoomKindWeightOverride> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(new RoomKindWeightOverrideSnapshot(source[i]));
                }
            }

            return result;
        }

        private static IReadOnlyList<IHybridSequenceStep> CopyHybridSequence(IReadOnlyList<IHybridSequenceStep> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<IHybridSequenceStep>();
            }

            List<IHybridSequenceStep> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(new HybridSequenceStepSnapshot(source[i]));
                }
            }

            return result;
        }

        private static IReadOnlyList<IInteractiveMissionContentSpec> CopyInteractiveContent(
            IReadOnlyList<IInteractiveMissionContentSpec> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<IInteractiveMissionContentSpec>();
            }

            List<IInteractiveMissionContentSpec> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(new InteractiveContentSpecSnapshot(source[i]));
                }
            }

            return result;
        }

        private sealed class MissionRoomRuleSnapshot : IMissionRoomRule
        {
            public MissionRoomRuleSnapshot(IMissionRoomRule source)
            {
                RoomId = source.RoomId;
                Kind = source.Kind;
                HasSpawnRule = source.HasSpawnRule;
                Mode = source.Mode;
                OverrideChance = source.OverrideChance;
                Chance = source.Chance;
                CountRange = source.CountRange;
                OverrideWeight = source.OverrideWeight;
                Weight = source.Weight;
            }

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

        private sealed class RoomKindSpawnRuleSnapshot : IRoomKindSpawnRule
        {
            public RoomKindSpawnRuleSnapshot(IRoomKindSpawnRule source)
            {
                Kind = source.Kind;
                Mode = source.Mode;
                OverrideChance = source.OverrideChance;
                Chance = source.Chance;
                CountRange = source.CountRange;
            }

            public RoomKind Kind { get; }
            public RoomTypeSpawnMode Mode { get; }
            public bool OverrideChance { get; }
            public float Chance { get; }
            public OptionalIntRange CountRange { get; }
        }

        private sealed class RoomWeightOverrideSnapshot : IRoomWeightOverride
        {
            public RoomWeightOverrideSnapshot(IRoomWeightOverride source)
            {
                RoomId = source.RoomId;
                Weight = source.Weight;
            }

            public string RoomId { get; }
            public int Weight { get; }
        }

        private sealed class RoomKindWeightOverrideSnapshot : IRoomKindWeightOverride
        {
            public RoomKindWeightOverrideSnapshot(IRoomKindWeightOverride source)
            {
                Kind = source.Kind;
                Weight = source.Weight;
            }

            public RoomKind Kind { get; }
            public int Weight { get; }
        }

        private sealed class HybridSequenceStepSnapshot : IHybridSequenceStep
        {
            public HybridSequenceStepSnapshot(IHybridSequenceStep source)
            {
                Kind = source.Kind;
                RoomKind = source.RoomKind;
                RoomId = source.RoomId;
                OtherCountRange = source.OtherCountRange;
            }

            public HybridSequenceSlotKind Kind { get; }
            public RoomKind RoomKind { get; }
            public string RoomId { get; }
            public OptionalIntRange OtherCountRange { get; }
        }

        private sealed class InteractiveContentSpecSnapshot : IInteractiveMissionContentSpec
        {
            public InteractiveContentSpecSnapshot(IInteractiveMissionContentSpec source)
            {
                ContentId = source.ContentId;
                InteractiveKind = source.InteractiveKind;
                OptionCount = source.OptionCount;
                CorrectAnswerCount = source.CorrectAnswerCount;
                IsMandatory = source.IsMandatory;
            }

            public string ContentId { get; }
            public string InteractiveKind { get; }
            public int OptionCount { get; }
            public int CorrectAnswerCount { get; }
            public bool IsMandatory { get; }
        }
    }

    internal sealed class TilesetViewSnapshot : ITilesetView
    {
        private TilesetViewSnapshot(ITilesetView source)
        {
            TilesetId = source.TilesetId;
            DisplayName = source.DisplayName;
            TotalRoomsRange = source.TotalRoomsRange;
            MainPathRoomsRange = source.MainPathRoomsRange;
            BranchingRange = source.BranchingRange;
            SideBranchesRange = source.SideBranchesRange;
            DeadEndsRange = source.DeadEndsRange;
            DependencyDistanceRange = source.DependencyDistanceRange;
            BonusRoomChance = source.BonusRoomChance;
            SecretRoomChance = source.SecretRoomChance;
            LogicRoomShare = source.LogicRoomShare;
            DefaultExtractionMode = source.DefaultExtractionMode;
            DefaultExtractionAvailability = source.DefaultExtractionAvailability;
        }

        public static TilesetViewSnapshot Capture(ITilesetView source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new TilesetViewSnapshot(source);
        }

        public string TilesetId { get; }
        public string DisplayName { get; }
        public IntRange TotalRoomsRange { get; }
        public IntRange MainPathRoomsRange { get; }
        public IntRange BranchingRange { get; }
        public IntRange SideBranchesRange { get; }
        public IntRange DeadEndsRange { get; }
        public IntRange DependencyDistanceRange { get; }
        public float BonusRoomChance { get; }
        public float SecretRoomChance { get; }
        public float LogicRoomShare { get; }
        public ExtractionMode DefaultExtractionMode { get; }
        public ExtractionAvailability DefaultExtractionAvailability { get; }
    }

    internal sealed class RoomDatabaseSnapshot : IRoomDatabase
    {
        private readonly IReadOnlyList<IRoomTemplateDescriptor> _allRooms;
        private readonly Dictionary<string, List<IRoomTemplateDescriptor>> _byTileset = new(StringComparer.Ordinal);

        private RoomDatabaseSnapshot(IReadOnlyList<IRoomTemplateDescriptor> rooms)
        {
            _allRooms = rooms ?? Array.Empty<IRoomTemplateDescriptor>();
            for (int i = 0; i < _allRooms.Count; i++)
            {
                IRoomTemplateDescriptor room = _allRooms[i];
                if (room == null || string.IsNullOrEmpty(room.TilesetId))
                {
                    continue;
                }

                if (!_byTileset.TryGetValue(room.TilesetId, out List<IRoomTemplateDescriptor> list))
                {
                    list = new List<IRoomTemplateDescriptor>();
                    _byTileset[room.TilesetId] = list;
                }

                list.Add(room);
            }
        }

        public static RoomDatabaseSnapshot Capture(IRoomDatabase source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            IReadOnlyList<IRoomTemplateDescriptor> sourceRooms =
                source.AllRooms ?? Array.Empty<IRoomTemplateDescriptor>();
            List<IRoomTemplateDescriptor> snapshots = new(sourceRooms.Count);
            HashSet<string> captured = new(StringComparer.Ordinal);
            for (int i = 0; i < sourceRooms.Count; i++)
            {
                IRoomTemplateDescriptor room = sourceRooms[i];
                if (room == null || string.IsNullOrEmpty(room.RoomId))
                {
                    continue;
                }

                string key = $"{room.TilesetId}|{room.RoomId}";
                if (captured.Add(key))
                {
                    snapshots.Add(new RoomTemplateDescriptorSnapshot(room));
                }
            }

            return new RoomDatabaseSnapshot(snapshots);
        }

        public IReadOnlyList<IRoomTemplateDescriptor> AllRooms => _allRooms;

        public IReadOnlyList<IRoomTemplateDescriptor> GetRoomsForTileset(string tilesetId)
        {
            if (!string.IsNullOrEmpty(tilesetId)
                && _byTileset.TryGetValue(tilesetId, out List<IRoomTemplateDescriptor> rooms))
            {
                return rooms;
            }

            return Array.Empty<IRoomTemplateDescriptor>();
        }
    }
}
