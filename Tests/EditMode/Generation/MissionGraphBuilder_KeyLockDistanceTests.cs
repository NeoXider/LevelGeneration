using System;
using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Application.DependencyHints;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    [Category("LevelGeneration")]
    public sealed class MissionGraphBuilder_KeyLockDistanceTests
    {
        [Test]
        public void LevelValidator_AcceptsKeyLockDistanceInsideRange()
        {
            LevelLayout layout = KeyLockLayoutFactory.Create(3);
            LevelValidator validator = KeyLockLayoutFactory.CreateValidator(3, 6);

            LevelValidationResult result = validator.Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsTrue(result.IsValid);
            Assert.IsFalse(HasIssue(result, "KeyDependencyUnsatisfied"));
        }

        private static bool HasIssue(LevelValidationResult result, string code)
        {
            for (int i = 0; i < result.Issues.Count; i++)
            {
                if (result.Issues[i].Code == code)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class KeyLockLayoutFactory
    {
        public static LevelLayout Create(int distance)
        {
            List<PlacedRoom> rooms = new();
            List<DoorConnection> connections = new();

            const float roomSpacing = 2f;
            rooms.Add(Room("n_key", "room_key", 0f));
            for (int i = 1; i < distance; i++)
            {
                rooms.Add(Room($"n_mid_{i}", "room_corridor", i * roomSpacing));
            }

            rooms.Add(Room("n_lock", "room_lock", distance * roomSpacing));

            for (int i = 0; i < rooms.Count - 1; i++)
            {
                connections.Add(new DoorConnection(rooms[i].NodeId, "east", rooms[i + 1].NodeId, "west"));
            }

            return new LevelLayout(rooms, connections, "n_key", new[] { "n_lock" });
        }

        public static LevelValidator CreateValidator(int minDistance, int maxDistance)
        {
            IRoomTemplateDescriptor[] rooms = new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_key", RoomKind.Interactive), new FakeRoomTemplate("room_lock", RoomKind.Interactive), new FakeRoomTemplate("room_corridor", RoomKind.Passage) };

            return new LevelValidator(
                new FakeRoomDatabase(rooms),
                new[] { new StaticHintExtractor(new RoomDependencyHint("room_key", "room_lock", minDistance, maxDistance)) });
        }

        private static PlacedRoom Room(string nodeId, string templateId, float x)
        {
            return new PlacedRoom(
                nodeId,
                templateId,
                new GridPose(x, 0f, 0),
                new Dictionary<string, DoorState>());
        }
    }

    internal sealed class StaticHintExtractor : IRoomDependencyHintExtractor
    {
        private readonly RoomDependencyHint _hint;

        public StaticHintExtractor(RoomDependencyHint hint)
        {
            _hint = hint;
        }

        public IReadOnlyList<RoomDependencyHint> Extract(IReadOnlyList<IRoomTemplateDescriptor> rooms,
            ITilesetView tileset) =>
            new[] { _hint };
    }

    internal sealed class FakeRoomDatabase : IRoomDatabase
    {
        public FakeRoomDatabase(IReadOnlyList<IRoomTemplateDescriptor> rooms)
        {
            AllRooms = rooms;
        }

        public IReadOnlyList<IRoomTemplateDescriptor> AllRooms { get; }

        public IReadOnlyList<IRoomTemplateDescriptor> GetRoomsForTileset(string tilesetId) => AllRooms;
    }

    internal class FakeRoomTemplate : IRoomTemplateDescriptor, IRoomFootprintShapeDescriptor,
        IRoomPlacementBoundsDescriptor, IRoomWallCollisionDescriptor, IInteractiveRoomTemplateDescriptor
    {
        public FakeRoomTemplate(
            string roomId,
            RoomKind kind,
            int requiredDoorCount = 2,
            int weight = 1,
            IReadOnlyList<IDoorSocketDescriptor> doors = null,
            bool canBeMainPath = true,
            bool canBeSideBranch = true,
            bool canBeDeadEnd = true,
            RoomFootprint footprint = default,
            RoomFootprintShape? footprintShape = null,
            float? placementBoundsMinX = null,
            float? placementBoundsMinZ = null,
            IReadOnlyList<RoomWallSegment2D> wallSegments = null,
            string interactiveKind = null,
            int interactiveOptionCapacity = 0,
            string moduleAssetId = null)
        {
            RoomId = roomId;
            Kind = kind;
            Weight = weight;
            Doors = doors ?? CreateDefaultDoors(requiredDoorCount);
            CanBeMainPath = canBeMainPath;
            CanBeSideBranch = canBeSideBranch;
            CanBeDeadEnd = canBeDeadEnd;
            Footprint = footprint.WidthCells > 0 ? footprint : new RoomFootprint(1, 1);
            FootprintShape = footprintShape ?? RoomFootprintShape.Rectangle(Footprint);
            HasPlacementBounds = placementBoundsMinX.HasValue || placementBoundsMinZ.HasValue;
            PlacementBoundsMinXMeters = placementBoundsMinX.GetValueOrDefault();
            PlacementBoundsMinZMeters = placementBoundsMinZ.GetValueOrDefault();
            WallCollisionSegments = wallSegments ?? Array.Empty<RoomWallSegment2D>();
            InteractiveKind = interactiveKind;
            InteractiveOptionCapacity = interactiveOptionCapacity;
            ModuleAssetId = moduleAssetId;
        }

        public string RoomId { get; }
        public RoomKind Kind { get; }
        public string TilesetId => "test";
        public int Difficulty => 1;
        public int Weight { get; }
        public bool CanBeMainPath { get; }
        public bool CanBeSideBranch { get; }
        public bool CanBeStart => Kind == RoomKind.Start;
        public bool CanBeExit => Kind == RoomKind.Exit;
        public bool CanBeDeadEnd { get; }
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public int EstimatedDurationSeconds => 1;
        public IReadOnlyList<IDoorSocketDescriptor> Doors { get; }
        public RoomFootprint Footprint { get; }
        public RoomFootprintShape FootprintShape { get; }
        public bool HasPlacementBounds { get; }
        public float PlacementBoundsMinXMeters { get; }
        public float PlacementBoundsMinZMeters { get; }
        public IReadOnlyList<RoomWallSegment2D> WallCollisionSegments { get; }
        public string ModuleAssetId { get; }
        public string InteractiveKind { get; }
        public int InteractiveOptionCapacity { get; }

        private static IReadOnlyList<IDoorSocketDescriptor> CreateDefaultDoors(int count)
        {
            if (count <= 0)
            {
                return Array.Empty<IDoorSocketDescriptor>();
            }

            IDoorSocketDescriptor[] doors = new IDoorSocketDescriptor[count];
            for (int i = 0; i < doors.Length; i++)
            {
                doors[i] = new FakeDoorSocket($"door_{i}", direction: DirectionForIndex(i));
            }

            return doors;
        }

        private static DoorDirection DirectionForIndex(int index)
        {
            return (index % 4) switch
            {
                0 => DoorDirection.East,
                1 => DoorDirection.West,
                2 => DoorDirection.North,
                _ => DoorDirection.South
            };
        }
    }

    internal sealed class FakeDoorSocket : IDoorSocketDescriptor, IDoorSocketInitialStateDescriptor,
        IDoorSocketFlowDescriptor
    {
        public FakeDoorSocket(
            string socketId,
            DoorSize size = DoorSize.Medium,
            DoorDirection direction = DoorDirection.East,
            int gridOffsetSteps = 1,
            int localXSteps = -1,
            int localZSteps = -1,
            float localYawDegrees = float.NaN,
            DoorState initialState = DoorState.Closed,
            DoorSocketFlowDirection flowDirection = DoorSocketFlowDirection.Both)
        {
            SocketId = socketId;
            Size = size;
            Direction = direction;
            GridOffsetSteps = gridOffsetSteps;
            LocalXSteps = localXSteps >= 0 ? localXSteps : DefaultX(direction);
            LocalZSteps = localZSteps >= 0 ? localZSteps : DefaultZ(direction);
            LocalYawDegrees = float.IsNaN(localYawDegrees) ? DefaultYaw(direction) : localYawDegrees;
            InitialState = initialState;
            FlowDirection = flowDirection;
        }

        public string SocketId { get; }
        public DoorSize Size { get; }
        public DoorDirection Direction { get; }
        public int GridOffsetSteps { get; }
        public int LocalXSteps { get; }
        public int LocalZSteps { get; }
        public float LocalYawDegrees { get; }
        public bool CanBeMainPath => true;
        public bool CanBeSidePath => true;
        public bool CanBeLocked => true;
        public DoorState InitialState { get; }
        public DoorSocketFlowDirection FlowDirection { get; }

        private static int DefaultX(DoorDirection direction)
        {
            return direction switch
            {
                DoorDirection.West => 0,
                DoorDirection.North or DoorDirection.South => 1,
                _ => 2
            };
        }

        private static int DefaultZ(DoorDirection direction)
        {
            return direction switch
            {
                DoorDirection.South => 0,
                DoorDirection.East or DoorDirection.West => 1,
                _ => 2
            };
        }

        private static float DefaultYaw(DoorDirection direction)
        {
            return direction switch
            {
                DoorDirection.South => 90f,
                DoorDirection.West => 180f,
                DoorDirection.North => 270f,
                _ => 0f
            };
        }
    }

    internal class FakeMissionConfig : IMissionConfigView
    {
        public string MissionId => "mission";
        public string TilesetId => "test";
        public virtual MissionArchetypeKind ArchetypeKind => MissionArchetypeKind.Simple;
        public int Difficulty => 1;
        public virtual OptionalIntRange TotalRoomsRange => new(1, 10);
        public OptionalIntRange MainPathRoomsRange => new(1, 10);
        public OptionalIntRange BranchingRange => new(4, 4);
        public OptionalIntRange SideBranchesRange => new(0, 0);
        public OptionalIntRange DeadEndsRange => new(0, 0);
        public bool AllowLoops => false;
        public OptionalFloat BonusRoomChance => default;
        public OptionalFloat SecretRoomChance => default;
        public OptionalFloat LogicRoomShare => default;
        public virtual ExtractionMode ExtractionMode => ExtractionMode.SeparateExitRoom;
        public virtual int ExtractionRoomsCount => 1;
        public ExtractionAvailability ExtractionAvailability => ExtractionAvailability.AlwaysAvailable;
        public float ExtractionUnlockDelaySeconds => 0f;
        public virtual IReadOnlyList<RoomKind> RequiredRoomKinds => Array.Empty<RoomKind>();
        public virtual IReadOnlyList<RoomKind> OptionalRoomKinds => Array.Empty<RoomKind>();
        public virtual IReadOnlyList<IMissionRoomRule> MissionRoomRules => Array.Empty<IMissionRoomRule>();
        public virtual IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => Array.Empty<IRoomKindSpawnRule>();
        public virtual IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => Array.Empty<IRoomWeightOverride>();

        public virtual IReadOnlyList<IRoomKindWeightOverride> RoomKindWeightOverrides =>
            Array.Empty<IRoomKindWeightOverride>();

        public virtual LevelGenerationMode GenerationMode => LevelGenerationMode.Procedural;
        public virtual IReadOnlyList<IHybridSequenceStep> HybridSequence => Array.Empty<IHybridSequenceStep>();
    }

    internal class FakeTileset : ITilesetView
    {
        public string TilesetId => "test";
        public string DisplayName => "Test";
        public IntRange TotalRoomsRange => new(1, 10);
        public IntRange MainPathRoomsRange => new(1, 10);
        public IntRange BranchingRange => new(4, 4);
        public IntRange SideBranchesRange => new(0, 0);
        public IntRange DeadEndsRange => new(0, 0);
        public IntRange DependencyDistanceRange => new(3, 6);
        public float BonusRoomChance => 0f;
        public float SecretRoomChance => 0f;
        public float LogicRoomShare => 0f;
        public ExtractionMode DefaultExtractionMode => ExtractionMode.SeparateExitRoom;
        public ExtractionAvailability DefaultExtractionAvailability => ExtractionAvailability.AlwaysAvailable;
    }
}
