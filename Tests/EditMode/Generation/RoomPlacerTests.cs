using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class RoomPlacerTests
    {
        [Test]
        public void Place_ReturnsEmptyLayout_ForNullGraph()
        {
            LevelLayout layout = new RoomPlacer().Place(
                null,
                new Dictionary<string, IRoomTemplateDescriptor>(),
                new FirstRollRng());

            Assert.AreEqual(0, layout.Rooms.Count);
            Assert.AreEqual("n_start", layout.StartNodeId);
            Assert.AreEqual(0, layout.ExitNodeIds.Count);
        }

        [Test]
        public void Place_UsesPlaceholderStart_WhenSelectionMissing()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1) },
                Array.Empty<LevelGraphEdge>());

            LevelLayout layout = new RoomPlacer().Place(graph, new Dictionary<string, IRoomTemplateDescriptor>(),
                new FirstRollRng());

            Assert.AreEqual(1, layout.Rooms.Count);
            PlacedRoom start = layout.Rooms.Single();
            Assert.AreEqual("n_start", start.NodeId);
            Assert.AreEqual("MISSING_START", start.RoomTemplateId);
            Assert.AreEqual(0f, start.Pose.CellX);
            Assert.AreEqual(0f, start.Pose.CellY);
            Assert.AreEqual(0, start.DoorStates.Count);
        }

        [Test]
        public void Place_AssignsUniqueGridCellsAndInitialClosedDoorStates()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_corridor", RoomKind.Passage, 2), new RoomNode("n_exit", RoomKind.Exit, 1) },
                new[] { new LevelGraphEdge("n_start", "n_corridor", true), new LevelGraphEdge("n_corridor", "n_exit", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["n_start"] = new FakeRoomTemplate("room_start", RoomKind.Start, doors: TwoDoors()), ["n_corridor"] = new FakeRoomTemplate("room_corridor", RoomKind.Passage, doors: TwoDoors()), ["n_exit"] = new FakeRoomTemplate("room_exit", RoomKind.Exit, doors: TwoDoors()) };

            LevelLayout layout = new RoomPlacer().Place(graph, selections, new FirstRollRng());

            Assert.AreEqual(3, layout.Rooms.Select(r => (r.Pose.CellX, r.Pose.CellY)).Distinct().Count());
            Assert.IsTrue(layout.Rooms.All(r => r.DoorStates.Values.All(state => state == DoorState.Closed)));
        }

        [Test]
        public void Place_UsesTemplateFootprint_WhenAvoidingOccupiedCells()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_big", RoomKind.Passage, 3), new RoomNode("n_exit", RoomKind.Exit, 1) },
                new[] { new LevelGraphEdge("n_start", "n_big", true), new LevelGraphEdge("n_big", "n_exit", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate("room_start", RoomKind.Start, doors: TwoDoors()),
                ["n_big"] = new FakeRoomTemplate("room_big", RoomKind.Passage, doors: TwoDoors(),
                    footprint: new RoomFootprint(2, 2)),
                ["n_exit"] = new FakeRoomTemplate("room_exit", RoomKind.Exit, doors: TwoDoors())
            };

            LevelLayout layout = new RoomPlacer().Place(graph, selections, new FirstRollRng());

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                for (int j = i + 1; j < layout.Rooms.Count; j++)
                {
                    PlacedRoom a = layout.Rooms[i];
                    PlacedRoom b = layout.Rooms[j];
                    Assert.IsFalse(RoomPlacer.Overlaps(a.Pose, selections[a.NodeId].Footprint, b.Pose,
                        selections[b.NodeId].Footprint));
                }
            }
        }

        [Test]
        public void Overlaps_UsesOccupiedCells_ForLShapeRooms()
        {
            RoomFootprintShape lShape = new(
                new RoomFootprint(2, 2),
                new[] { new RoomFootprintCell(0, 0), new RoomFootprintCell(1, 0), new RoomFootprintCell(0, 1) });
            FakeRoomTemplate lRoom = new(
                "room_l",
                RoomKind.Passage,
                footprint: new RoomFootprint(2, 2),
                footprintShape: lShape);
            FakeRoomTemplate oneCell = new("room_one", RoomKind.Passage);

            Assert.IsFalse(RoomPlacer.Overlaps(
                new GridPose(0f, 0f, 0),
                lRoom,
                new GridPose(1f, 1f, 0),
                oneCell));

            Assert.IsTrue(RoomPlacer.Overlaps(
                new GridPose(0f, 0f, 0),
                lRoom,
                new GridPose(1f, 0f, 0),
                oneCell));
        }

        [Test]
        public void Overlaps_UsesWallSegments_WhenFootprintsOverlapButWallsDoNot()
        {
            FakeRoomTemplate roomA = new(
                "room_a",
                RoomKind.Passage,
                wallSegments: new[] { new RoomWallSegment2D(0f, 0f, 4f, 0f, 0.2f) });
            FakeRoomTemplate roomB = new(
                "room_b",
                RoomKind.Passage,
                wallSegments: new[] { new RoomWallSegment2D(0f, 4f, 4f, 4f, 0.2f) });

            Assert.IsFalse(RoomPlacer.Overlaps(
                new GridPose(0f, 0f, 0),
                roomA,
                new GridPose(0f, 0f, 0),
                roomB));
        }

        [Test]
        public void Overlaps_UsesWallSegments_WhenWallsIntersect()
        {
            FakeRoomTemplate roomA = new(
                "room_a",
                RoomKind.Passage,
                wallSegments: new[] { new RoomWallSegment2D(0f, 0f, 4f, 0f, 0.2f) });
            FakeRoomTemplate roomB = new(
                "room_b",
                RoomKind.Passage,
                wallSegments: new[] { new RoomWallSegment2D(2f, -2f, 2f, 2f, 0.2f) });

            Assert.IsTrue(RoomPlacer.Overlaps(
                new GridPose(0f, 0f, 0),
                roomA,
                new GridPose(0f, 0f, 0),
                roomB));
        }

        [Test]
        public void Place_RotatesRoomToUseCompatibleDoorForChosenDirection()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_tunnel", RoomKind.Passage, 2) },
                new[] { new LevelGraphEdge("n_start", "n_tunnel", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new() { ["n_start"] = new FakeRoomTemplate("room_start", RoomKind.Start, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("east", direction: DoorDirection.East) }), ["n_tunnel"] = new FakeRoomTemplate("room_tunnel", RoomKind.Passage, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("west", direction: DoorDirection.West), new FakeDoorSocket("east", direction: DoorDirection.East) }) };

            LevelLayout layout = new RoomPlacer().Place(graph, selections, new FirstRollRng());

            PlacedRoom tunnel = layout.Rooms.Single(room => room.NodeId == "n_tunnel");
            Assert.AreEqual(0, tunnel.Pose.RotationDegrees);
            Assert.AreEqual((2f, 0f), (tunnel.Pose.CellX, tunnel.Pose.CellY));
        }

        [Test]
        public void Place_DoorFrontier_WithNullGraph_DelegatesToRoomPlacer()
        {
            LevelLayout layout = new DoorFrontierRoomPlacer(TestInteractiveRoomKinds.Standard).Place(null,
                new Dictionary<string, IRoomTemplateDescriptor>(), new FirstRollRng());

            Assert.AreEqual(0, layout.Rooms.Count);
            Assert.AreEqual("n_start", layout.StartNodeId);
        }

        [Test]
        public void Place_DoorFrontier_WithNullSelections_Throws()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1) },
                Array.Empty<LevelGraphEdge>());

            Assert.Throws<ArgumentNullException>(() => new DoorFrontierRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                graph,
                null,
                new FirstRollRng()));
        }

        [Test]
        public void DoorWorldPoint_UsesUnityYawConvention()
        {
            FakeDoorSocket door = new(
                "north",
                direction: DoorDirection.North,
                localXSteps: 1,
                localZSteps: 2);

            (float x, float z) point = RoomPlacer.DoorWorldPoint(new GridPose(10f, 20f, 90), null, door);

            Assert.AreEqual(12f, point.x, 0.001f);
            Assert.AreEqual(19f, point.z, 0.001f);
        }

        [Test]
        public void RoomFootprintShape_NormalizesCells_ByDroppingOutOfRangeAndDeduplicating()
        {
            RoomFootprintShape shape = new(
                new RoomFootprint(3, 2),
                new[] { new RoomFootprintCell(0, 0), new RoomFootprintCell(0, 0), new RoomFootprintCell(3, 0), new RoomFootprintCell(2, 1) });

            Assert.AreEqual(2, shape.Cells.Count);
            Assert.AreEqual(new RoomFootprintCell(0, 0), shape.Cells[0]);
            Assert.AreEqual(new RoomFootprintCell(2, 1), shape.Cells[1]);
            Assert.IsFalse(shape.IsRectangle);
        }

        [Test]
        public void RoomFootprintShape_FallsBackToRectangle_WhenNoValidCells()
        {
            RoomFootprintShape shape = new(
                new RoomFootprint(2, 2),
                new[] { new RoomFootprintCell(-1, 0) });

            Assert.AreEqual(4, shape.Cells.Count);
            Assert.AreEqual(new RoomFootprintCell(0, 0), shape.Cells[0]);
            Assert.AreEqual(new RoomFootprintCell(1, 1), shape.Cells[3]);
            Assert.IsTrue(shape.IsRectangle);
        }


        private static IReadOnlyList<IDoorSocketDescriptor> TwoDoors() => new IDoorSocketDescriptor[] { new FakeDoorSocket("west", direction: DoorDirection.West), new FakeDoorSocket("east", direction: DoorDirection.East) };
    }
}
