using System;
using System.Collections.Generic;
using System.Threading;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Door-anchor placer. Coordinates are expressed in global door-snap units.
    /// Rooms may have different sizes and may rotate in 45-degree steps; placement is valid when
    /// two door anchors coincide, their yaws oppose each other and rotated room bounds do not overlap.
    /// </summary>
    public sealed class RoomPlacer : IRoomPlacer, IRoomPlacementDebugCountersSink
    {
        private static readonly int[] RotationCandidates = { 0, 45, 90, 135, 180, 225, 270, 315 };
        private LevelGenerationDebugCounters _debugCounters;

        public void SetDebugCounters(LevelGenerationDebugCounters debugCounters) => _debugCounters = debugCounters;

        public LevelLayout Place(
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken ct = default)
        {
            if (graph == null || graph.Nodes.Count == 0)
            {
                return new LevelLayout(Array.Empty<PlacedRoom>(), Array.Empty<DoorConnection>(), "n_start",
                    Array.Empty<string>());
            }

            Dictionary<string, PlacedRoom> placedByNodeId = new(graph.Nodes.Count);
            Dictionary<string, HashSet<string>> usedSocketsByNodeId = new(graph.Nodes.Count);
            List<DoorConnection> connections = new(graph.Edges.Count);
            IReadOnlyDictionary<string, List<string>> adjacency = BuildAdjacency(graph);
            IReadOnlyDictionary<string, bool> edgeMainPathByKey = BuildEdgeMainPathLookup(graph);
            IReadOnlyDictionary<string, string> contentByNodeId = BuildContentLookup(graph);

            RoomNode startNode = graph.Nodes[0];
            IRoomTemplateDescriptor startTemplate =
                selections.TryGetValue(startNode.NodeId, out IRoomTemplateDescriptor startTpl) ? startTpl : null;
            PlacedRoom startPlaced = new(
                startNode.NodeId,
                startTemplate?.RoomId ?? "MISSING_START",
                new GridPose(0f, 0f, 0),
                EmptyDoorStates(startTemplate),
                footprint: startTemplate?.Footprint,
                footprintShape: RoomFootprintShapeResolver.ShapeFor(startTemplate),
                contentId: startNode.ContentId);
            placedByNodeId[startNode.NodeId] = startPlaced;
            usedSocketsByNodeId[startNode.NodeId] = new HashSet<string>(StringComparer.Ordinal);

            Queue<string> queue = new();
            queue.Enqueue(startNode.NodeId);

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string currentId = queue.Dequeue();
                if (!adjacency.TryGetValue(currentId, out List<string> neighbors))
                {
                    continue;
                }

                if (!placedByNodeId.TryGetValue(currentId, out PlacedRoom currentPlaced))
                {
                    continue;
                }

                IRoomTemplateDescriptor currentTemplate =
                    selections.TryGetValue(currentId, out IRoomTemplateDescriptor curTpl) ? curTpl : null;

                for (int i = 0; i < neighbors.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string nextId = neighbors[i];
                    if (placedByNodeId.ContainsKey(nextId))
                    {
                        continue;
                    }

                    IRoomTemplateDescriptor nextTemplate =
                        selections.TryGetValue(nextId, out IRoomTemplateDescriptor nextTpl) ? nextTpl : null;

                    usedSocketsByNodeId.TryGetValue(currentId, out HashSet<string> usedCurrentSockets);
                    PlacementCandidate? candidate = TryPlaceNext(
                        currentPlaced,
                        currentTemplate,
                        nextId,
                        nextTemplate,
                        placedByNodeId,
                        selections,
                        rng,
                        usedCurrentSockets,
                        contentByNodeId,
                        _debugCounters);
                    if (!candidate.HasValue)
                    {
                        _debugCounters?.IncrementDomainEmptyRejects();
                        continue;
                    }

                    PlacementCandidate placement = candidate.Value;
                    placedByNodeId[nextId] = placement.Room;
                    ReserveSocket(usedSocketsByNodeId, currentId, placement.CurrentSocketId);
                    ReserveSocket(usedSocketsByNodeId, nextId, placement.NextSocketId);
                    connections.Add(new DoorConnection(
                        currentId,
                        placement.CurrentSocketId,
                        nextId,
                        placement.NextSocketId,
                        edgeMainPathByKey.TryGetValue(EdgeKey(currentId, nextId), out bool isMainPath)
                        && isMainPath));
                    queue.Enqueue(nextId);
                }
            }

            List<PlacedRoom> rooms = new(placedByNodeId.Count);
            List<string> exits = new();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                if (!placedByNodeId.TryGetValue(node.NodeId, out PlacedRoom placed))
                {
                    continue;
                }

                rooms.Add(placed);
                if (node.Kind == RoomKind.Exit)
                {
                    exits.Add(node.NodeId);
                }
            }

            return new LevelLayout(rooms, connections, startNode.NodeId, exits);
        }

        private static PlacementCandidate? TryPlaceNext(
            PlacedRoom currentPlaced,
            IRoomTemplateDescriptor currentTemplate,
            string nextNodeId,
            IRoomTemplateDescriptor nextTemplate,
            IReadOnlyDictionary<string, PlacedRoom> alreadyPlaced,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            ISet<string> usedCurrentSockets,
            IReadOnlyDictionary<string, string> contentByNodeId,
            LevelGenerationDebugCounters debugCounters)
        {
            if (currentTemplate?.Doors == null || nextTemplate?.Doors == null)
            {
                return null;
            }

            List<int> currentDoorIndices = Indices(currentTemplate.Doors.Count, rng);
            List<int> nextDoorIndices = Indices(nextTemplate.Doors.Count, rng);
            List<int> rotations = RotationList(rng);

            for (int c = 0; c < currentDoorIndices.Count; c++)
            {
                IDoorSocketDescriptor currentDoor = currentTemplate.Doors[currentDoorIndices[c]];
                if (!currentDoor.IsConnectable())
                {
                    continue;
                }

                if (IsSocketUsed(usedCurrentSockets, currentDoor.SocketId))
                {
                    continue;
                }

                (float x, float z) currentPoint = DoorWorldPoint(currentPlaced.Pose, currentTemplate, currentDoor);
                float currentYaw = NormalizeYaw(currentPlaced.Pose.RotationDegrees + currentDoor.LocalYawDegrees);

                for (int n = 0; n < nextDoorIndices.Count; n++)
                {
                    IDoorSocketDescriptor nextDoor = nextTemplate.Doors[nextDoorIndices[n]];
                    if (!nextDoor.IsConnectable() || IsLockedDoor(nextDoor))
                    {
                        continue;
                    }

                    if (currentDoor.Size != nextDoor.Size)
                    {
                        continue;
                    }

                    if (!DoorFramesCompatible(currentDoor, nextDoor))
                    {
                        continue;
                    }

                    for (int r = 0; r < rotations.Count; r++)
                    {
                        int rotation = rotations[r];
                        float nextYaw = NormalizeYaw(rotation + nextDoor.LocalYawDegrees);
                        if (!Opposes(currentYaw, nextYaw))
                        {
                            continue;
                        }

                        (float x, float z) nextLocalPoint = Rotate(LocalDoorPoint(nextDoor), rotation);
                        GridPose pose = new(currentPoint.x - nextLocalPoint.x, currentPoint.z - nextLocalPoint.z,
                            rotation);
                        if (OverlapsAny(
                                pose,
                                nextTemplate,
                                alreadyPlaced,
                                selections,
                                currentPlaced.NodeId,
                                currentPoint))
                        {
                            debugCounters?.IncrementOverlapRejects();
                            continue;
                        }

                        debugCounters?.IncrementCandidatesBuilt();
                        return new PlacementCandidate(
                            new PlacedRoom(
                                nextNodeId,
                                nextTemplate?.RoomId ?? "MISSING_ROOM",
                                pose,
                                EmptyDoorStates(nextTemplate),
                                footprint: nextTemplate?.Footprint,
                                footprintShape: RoomFootprintShapeResolver.ShapeFor(nextTemplate),
                                contentId: contentByNodeId != null &&
                                           contentByNodeId.TryGetValue(nextNodeId, out string contentId)
                                    ? contentId
                                    : null),
                            currentDoor.SocketId,
                            nextDoor.SocketId);
                    }
                }
            }

            return null;
        }

        private static bool IsSocketUsed(ISet<string> usedSockets, string socketId) => usedSockets != null && usedSockets.Contains(socketId);

        private static void ReserveSocket(IDictionary<string, HashSet<string>> usedSocketsByNodeId, string nodeId,
            string socketId)
        {
            if (!usedSocketsByNodeId.TryGetValue(nodeId, out HashSet<string> usedSockets))
            {
                usedSockets = new HashSet<string>(StringComparer.Ordinal);
                usedSocketsByNodeId[nodeId] = usedSockets;
            }

            usedSockets.Add(socketId);
        }

        private static IReadOnlyDictionary<string, string> BuildContentLookup(LevelGraph graph)
        {
            Dictionary<string, string> result = new(graph.Nodes.Count, StringComparer.Ordinal);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = graph.Nodes[i];
                if (!string.IsNullOrWhiteSpace(node.ContentId))
                {
                    result[node.NodeId] = node.ContentId;
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, bool> BuildEdgeMainPathLookup(LevelGraph graph)
        {
            Dictionary<string, bool> result = new(graph.Edges.Count, StringComparer.Ordinal);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                string key = EdgeKey(edge.FromNodeId, edge.ToNodeId);
                result[key] = result.TryGetValue(key, out bool existing)
                    ? existing || edge.IsMainPath
                    : edge.IsMainPath;
            }

            return result;
        }

        private static string EdgeKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}->{b}" : $"{b}->{a}";

        public static bool Overlaps(
            GridPose aPose,
            RoomFootprint aFootprint,
            GridPose bPose,
            RoomFootprint bFootprint)
        {
            Obb a = BuildObb(aPose, aFootprint);
            Obb b = BuildObb(bPose, bFootprint);
            return Overlaps(a, b);
        }

        public static bool Overlaps(
            GridPose aPose,
            IRoomTemplateDescriptor aTemplate,
            GridPose bPose,
            IRoomTemplateDescriptor bTemplate)
        {
            if (RoomWallCollision.TryOverlaps(
                    aPose,
                    aTemplate,
                    bPose,
                    bTemplate,
                    null,
                    out bool wallOverlaps))
            {
                return wallOverlaps;
            }

            return Overlaps(
                aPose,
                RoomFootprintShapeResolver.ShapeFor(aTemplate),
                bPose,
                RoomFootprintShapeResolver.ShapeFor(bTemplate));
        }

        private static bool OverlapsAny(
            GridPose pose,
            IRoomTemplateDescriptor template,
            IReadOnlyDictionary<string, PlacedRoom> alreadyPlaced,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            string connectedNodeId = null,
            (float x, float z)? connectedSeamPointDoorSteps = null)
        {
            RoomFootprintShape candidateShape = RoomFootprintShapeResolver.ShapeFor(template);
            foreach (KeyValuePair<string, PlacedRoom> kv in alreadyPlaced)
            {
                PlacedRoom placed = kv.Value;
                IRoomTemplateDescriptor placedTemplate =
                    selections.TryGetValue(placed.NodeId, out IRoomTemplateDescriptor selected) ? selected : null;
                (float x, float z)? seamPoint = connectedSeamPointDoorSteps.HasValue
                                                && string.Equals(placed.NodeId, connectedNodeId,
                                                    StringComparison.Ordinal)
                    ? connectedSeamPointDoorSteps
                    : null;
                if (RoomWallCollision.TryOverlaps(
                        pose,
                        template,
                        placed.Pose,
                        placedTemplate,
                        seamPoint,
                        out bool wallOverlaps))
                {
                    if (wallOverlaps)
                    {
                        return true;
                    }

                    continue;
                }

                RoomFootprintShape placedShape = RoomFootprintShapeResolver.ShapeFor(placedTemplate);
                if (Overlaps(
                        pose,
                        candidateShape,
                        placed.Pose,
                        placedShape))
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyDictionary<string, DoorState> EmptyDoorStates(IRoomTemplateDescriptor template)
        {
            if (template?.Doors == null || template.Doors.Count == 0)
            {
                return new Dictionary<string, DoorState>(0);
            }

            Dictionary<string, DoorState> dict = new(template.Doors.Count);
            for (int i = 0; i < template.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = template.Doors[i];
                dict[door.SocketId] = door.IsConnectable() ? InitialState(door) : DoorState.Disabled;
            }

            return dict;
        }

        private static DoorState InitialState(IDoorSocketDescriptor door)
        {
            return door is IDoorSocketInitialStateDescriptor initialState
                ? initialState.InitialState
                : DoorState.Closed;
        }

        private static bool IsLockedDoor(IDoorSocketDescriptor door)
        {
            return door is IDoorSocketInitialStateDescriptor initialState
                   && initialState.InitialState == DoorState.Locked;
        }

        public static (float x, float z) DoorWorldPoint(GridPose pose, IRoomTemplateDescriptor template,
            IDoorSocketDescriptor door)
        {
            (float x, float z) local = LocalDoorPoint(door);
            (float x, float z) rotated = Rotate(local, pose.RotationDegrees);
            return (pose.CellX + rotated.x, pose.CellY + rotated.z);
        }

        private static (float x, float z) LocalDoorPoint(IDoorSocketDescriptor door) => (door.ConnectionLocalXDoorSteps(), door.ConnectionLocalZDoorSteps());

        private static (float x, float z) Rotate((float x, float z) point, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        private static bool Opposes(float aYaw, float bYaw)
        {
            float diff = Math.Abs(NormalizeYaw(aYaw - bYaw));
            if (diff > 180f)
            {
                diff = 360f - diff;
            }

            return Math.Abs(diff - 180f) <= 0.1f;
        }

        private static bool DoorFramesCompatible(IDoorSocketDescriptor a, IDoorSocketDescriptor b)
        {
            const float toleranceMeters = 0.35f;
            return Math.Abs(a.FrameWidthMeters() - b.FrameWidthMeters()) <= toleranceMeters;
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private static List<int> RotationList(IDeterministicRng rng) => new(RotationCandidates);

        private static List<int> Indices(int count, IDeterministicRng rng)
        {
            List<int> indices = new(count);
            for (int i = 0; i < count; i++)
            {
                indices.Add(i);
            }

            return indices;
        }

        private static Obb BuildObb(GridPose pose, RoomFootprint footprint)
        {
            float width = footprint.WidthCells * LevelGenerationGridMetrics.FootprintCellDoorSteps;
            float depth = footprint.DepthCells * LevelGenerationGridMetrics.FootprintCellDoorSteps;
            (float x, float z) p0 = Rotate((0f, 0f), pose.RotationDegrees);
            (float x, float z) p1 = Rotate((width, 0f), pose.RotationDegrees);
            (float x, float z) p2 = Rotate((width, depth), pose.RotationDegrees);
            (float x, float z) p3 = Rotate((0f, depth), pose.RotationDegrees);
            (float x, float z) origin = (x: pose.CellX, z: pose.CellY);
            return new Obb(
                new[] { Add(origin, p0), Add(origin, p1), Add(origin, p2), Add(origin, p3) });
        }

        private static bool Overlaps(
            GridPose aPose,
            RoomFootprintShape aShape,
            GridPose bPose,
            RoomFootprintShape bShape)
        {
            IReadOnlyList<RoomFootprintCell> aCells = aShape.Cells;
            IReadOnlyList<RoomFootprintCell> bCells = bShape.Cells;
            for (int a = 0; a < aCells.Count; a++)
            {
                Obb aObb = BuildCellObb(aPose, aCells[a]);
                for (int b = 0; b < bCells.Count; b++)
                {
                    if (Overlaps(aObb, BuildCellObb(bPose, bCells[b])))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static Obb BuildCellObb(GridPose pose, RoomFootprintCell cell)
        {
            float step = LevelGenerationGridMetrics.FootprintCellDoorSteps;
            float minX = cell.X * step;
            float minZ = cell.Z * step;
            float maxX = minX + step;
            float maxZ = minZ + step;
            (float x, float z) p0 = Rotate((minX, minZ), pose.RotationDegrees);
            (float x, float z) p1 = Rotate((maxX, minZ), pose.RotationDegrees);
            (float x, float z) p2 = Rotate((maxX, maxZ), pose.RotationDegrees);
            (float x, float z) p3 = Rotate((minX, maxZ), pose.RotationDegrees);
            (float x, float z) origin = (x: pose.CellX, z: pose.CellY);
            return new Obb(new[] { Add(origin, p0), Add(origin, p1), Add(origin, p2), Add(origin, p3) });
        }

        private static (float x, float z) Add((float x, float z) a, (float x, float z) b) => (a.x + b.x, a.z + b.z);

        private static bool Overlaps(Obb a, Obb b) => !HasSeparatingAxis(a, b) && !HasSeparatingAxis(b, a);

        private static bool HasSeparatingAxis(Obb source, Obb target)
        {
            const float epsilon = 0.001f;
            for (int i = 0; i < 4; i++)
            {
                (float x, float z) p0 = source.Points[i];
                (float x, float z) p1 = source.Points[(i + 1) % 4];
                (float x, float z) edge = (x: p1.x - p0.x, z: p1.z - p0.z);
                (float x, float z) axis = (x: -edge.z, z: edge.x);
                Project(source, axis, out float sourceMin, out float sourceMax);
                Project(target, axis, out float targetMin, out float targetMax);
                if (sourceMax <= targetMin + epsilon || targetMax <= sourceMin + epsilon)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Project(Obb obb, (float x, float z) axis, out float min, out float max)
        {
            min = max = Dot(obb.Points[0], axis);
            for (int i = 1; i < obb.Points.Length; i++)
            {
                float value = Dot(obb.Points[i], axis);
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }
        }

        private static float Dot((float x, float z) a, (float x, float z) b) => a.x * b.x + a.z * b.z;

        private readonly struct Obb
        {
            public readonly (float x, float z)[] Points;

            public Obb((float x, float z)[] points)
            {
                Points = points;
            }
        }

        private readonly struct PlacementCandidate
        {
            public readonly PlacedRoom Room;
            public readonly string CurrentSocketId;
            public readonly string NextSocketId;

            public PlacementCandidate(PlacedRoom room, string currentSocketId, string nextSocketId)
            {
                Room = room;
                CurrentSocketId = currentSocketId;
                NextSocketId = nextSocketId;
            }
        }

        private static IReadOnlyDictionary<string, List<string>> BuildAdjacency(LevelGraph graph)
        {
            Dictionary<string, List<string>> map = new(graph.Nodes.Count);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge e = graph.Edges[i];
                if (!map.TryGetValue(e.FromNodeId, out List<string> list))
                {
                    list = new List<string>(2);
                    map[e.FromNodeId] = list;
                }

                list.Add(e.ToNodeId);
            }

            return map;
        }
    }
}
