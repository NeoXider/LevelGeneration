using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class DoorConnectorTests
    {
        [Test]
        public void Connect_OpensConnectedSocketsAndDisablesUnusedSockets()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East), new FakeDoorSocket("a_unused", DoorSize.Large, DoorDirection.North) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f), Room("b", "room_b", selections["b"], 2f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(1, connected.Connections.Count);
            Assert.AreEqual(DoorState.Open, connected.Rooms[0].DoorStates["a_east"]);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[0].DoorStates["a_unused"]);
            Assert.AreEqual(DoorState.Open, connected.Rooms[1].DoorStates["b_west"]);
        }

        [Test]
        public void Connect_DisablesUnusedRuntimeOnlyNoneDoors()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[]
                {
                    new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East), new FakeDoorSocket(
                        "a_runtime_only",
                        DoorSize.Medium,
                        DoorDirection.North,
                        flowDirection: DoorSocketFlowDirection.None)
                }),
                ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West) })
            };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f), Room("b", "room_b", selections["b"], 2f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            // Runtime-only FlowDirection.None doors are not procedural connectors; they stay usable for
            // authored service/UI doors, while ordinary unused connectors still seal to Disabled.
            Assert.AreEqual(1, connected.Connections.Count);
            Assert.AreEqual(DoorState.Open, connected.Rooms[0].DoorStates["a_east"]);
            Assert.AreEqual(DoorState.Closed, connected.Rooms[0].DoorStates["a_runtime_only"]);
            Assert.AreEqual(DoorState.Open, connected.Rooms[1].DoorStates["b_west"]);
        }

        [Test]
        public void Connect_DoesNotConnectSocketsFacingWrongDirection()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_north", DoorSize.Medium, DoorDirection.North) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0, 0), Room("b", "room_b", selections["b"], 1, 0) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(0, connected.Connections.Count);
        }

        [Test]
        public void Connect_DoesNotConnectSocketsOnDifferentGridOffsetLines()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East, 1) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West, 2, localZSteps: 4) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0, 0), Room("b", "room_b", selections["b"], 1, 0) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(0, connected.Connections.Count);
        }

        [Test]
        public void Connect_AllowsLockedOutgoingSocket()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f, new Dictionary<string, DoorState> { ["a_east"] = DoorState.Locked }), Room("b", "room_b", selections["b"], 2f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            // Locked outgoing doors are gated exits. The player unlocks them from the source room, so they
            // are valid live connections as long as the locked socket is not used as the incoming side.
            Assert.AreEqual(1, connected.Connections.Count);
            Assert.AreEqual(DoorState.Locked, connected.Rooms[0].DoorStates["a_east"]);
            Assert.AreEqual(DoorState.Open, connected.Rooms[1].DoorStates["b_west"]);
        }

        [Test]
        public void Connect_DisablesUnusedLockedSockets()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Interactive, 1), new RoomNode("b", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("a", "b", false) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Interactive, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_entry", DoorSize.Medium, DoorDirection.East), new FakeDoorSocket("a_answer_1", DoorSize.Medium, DoorDirection.North), new FakeDoorSocket("a_answer_2", DoorSize.Medium, DoorDirection.South) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f, new Dictionary<string, DoorState> { ["a_entry"] = DoorState.Closed, ["a_answer_1"] = DoorState.Locked, ["a_answer_2"] = DoorState.Locked }), Room("b", "room_b", selections["b"], 2f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(1, connected.Connections.Count);
            Assert.AreEqual(DoorState.Open, connected.Rooms[0].DoorStates["a_entry"]);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[0].DoorStates["a_answer_1"]);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[0].DoorStates["a_answer_2"]);
        }

        [Test]
        public void Connect_DropsInvalidPrecomputedConnectorPairs()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_north", DoorSize.Medium, DoorDirection.North) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_south", DoorSize.Medium, DoorDirection.South) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0, 0), Room("b", "room_b", selections["b"], 8, 8) },
                new[] { new DoorConnection("a", "a_north", "b", "b_south") },
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(0, connected.Connections.Count);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[0].DoorStates["a_north"]);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[1].DoorStates["b_south"]);
        }

        [Test]
        public void Connect_DoesNotAddIncidentalPairsByDefault()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 3), new RoomNode("c", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West), new FakeDoorSocket("b_east", DoorSize.Medium, DoorDirection.East) }), ["c"] = new FakeRoomTemplate("room_c", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("c_west", DoorSize.Medium, DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f), Room("b", "room_b", selections["b"], 2f, 0f), Room("c", "room_c", selections["c"], 4f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "c" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(1, connected.Connections.Count);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[1].DoorStates["b_east"]);
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[2].DoorStates["c_west"]);
        }


        [Test]
        public void Connect_AddsIncidentalPairsWhenExplicitlyEnabled()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 3), new RoomNode("c", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West), new FakeDoorSocket("b_east", DoorSize.Medium, DoorDirection.East) }), ["c"] = new FakeRoomTemplate("room_c", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("c_west", DoorSize.Medium, DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f), Room("b", "room_b", selections["b"], 2f, 0f), Room("c", "room_c", selections["c"], 4f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "c" });

            LevelLayout connected = new DoorConnector(true)
                .Connect(layout, graph, selections);

            Assert.AreEqual(2, connected.Connections.Count);
            Assert.AreEqual(DoorState.Open, connected.Rooms[1].DoorStates["b_east"]);
            Assert.AreEqual(DoorState.Open, connected.Rooms[2].DoorStates["c_west"]);
        }

        [Test]
        public void Connect_IncidentalDoorContactMode_SealsByDefault_AndConnectsOnlyOutsideGraphWhenAsked()
        {
            // Касание b_east/c_west не является ребром графа. Режим по умолчанию (и единственный годный для
            // MissionGenerator) — запечатать; соединять вне графа — только явный режим песочницы.
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 3), new RoomNode("c", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West), new FakeDoorSocket("b_east", DoorSize.Medium, DoorDirection.East) }), ["c"] = new FakeRoomTemplate("room_c", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("c_west", DoorSize.Medium, DoorDirection.West) }) };
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f), Room("b", "room_b", selections["b"], 2f, 0f), Room("c", "room_c", selections["c"], 4f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "c" });

            Assert.AreEqual(IncidentalDoorContactMode.Seal, new DoorConnector().IncidentalDoorContacts);
            Assert.AreEqual(IncidentalDoorContactMode.Seal, new DoorConnector(false).IncidentalDoorContacts);
            Assert.AreEqual(IncidentalDoorContactMode.ConnectOutsideGraph, new DoorConnector(true).IncidentalDoorContacts);

            LevelLayout sealedLayout = new DoorConnector(IncidentalDoorContactMode.Seal).Connect(layout, graph, selections);
            LevelLayout sandboxLayout =
                new DoorConnector(IncidentalDoorContactMode.ConnectOutsideGraph).Connect(layout, graph, selections);

            Assert.AreEqual(1, sealedLayout.Connections.Count);
            Assert.AreEqual(DoorState.Disabled, sealedLayout.Rooms[1].DoorStates["b_east"]);
            Assert.AreEqual(DoorState.Disabled, sealedLayout.Rooms[2].DoorStates["c_west"]);
            Assert.AreEqual(2, sandboxLayout.Connections.Count);
        }


        private static PlacedRoom Room(
            string nodeId,
            string templateId,
            IRoomTemplateDescriptor template,
            float x,
            float y,
            IReadOnlyDictionary<string, DoorState> overrides = null)
        {
            Dictionary<string, DoorState> states = new();
            for (int i = 0; i < template.Doors.Count; i++)
            {
                string socketId = template.Doors[i].SocketId;
                states[socketId] = overrides != null && overrides.TryGetValue(socketId, out DoorState state)
                    ? state
                    : DoorState.Closed;
            }

            return new PlacedRoom(nodeId, templateId, new GridPose(x, y, 0), states);
        }
    }
}
