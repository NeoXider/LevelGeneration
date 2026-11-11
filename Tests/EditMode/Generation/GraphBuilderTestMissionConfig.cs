using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    internal class GraphBuilderTestMissionConfig : IMissionConfigView
    {
        public string MissionId => "mission";
        public string TilesetId => "test";
        public virtual MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
        public int Difficulty => 1;
        public virtual OptionalIntRange TotalRoomsRange => new(1, 20);
        public virtual OptionalIntRange MainPathRoomsRange => new(4, 7);
        public virtual OptionalIntRange BranchingRange => new(4, 4);
        public virtual OptionalIntRange SideBranchesRange => new(0, 0);
        public virtual OptionalIntRange DeadEndsRange => new(0, 0);
        public virtual bool AllowLoops => false;
        public OptionalFloat BonusRoomChance => default;
        public OptionalFloat SecretRoomChance => default;
        public OptionalFloat LogicRoomShare => default;
        public ExtractionMode ExtractionMode => ExtractionMode.SeparateExitRoom;
        public int ExtractionRoomsCount => 1;
        public ExtractionAvailability ExtractionAvailability => ExtractionAvailability.AlwaysAvailable;
        public float ExtractionUnlockDelaySeconds => 0f;
        public virtual IReadOnlyList<RoomKind> RequiredRoomKinds => Array.Empty<RoomKind>();
        public virtual IReadOnlyList<RoomKind> OptionalRoomKinds => Array.Empty<RoomKind>();
        public virtual IReadOnlyList<IMissionRoomRule> MissionRoomRules => Array.Empty<IMissionRoomRule>();
        public virtual IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => Array.Empty<IRoomKindSpawnRule>();
        public IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => Array.Empty<IRoomWeightOverride>();
        public IReadOnlyList<IRoomKindWeightOverride> RoomKindWeightOverrides => Array.Empty<IRoomKindWeightOverride>();
        public virtual LevelGenerationMode GenerationMode => LevelGenerationMode.Procedural;
        public virtual IReadOnlyList<IHybridSequenceStep> HybridSequence => Array.Empty<IHybridSequenceStep>();
    }
}
