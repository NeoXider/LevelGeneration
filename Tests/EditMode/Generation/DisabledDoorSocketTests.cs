using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class DisabledDoorSocketTests
    {
        [Test]
        public void RoomSelector_DoesNotCountDisabledAnswerDoors()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "disabled_third_answer_door",
                    RoomKind.Passage,
                    3,
                    doors: new IDoorSocketDescriptor[] { new Door("a"), new Door("b"), new Door("disabled", initialState: DoorState.Disabled) }),
                new FakeRoomTemplate(
                    "valid_three_answer_doors",
                    RoomKind.Passage,
                    3,
                    doors: new IDoorSocketDescriptor[] { new Door("a"), new Door("b"), new Door("c") })
            });
            RoomNode node = new("n_junction", RoomKind.Passage, 3);

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new QuizBranchMissionConfig(3),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("valid_three_answer_doors", selected.RoomId);
        }

        [Test]
        public void DoorConnector_DoesNotConnectDisabledSockets()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new Door("a_east", DoorDirection.East, DoorState.Disabled) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new Door("b_west", DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f), Room("b", "room_b", selections["b"], 2f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(0, connected.Connections.Count);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[0].DoorStates["a_east"]);
        }

        [Test]
        public void DoorConnector_DoesNotPreservePrecomputedDisabledSocketConnection()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new Door("a_east", DoorDirection.East, DoorState.Disabled) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new Door("b_west", DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f), Room("b", "room_b", selections["b"], 2f, 0f) },
                new[] { new DoorConnection("a", "a_east", "b", "b_west") },
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(0, connected.Connections.Count);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[0].DoorStates["a_east"]);
        }

        [Test]
        public void RoomPlacer_DoesNotPlaceThroughDisabledSocket()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new Door("a_east", DoorDirection.East, DoorState.Disabled) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new Door("b_west", DoorDirection.West) }) };

            LevelLayout layout = new RoomPlacer().Place(graph, selections, new FirstRollRng());

            Assert.AreEqual(1, layout.Rooms.Count);
            Assert.AreEqual("a", layout.Rooms[0].NodeId);
            Assert.AreEqual(DoorState.Disabled, layout.Rooms[0].DoorStates["a_east"]);
        }

        private static PlacedRoom Room(string nodeId, string templateId, IRoomTemplateDescriptor template, float x,
            float y)
        {
            Dictionary<string, DoorState> states = new();
            for (int i = 0; i < template.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = template.Doors[i];
                states[door.SocketId] = door is IDoorSocketInitialStateDescriptor initial
                    ? initial.InitialState
                    : DoorState.Closed;
            }

            return new PlacedRoom(nodeId, templateId, new GridPose(x, y, 0), states);
        }

        private sealed class Door : IDoorSocketDescriptor, IDoorSocketInitialStateDescriptor
        {
            public Door(string socketId, DoorDirection direction = DoorDirection.East,
                DoorState initialState = DoorState.Closed)
            {
                SocketId = socketId;
                Direction = direction;
                InitialState = initialState;
            }

            public string SocketId { get; }
            public DoorSize Size => DoorSize.Medium;
            public DoorDirection Direction { get; }
            public int GridOffsetSteps => 1;

            public int LocalXSteps => Direction == DoorDirection.West ? 0 :
                Direction is DoorDirection.North or DoorDirection.South ? 1 : 2;

            public int LocalZSteps => Direction == DoorDirection.South ? 0 :
                Direction is DoorDirection.East or DoorDirection.West ? 1 : 2;

            public float LocalYawDegrees => Direction switch
            {
                DoorDirection.South => 90f,
                DoorDirection.West => 180f,
                DoorDirection.North => 270f,
                _ => 0f
            };

            public bool CanBeMainPath => true;
            public bool CanBeSidePath => true;
            public bool CanBeLocked => true;
            public DoorState InitialState { get; }
        }

        private sealed class QuizBranchMissionConfig : FakeMissionConfig, IQuizBranchMissionConfigView
        {
            public QuizBranchMissionConfig(int answerCount)
            {
                QuizBranchAnswerCount = answerCount;
            }

            public int QuizBranchAnswerCount { get; }
        }
    }
}
