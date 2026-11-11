using System;
using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using RedoSchool.LevelGeneration.Generation.Infrastructure;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    /// <summary>
    /// Regression guard for C1 (audit): the wall-OBB collision cache must roll back together with
    /// <c>_placedByNodeId</c> when <see cref="SocketBacktrackingRoomPlacer"/> backtracks. Before the fix the
    /// cache kept the geometry of an abandoned branch's pose, so <c>GetCollision</c> handed back stale OBBs and
    /// the placer could accept layouts whose rooms overlap (or reject valid ones). These graphs are dense
    /// enough to exercise dense cyclic placement; the final layout must stay overlap-free.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class CollisionCacheBacktrackTests
    {
        private const float RoomSizeMeters = 5f;

        [Test]
        public void Place_LoopGraph_ProducesOverlapFreeLayout()
        {
            LevelGraph graph = LoopGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = SquareRoomSelections(graph);

            // A few seeds so we exercise different door-pairing orders, all of which must converge on an
            // overlap-free layout once the collision cache is rolled back correctly.
            foreach (int seed in new[] { 1, 7, 42, 1234, 9999 })
            {
                LevelGenerationDebugCounters counters = new();
                SocketBacktrackingRoomPlacer placer = new(TestInteractiveRoomKinds.Standard);
                placer.SetDebugCounters(counters);

                LevelLayout layout = placer.Place(graph, selections, new SystemRandomRng(seed));

                Assert.AreEqual(graph.Nodes.Count, layout.Rooms.Count,
                    $"Seed {seed}: every node should be placed.");
                AssertNoOverlaps(layout, selections, seed);
            }
        }

        private static void AssertNoOverlaps(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            int seed)
        {
            HashSet<string> connectedPairs = new(StringComparer.Ordinal);
            foreach (DoorConnection connection in layout.Connections)
            {
                connectedPairs.Add(PairKey(connection.FromNodeId, connection.ToNodeId));
            }

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                for (int j = i + 1; j < layout.Rooms.Count; j++)
                {
                    PlacedRoom a = layout.Rooms[i];
                    PlacedRoom b = layout.Rooms[j];

                    // Two distinct rooms snap to a 5m door grid, so they must differ by a full cell on at
                    // least one axis. Sharing a cell means two 5m squares occupy the same footprint.
                    (float x, float z) pa = WorldPosition(a);
                    (float x, float z) pb = WorldPosition(b);
                    bool shareCell = Math.Abs(pa.x - pb.x) < RoomSizeMeters - 0.01f
                                     && Math.Abs(pa.z - pb.z) < RoomSizeMeters - 0.01f;
                    Assert.IsFalse(shareCell,
                        $"Seed {seed}: rooms {a.NodeId} and {b.NodeId} share a cell "
                        + $"(a=({pa.x},{pa.z}) b=({pb.x},{pb.z})).");

                    // Non-adjacent rooms share no door seam, so the wall SAT test (via RoomPlacer.Overlaps)
                    // must report no overlap. This is the check the stale collision cache used to fail.
                    if (connectedPairs.Contains(PairKey(a.NodeId, b.NodeId)))
                    {
                        continue;
                    }

                    selections.TryGetValue(a.NodeId, out IRoomTemplateDescriptor ta);
                    selections.TryGetValue(b.NodeId, out IRoomTemplateDescriptor tb);
                    Assert.IsFalse(RoomPlacer.Overlaps(a.Pose, ta, b.Pose, tb),
                        $"Seed {seed}: non-adjacent rooms {a.NodeId} and {b.NodeId} overlap.");
                }
            }
        }

        private static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "->" + b : b + "->" + a;

        private static (float x, float z) WorldPosition(PlacedRoom room)
        {
            return room.WorldPose.HasValue
                ? (room.WorldPose.Value.X, room.WorldPose.Value.Z)
                : (room.Pose.CellX * RoomSizeMeters, room.Pose.CellY * RoomSizeMeters);
        }

        // A graph with a cycle (n1->n2->n3->n4->n1) plus side branches. The regression contract is that
        // cyclic placement must not leave stale collision geometry behind and accept overlapping rooms.
        private static LevelGraph LoopGraph()
        {
            RoomNode[] nodes = { new("n_start", RoomKind.Start, 1), new("n1", RoomKind.Passage, 1), new("n2", RoomKind.Passage, 1), new("n3", RoomKind.Passage, 1), new("n4", RoomKind.Passage, 1), new("n5", RoomKind.Passage, 1), new("n6", RoomKind.Passage, 1) };
            LevelGraphEdge[] edges = { new("n_start", "n1", true), new("n1", "n2", true), new("n2", "n3", true), new("n3", "n4", true), new("n4", "n1", false), new("n2", "n5", false), new("n3", "n6", false) };
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
