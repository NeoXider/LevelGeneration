using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class DeadEndRoomPostProcessorTests
    {
        [Test]
        public void Process_AttachesOneDoorPassageToDisabledConnectableDoor()
        {
            FakeRoomTemplate hostTemplate = new(
                "room_maze",
                RoomKind.Interactive,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("entry", direction: DoorDirection.West), new FakeDoorSocket("wrong_answer", direction: DoorDirection.North, initialState: DoorState.Locked) });
            FakeRoomTemplate twoDoorPassage = new(
                "room_tunnel",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("west", direction: DoorDirection.West), new FakeDoorSocket("east", direction: DoorDirection.East) });
            FakeRoomTemplate oneDoorPassage = new(
                "room_dead_end",
                RoomKind.Passage,
                canBeDeadEnd: false,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("south", direction: DoorDirection.South) });
            LevelLayout layout = new(
                new[] { Room("n_maze", hostTemplate, new Dictionary<string, DoorState> { ["entry"] = DoorState.Open, ["wrong_answer"] = DoorState.Disabled }) },
                Array.Empty<DoorConnection>(),
                "n_maze",
                Array.Empty<string>());
            LevelGraph graph = new(
                new[] { new RoomNode("n_maze", RoomKind.Interactive, 2, contentId: "maze") },
                Array.Empty<LevelGraphEdge>());

            DeadEndRoomPostProcessResult result = new DeadEndRoomPostProcessor(new IRoomTemplateDescriptor[] { hostTemplate, twoDoorPassage, oneDoorPassage }).Process(layout, graph, new FirstRollRng());

            Assert.AreEqual(1, result.AddedRoomCount);
            Assert.AreEqual(2, result.Layout.Rooms.Count);
            Assert.AreEqual(1, result.Layout.Connections.Count);
            Assert.AreEqual(2, result.Graph.Nodes.Count);
            Assert.AreEqual(1, result.Graph.Edges.Count);

            PlacedRoom host = result.Layout.Rooms.Single(room => room.NodeId == "n_maze");
            PlacedRoom deadEnd = result.Layout.Rooms.Single(room => room.NodeId != "n_maze");
            DoorConnection connection = result.Layout.Connections[0];

            Assert.AreEqual("room_dead_end", deadEnd.RoomTemplateId);
            Assert.AreEqual(DoorState.Closed, host.DoorStates["wrong_answer"],
                "A disabled answer door with a physical dead-end room behind it must become a closed door.");
            Assert.AreEqual(DoorState.Closed, deadEnd.DoorStates["south"]);
            Assert.AreEqual("n_maze", connection.FromNodeId);
            Assert.AreEqual("wrong_answer", connection.FromSocketId);
            Assert.AreEqual(deadEnd.NodeId, connection.ToNodeId);
            Assert.AreEqual("south", connection.ToSocketId);
            Assert.IsFalse(connection.IsMainPath);
            Assert.IsTrue(result.Graph.Nodes.Single(node => node.NodeId == deadEnd.NodeId).IsDeadEnd);
        }

        [Test]
        public void Process_SkipsDeadEndWhenCandidateWouldOverlapExistingRoom()
        {
            FakeRoomTemplate hostTemplate = new(
                "room_host",
                RoomKind.Passage,
                canBeSideBranch: false,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("east", direction: DoorDirection.East) });
            FakeRoomTemplate blockerTemplate = new("room_blocker", RoomKind.Passage);
            FakeRoomTemplate deadEndTemplate = new(
                "room_dead_end",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("west", direction: DoorDirection.West) });
            LevelLayout layout = new(
                new[]
                {
                    Room("n_host", hostTemplate, new Dictionary<string, DoorState> { ["east"] = DoorState.Disabled }), new PlacedRoom(
                        "n_blocker",
                        blockerTemplate.RoomId,
                        new GridPose(2f, 0f, 0),
                        new Dictionary<string, DoorState>())
                },
                Array.Empty<DoorConnection>(),
                "n_host",
                Array.Empty<string>());
            LevelGraph graph = new(
                new[] { new RoomNode("n_host", RoomKind.Passage, 1), new RoomNode("n_blocker", RoomKind.Passage, 0) },
                Array.Empty<LevelGraphEdge>());

            DeadEndRoomPostProcessResult result = new DeadEndRoomPostProcessor(new IRoomTemplateDescriptor[] { hostTemplate, blockerTemplate, deadEndTemplate }).Process(layout, graph, new FirstRollRng());

            Assert.AreEqual(0, result.AddedRoomCount);
            Assert.AreSame(layout, result.Layout);
            Assert.AreSame(graph, result.Graph);
        }

        private static PlacedRoom Room(
            string nodeId,
            IRoomTemplateDescriptor template,
            IReadOnlyDictionary<string, DoorState> states)
        {
            return new PlacedRoom(
                nodeId,
                template.RoomId,
                new GridPose(0f, 0f, 0),
                states,
                new RoomWorldPose(0f, 0f, 0f, 0f, 0f, 0f, 1f),
                template.Footprint,
                RoomFootprintShape.Rectangle(template.Footprint));
        }
    }
}
