using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Generation.Infrastructure;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    /// <summary>
    /// Guards the wall-OBB caching + AABB broadphase optimisation in <see cref="SocketBacktrackingRoomPlacer"/>:
    /// the layout it produces must stay deterministic and overlap-free, the broadphase must actually fire on
    /// non-trivial layouts, and cancellation must be honoured. These are the behaviours that previously caused
    /// runaway placement time on WebGL.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class SocketBacktrackingRoomPlacerBroadphaseTests
    {
        private const float RoomSizeMeters = 5f;

        [Test]
        public void Place_ProducesIdenticalLayout_ForSameSeed()
        {
            LevelGraph graph = BranchingGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = SquareRoomSelections(graph);

            LevelLayout first = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, selections, new SystemRandomRng(1234));
            LevelLayout second = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, selections, new SystemRandomRng(1234));

            Assert.AreEqual(graph.Nodes.Count, first.Rooms.Count, "All nodes should be placed.");
            Assert.AreEqual(first.Rooms.Count, second.Rooms.Count);
            Assert.AreEqual(first.Connections.Count, second.Connections.Count);

            Dictionary<string, PlacedRoom> firstByNode = ByNode(first);
            Dictionary<string, PlacedRoom> secondByNode = ByNode(second);
            foreach (KeyValuePair<string, PlacedRoom> kv in firstByNode)
            {
                Assert.IsTrue(secondByNode.TryGetValue(kv.Key, out PlacedRoom other),
                    $"Node {kv.Key} missing from second run.");
                (float x, float z) a = WorldPosition(kv.Value);
                (float x, float z) b = WorldPosition(other);
                Assert.That(b.x, Is.EqualTo(a.x).Within(0.0001f), $"Node {kv.Key} X drifted between runs.");
                Assert.That(b.z, Is.EqualTo(a.z).Within(0.0001f), $"Node {kv.Key} Z drifted between runs.");
            }
        }

        [Test]
        public void Place_LargerLayout_ExercisesBroadphaseAndStaysOverlapFree()
        {
            LevelGraph graph = BranchingGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = SquareRoomSelections(graph);
            LevelGenerationDebugCounters counters = new();
            SocketBacktrackingRoomPlacer placer = new(TestInteractiveRoomKinds.Standard);
            placer.SetDebugCounters(counters);

            LevelLayout layout = placer.Place(graph, selections, new SystemRandomRng(99));

            Assert.AreEqual(graph.Nodes.Count, layout.Rooms.Count, "Every free, connectable room should be placed.");
            AssertNoRoomsShareACell(layout);

            // Broadphase must actually run on a multi-room layout, proving the optimisation is wired in...
            Assert.Greater(counters.BroadphaseSkips, 0,
                "Broadphase should skip detailed tests for far rooms on a multi-room layout.");
            // ...while still running the detailed test where rooms can touch (so correctness is preserved).
            Assert.Greater(counters.OverlapChecks, 0, "Detailed overlap tests should still run for nearby rooms.");
        }

        [Test]
        public void Place_RespectsPreCancelledToken()
        {
            LevelGraph graph = BranchingGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = SquareRoomSelections(graph);
            CancellationTokenSource cts = new();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, selections, new SystemRandomRng(7), cts.Token));
        }

        private static void AssertNoRoomsShareACell(LevelLayout layout)
        {
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                for (int j = i + 1; j < layout.Rooms.Count; j++)
                {
                    (float x, float z) a = WorldPosition(layout.Rooms[i]);
                    (float x, float z) b = WorldPosition(layout.Rooms[j]);
                    float dx = Math.Abs(a.x - b.x);
                    float dz = Math.Abs(a.z - b.z);
                    // Rooms snap to a 5m door grid, so two distinct rooms must differ by a full cell on at
                    // least one axis. Sharing a cell would mean two 5m squares overlap (a broadphase miss).
                    bool shareCell = dx < RoomSizeMeters - 0.01f && dz < RoomSizeMeters - 0.01f;
                    Assert.IsFalse(shareCell,
                        $"Rooms {layout.Rooms[i].NodeId} and {layout.Rooms[j].NodeId} overlap (dx={dx}, dz={dz}).");
                }
            }
        }

        private static Dictionary<string, PlacedRoom> ByNode(LevelLayout layout)
        {
            Dictionary<string, PlacedRoom> result = new(StringComparer.Ordinal);
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                result[layout.Rooms[i].NodeId] = layout.Rooms[i];
            }

            return result;
        }

        private static (float x, float z) WorldPosition(PlacedRoom room)
        {
            return room.WorldPose.HasValue
                ? (room.WorldPose.Value.X, room.WorldPose.Value.Z)
                : (room.Pose.CellX * RoomSizeMeters, room.Pose.CellY * RoomSizeMeters);
        }

        private static LevelGraph BranchingGraph()
        {
            RoomNode[] nodes = { new("n_start", RoomKind.Start, 1), new("n1", RoomKind.Passage, 2), new("n2", RoomKind.Passage, 3), new("n3", RoomKind.Passage, 1), new("n4", RoomKind.Passage, 2), new("n5", RoomKind.Passage, 1) };
            LevelGraphEdge[] edges = { new("n_start", "n1", true), new("n1", "n2", true), new("n2", "n3", true), new("n2", "n4", false), new("n4", "n5", false) };
            return new LevelGraph(nodes, edges);
        }

        private static Dictionary<string, IRoomTemplateDescriptor> SquareRoomSelections(LevelGraph graph)
        {
            Dictionary<string, IRoomTemplateDescriptor> selections = new(StringComparer.Ordinal);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                selections[node.NodeId] = SquareRoom("room_" + node.NodeId, node.Kind);
            }

            return selections;
        }

        private static SquareRoomTemplate SquareRoom(string roomId, RoomKind kind)
        {
            float half = RoomSizeMeters * 0.5f;
            IDoorSocketDescriptor[] doors = { new SquareDoor("east", half, 0f, 0f), new SquareDoor("west", -half, 0f, 180f), new SquareDoor("north", 0f, half, 90f), new SquareDoor("south", 0f, -half, 270f) };
            RoomWallSegment2D[] walls = { new(-half, -half, half, -half, 0.2f), new(-half, half, half, half, 0.2f), new(-half, -half, -half, half, 0.2f), new(half, -half, half, half, 0.2f) };
            return new SquareRoomTemplate(roomId, kind, doors, walls);
        }

        private sealed class SquareRoomTemplate : IRoomTemplateDescriptor, IRoomWallCollisionDescriptor
        {
            public SquareRoomTemplate(
                string roomId,
                RoomKind kind,
                IReadOnlyList<IDoorSocketDescriptor> doors,
                IReadOnlyList<RoomWallSegment2D> wallSegments)
            {
                RoomId = roomId;
                Kind = kind;
                Doors = doors;
                WallCollisionSegments = wallSegments;
            }

            public string RoomId { get; }
            public RoomKind Kind { get; }
            public string TilesetId => "test";
            public int Difficulty => 1;
            public int Weight => 1;
            public bool CanBeMainPath => true;
            public bool CanBeSideBranch => true;
            public bool CanBeStart => Kind == RoomKind.Start;
            public bool CanBeExit => Kind == RoomKind.Exit;
            public bool CanBeDeadEnd => true;
            public IReadOnlyList<string> Tags => Array.Empty<string>();
            public int EstimatedDurationSeconds => 1;
            public RoomFootprint Footprint => new(1, 1);
            public IReadOnlyList<IDoorSocketDescriptor> Doors { get; }
            public string ModuleAssetId => RoomId;
            public IReadOnlyList<RoomWallSegment2D> WallCollisionSegments { get; }
        }

        private sealed class SquareDoor : IDoorSocketDescriptor, IDoorSocketConnectionPointDescriptor,
            IDoorSocketInitialStateDescriptor
        {
            public SquareDoor(string socketId, float x, float z, float localYawDegrees)
            {
                SocketId = socketId;
                ConnectionLocalXMeters = x;
                ConnectionLocalZMeters = z;
                LocalYawDegrees = localYawDegrees;
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
            public bool CanBeLocked => false;
            public float ConnectionLocalXMeters { get; }
            public float ConnectionLocalYMeters => 0f;
            public float ConnectionLocalZMeters { get; }
            public DoorState InitialState => DoorState.Closed;
        }
    }
}
