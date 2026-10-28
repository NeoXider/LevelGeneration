using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    /// <summary>
    /// Regression coverage for maze-containment invariants of <see cref="DoorConnector"/>:
    /// a Locked incoming socket is never chosen as the start-side connection, while a Locked outgoing
    /// socket can be a gated exit, and every socket that is not part of a real <see cref="DoorConnection"/>
    /// ends Disabled so the player can never open a door that leads nowhere and escape the maze.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class DoorConnectorMazeContainmentTests
    {
        [Test]
        public void Connect_AllowsLockedOutgoingSocket_AsGatedExit()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East) }), ["b"] = new FakeRoomTemplate("room_b", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("b_west", DoorSize.Medium, DoorDirection.West) }) };
            // a_east is geometrically a perfect pair for b_west and is Locked on the outgoing side.
            LevelLayout layout = new(
                new[] { Room("a", "room_a", selections["a"], 0f, 0f, new Dictionary<string, DoorState> { ["a_east"] = DoorState.Locked }), Room("b", "room_b", selections["b"], 2f, 0f) },
                new DoorConnection[0],
                "a",
                new[] { "b" });

            LevelLayout connected = new DoorConnector().Connect(layout, graph, selections);

            Assert.AreEqual(1, connected.Connections.Count,
                "A Locked outgoing socket is a gated exit and can be selected as a live connection.");
            Assert.AreEqual(DoorState.Locked, connected.Rooms[0].DoorStates["a_east"],
                "The gated outgoing socket must stay Locked until room logic unlocks it.");
            Assert.AreEqual(DoorState.Open, connected.Rooms[1].DoorStates["b_west"],
                "The incoming side is not locked and should be opened by the connection.");
        }

        [Test]
        public void Connect_SealsEveryUnconnectedSocketDisabled()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("a", RoomKind.Passage, 2), new RoomNode("b", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("a", "b", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["a"] = new FakeRoomTemplate("room_a", RoomKind.Passage, doors: new IDoorSocketDescriptor[]
                {
                    new FakeDoorSocket("a_east", DoorSize.Medium, DoorDirection.East),
                    // Leads into empty space (no matching room/socket): must end Disabled.
                    new FakeDoorSocket("a_dead_north", DoorSize.Medium, DoorDirection.North),
                    // Runtime-only service door: not a procedural connector, but still usable at runtime.
                    new FakeDoorSocket(
                        "a_auto_south",
                        DoorSize.Medium,
                        DoorDirection.South,
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

            Assert.AreEqual(1, connected.Connections.Count);
            // The only real edge is the connected pair.
            Assert.AreEqual(DoorState.Open, connected.Rooms[0].DoorStates["a_east"]);
            Assert.AreEqual(DoorState.Open, connected.Rooms[1].DoorStates["b_west"]);
            // Ordinary unused connectors lead nowhere and must be Disabled. Runtime-only doors stay usable.
            Assert.AreEqual(DoorState.Disabled, connected.Rooms[0].DoorStates["a_dead_north"]);
            Assert.AreEqual(DoorState.Closed, connected.Rooms[0].DoorStates["a_auto_south"]);
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
