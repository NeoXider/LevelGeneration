using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Generation.Infrastructure;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class SocketBacktrackingRoomPlacerTests
    {
        [Test]
        public void Place_SupportsArbitraryDoorYawAndConnectsDoorCenters()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_room", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("n_start", "n_room", true) });
            MetricDoor startDoor = new("start_17", 0f, 0f, 17f);
            MetricDoor roomDoor = new("room_231", 2f, 0f, 231f);
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { startDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, -1f, 1f, -1f, 0.2f) }),
                ["n_room"] = new FakeRoomTemplate(
                    "room_any_angle",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { roomDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, 1f, 1f, 1f, 0.2f) })
            };

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, selections, new FirstRollRng());

            Assert.AreEqual(2, layout.Rooms.Count);
            Assert.AreEqual(1, layout.Connections.Count);
            PlacedRoom placedRoom = layout.Rooms.Single(room => room.NodeId == "n_room");
            Assert.IsTrue(placedRoom.WorldPose.HasValue);
            Assert.That(YawDegrees(placedRoom.WorldPose.Value), Is.EqualTo(326f).Within(0.01f));

            PlacedRoom start = layout.Rooms.Single(room => room.NodeId == "n_start");
            (float x, float z) startPoint = DoorWorldPoint(start, startDoor);
            (float x, float z) roomPoint = DoorWorldPoint(placedRoom, roomDoor);
            Assert.That(startPoint.x, Is.EqualTo(roomPoint.x).Within(0.001f));
            Assert.That(startPoint.z, Is.EqualTo(roomPoint.z).Within(0.001f));
        }

        [Test]
        public void Place_DoesNotConnectDisabledDoorSockets()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_room", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("n_start", "n_room", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("disabled", 0f, 0f, 0f, DoorState.Disabled) }),
                ["n_room"] = new FakeRoomTemplate(
                    "room_next",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("west", 0f, 0f, 180f) })
            };

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, selections, new FirstRollRng());

            Assert.AreEqual(1, layout.Rooms.Count);
            Assert.AreEqual(0, layout.Connections.Count);
        }

        [Test]
        public void Place_WithSnapshotDescriptors_KeepsDoorCentersAndYaw()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_room", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("n_start", "n_room", true) });
            MetricDoor startDoor = new("start_45", 3f, -1f, 45f);
            MetricDoor roomDoor = new("room_225", -4f, 2f, 225f);
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { startDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, -1f, 1f, -1f, 0.2f) }),
                ["n_room"] = new FakeRoomTemplate(
                    "room_45",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { roomDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, 1f, 1f, 1f, 0.2f) })
            };
            Dictionary<string, IRoomTemplateDescriptor> snapshots = RoomTemplateDescriptorSnapshot.Capture(selections);

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, snapshots, new FirstRollRng());

            Assert.AreEqual(2, layout.Rooms.Count);
            Assert.AreEqual(1, layout.Connections.Count);
            PlacedRoom start = layout.Rooms.Single(room => room.NodeId == "n_start");
            PlacedRoom placedRoom = layout.Rooms.Single(room => room.NodeId == "n_room");
            IDoorSocketDescriptor startSnapshotDoor = snapshots["n_start"].Doors.Single();
            IDoorSocketDescriptor roomSnapshotDoor = snapshots["n_room"].Doors.Single();
            (float x, float z) startPoint = DoorWorldPoint(start, startSnapshotDoor);
            (float x, float z) roomPoint = DoorWorldPoint(placedRoom, roomSnapshotDoor);
            Assert.That(startPoint.x, Is.EqualTo(roomPoint.x).Within(0.001f));
            Assert.That(startPoint.z, Is.EqualTo(roomPoint.z).Within(0.001f));
        }

        [Test]
        public void Place_DenseMazeBranchRoles_AssignAnswerSocketsWithoutPermutingDoors()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_maze", RoomKind.Interactive, 4, isOnMainPath: true, contentId: "Q_MAZE"), new RoomNode("n_correct_tunnel", RoomKind.Passage, 2, isOnMainPath: true), new RoomNode("n_exit", RoomKind.Exit, 1), new RoomNode("n_wrong_1", RoomKind.Passage, 1, isDeadEnd: true), new RoomNode("n_wrong_2", RoomKind.Passage, 1, isDeadEnd: true) },
                new[] { new LevelGraphEdge("n_start", "n_maze", true, EdgeBranchRole.MazeEntry), new LevelGraphEdge("n_maze", "n_correct_tunnel", true, EdgeBranchRole.MazeCorrectExit), new LevelGraphEdge("n_correct_tunnel", "n_exit", true), new LevelGraphEdge("n_maze", "n_wrong_1", false, EdgeBranchRole.MazeWrongDeadEnd), new LevelGraphEdge("n_maze", "n_wrong_2", false, EdgeBranchRole.MazeWrongDeadEnd) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("start_exit", 0f, 0f, 0f, flowDirection: DoorSocketFlowDirection.ExitOnly) }),
                ["n_maze"] = new FakeRoomTemplate(
                    "room_maze",
                    RoomKind.Interactive,
                    doors: new IDoorSocketDescriptor[]
                    {
                        new MetricDoor("entry", -10f, 0f, 180f, flowDirection: DoorSocketFlowDirection.EntryOnly), new MetricDoor("correct", 10f, 0f, 0f, DoorState.Locked,
                            DoorSocketFlowDirection.ExitOnly),
                        new MetricDoor("wrong_1", 0f, 10f, 270f, DoorState.Locked,
                            DoorSocketFlowDirection.ExitOnly),
                        new MetricDoor("wrong_2", 0f, -10f, 90f, DoorState.Locked,
                            DoorSocketFlowDirection.ExitOnly)
                    },
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    interactiveOptionCapacity: 3),
                ["n_correct_tunnel"] = new FakeRoomTemplate(
                    "room_correct_tunnel",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("correct_entry", 0f, 0f, 180f), new MetricDoor("correct_exit", 10f, 0f, 0f) }),
                ["n_exit"] = new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("exit_entry", 0f, 0f, 180f) }),
                ["n_wrong_1"] = new FakeRoomTemplate(
                    "room_wrong_1",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry_1", 0f, 0f, 90f) }),
                ["n_wrong_2"] = new FakeRoomTemplate(
                    "room_wrong_2",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry_2", 0f, 0f, 270f) })
            };

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                graph,
                selections,
                new SystemRandomRng(1499425446));

            Assert.AreEqual(graph.Nodes.Count, layout.Rooms.Count, "Dense MazeBranch graph should fully place.");
            DoorConnection correct = layout.Connections.Single(c => c.FromNodeId == "n_maze"
                                                                    && c.ToNodeId == "n_correct_tunnel");
            Assert.AreEqual("correct", correct.FromSocketId);
            Assert.IsTrue(correct.IsMainPath);

            DoorConnection wrong1 = layout.Connections.Single(c => c.FromNodeId == "n_maze"
                                                                   && c.ToNodeId == "n_wrong_1");
            DoorConnection wrong2 = layout.Connections.Single(c => c.FromNodeId == "n_maze"
                                                                   && c.ToNodeId == "n_wrong_2");
            Assert.AreEqual("wrong_1", wrong1.FromSocketId);
            Assert.AreEqual("wrong_2", wrong2.FromSocketId);
            Assert.IsFalse(wrong1.IsMainPath);
            Assert.IsFalse(wrong2.IsMainPath);
            Assert.IsTrue(graph.Nodes.Single(n => n.NodeId == "n_wrong_1").IsDeadEnd);
            Assert.IsTrue(graph.Nodes.Single(n => n.NodeId == "n_wrong_2").IsDeadEnd);
        }

        private static (float x, float z) DoorWorldPoint(PlacedRoom room, IDoorSocketDescriptor door)
        {
            RoomWorldPose pose = room.WorldPose.Value;
            (float x, float z) local = Rotate((door.ConnectionLocalXMeters(), door.ConnectionLocalZMeters()),
                YawDegrees(pose));
            return (pose.X + local.x, pose.Z + local.z);
        }

        private static float YawDegrees(RoomWorldPose pose)
        {
            float siny = 2f * (pose.RotationW * pose.RotationY + pose.RotationZ * pose.RotationX);
            float cosy = 1f - 2f * (pose.RotationY * pose.RotationY + pose.RotationX * pose.RotationX);
            float yaw = (float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private static (float x, float z) Rotate((float x, float z) point, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        private sealed class MetricDoor : IDoorSocketDescriptor, IDoorSocketMetricDescriptor,
            IDoorSocketConnectionPointDescriptor, IDoorSocketInitialStateDescriptor, IDoorSocketFlowDescriptor
        {
            public MetricDoor(
                string socketId,
                float x,
                float z,
                float localYawDegrees,
                DoorState initialState = DoorState.Closed,
                DoorSocketFlowDirection flowDirection = DoorSocketFlowDirection.Both)
            {
                SocketId = socketId;
                LocalXMeters = x;
                LocalZMeters = z;
                ConnectionLocalXMeters = x;
                ConnectionLocalYMeters = 0f;
                ConnectionLocalZMeters = z;
                LocalYawDegrees = localYawDegrees;
                InitialState = initialState;
                FlowDirection = flowDirection;
            }

            public string SocketId { get; }
            public DoorSize Size => DoorSize.Medium;
            public DoorDirection Direction => DoorDirection.East;
            public int GridOffsetSteps => 0;
            public int LocalXSteps => 0;
            public int LocalZSteps => 0;
            public float LocalYawDegrees { get; }
            public bool CanBeMainPath => true;
            public bool CanBeSidePath => true;
            public bool CanBeLocked => true;
            public float GridOriginXMeters => 0f;
            public float GridOriginZMeters => 0f;
            public float LocalXMeters { get; }
            public float LocalZMeters { get; }
            public float ConnectionLocalXMeters { get; }
            public float ConnectionLocalYMeters { get; }
            public float ConnectionLocalZMeters { get; }
            public DoorState InitialState { get; }
            public DoorSocketFlowDirection FlowDirection { get; }
        }
    }
}
