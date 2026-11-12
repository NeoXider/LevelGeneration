using System;
using System.Collections.Generic;
using System.Threading;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Opt-in diagnostics for the dead-end capping pass: records, per free door, whether it got a cap and,
    /// if not, the aggregated reason (door incompatibility vs overlap). Off by default; enable around a
    /// single generation when investigating why MazeBranch answer doors stay Disabled.
    /// </summary>
    public static class DeadEndPlacementTrace
    {
        public readonly struct Record
        {
            public Record(string nodeId, string socketId, bool capped, string detail)
            {
                NodeId = nodeId ?? string.Empty;
                SocketId = socketId ?? string.Empty;
                Capped = capped;
                Detail = detail ?? string.Empty;
            }

            public string NodeId { get; }
            public string SocketId { get; }
            public bool Capped { get; }
            public string Detail { get; }
        }

        private static readonly object Gate = new();
        private static readonly List<Record> Records = new();

        public static bool Enabled { get; set; }

        public static void Reset()
        {
            lock (Gate)
            {
                Records.Clear();
            }
        }

        public static void RecordCap(string nodeId, string socketId, string templateId)
        {
            if (!Enabled)
            {
                return;
            }

            lock (Gate)
            {
                Records.Add(new Record(nodeId, socketId, true, templateId));
            }
        }

        public static void RecordSkip(string nodeId, string socketId, string reason)
        {
            if (!Enabled)
            {
                return;
            }

            lock (Gate)
            {
                Records.Add(new Record(nodeId, socketId, false, reason));
            }
        }

        public static IReadOnlyList<Record> Snapshot()
        {
            lock (Gate)
            {
                return Records.ToArray();
            }
        }
    }

    /// <summary>
    /// Final decoration pass: turns free disabled connectable doors into real short dead-end rooms when a
    /// one-door passage prefab from the current tileset can be docked without colliding with the layout.
    /// </summary>
    // TODO(deadend-inline): попробовать решать раньше — на этапе РАЗМЕЩЕНИЯ комнаты сразу помечать, будет ли
    // у неё ветвление (есть ли ещё что присоединять к её свободным дверям). Если ветвления не будет — ставить
    // dead-end для текущей комнаты прямо в процессе генерации и продолжать, вместо отдельного финального
    // пост-прохода. Должно сократить число «висящих» свободных дверей (а значит и запечатываний в стену) и
    // упростить этот класс до подчистки остатков. Затрагивает SocketBacktrackingRoomPlacer / MissionGraphBuilder.
    public sealed class DeadEndRoomPostProcessor
    {
        private const float DoorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;

        // Broadphase margin in meters: rooms whose wall AABBs cannot touch within this distance can never
        // overlap, so the expensive per-segment test is skipped. Mirrors SocketBacktrackingRoomPlacer.
        private const float BroadphaseMarginMeters = DoorSnapMeters;

        private readonly IReadOnlyList<IRoomTemplateDescriptor> _roomPool;

        // Wall geometry of a placed room, transformed once and reused for every candidate overlap test so the
        // dead-end pass is not O(rooms²) in full SAT checks on large layouts.
        private readonly struct RoomCollision
        {
            public RoomCollision(
                bool hasWalls,
                RoomWallCollision.TransformedWallSegments walls,
                RoomWallCollision.Aabb bounds,
                GridPose pose,
                IRoomTemplateDescriptor template)
            {
                HasWalls = hasWalls;
                Walls = walls;
                Bounds = bounds;
                Pose = pose;
                Template = template;
            }

            public bool HasWalls { get; }
            public RoomWallCollision.TransformedWallSegments Walls { get; }
            public RoomWallCollision.Aabb Bounds { get; }
            public GridPose Pose { get; }
            public IRoomTemplateDescriptor Template { get; }
        }

        private static RoomCollision BuildCollision(PlacedRoom room, IRoomTemplateDescriptor template)
        {
            if (RoomWallCollision.TryTransform(room, template,
                    out RoomWallCollision.TransformedWallSegments walls))
            {
                return new RoomCollision(true, walls, RoomWallCollision.BoundsOf(walls), room.Pose, template);
            }

            return new RoomCollision(false, default, RoomWallCollision.Aabb.Empty, room.Pose, template);
        }

        public DeadEndRoomPostProcessor(IReadOnlyList<IRoomTemplateDescriptor> roomPool)
        {
            _roomPool = roomPool ?? Array.Empty<IRoomTemplateDescriptor>();
        }

        public DeadEndRoomPostProcessResult Process(
            LevelLayout layout,
            LevelGraph graph,
            IDeterministicRng rng,
            CancellationToken ct = default)
        {
            if (layout == null || graph == null || layout.Rooms == null || layout.Rooms.Count == 0)
            {
                return new DeadEndRoomPostProcessResult(layout, graph, 0);
            }

            rng ??= new NullRng();
            List<IRoomTemplateDescriptor> deadEndTemplates = FindDeadEndTemplates();
            if (deadEndTemplates.Count == 0)
            {
                return new DeadEndRoomPostProcessResult(layout, graph, 0);
            }

            Dictionary<string, IRoomTemplateDescriptor> templatesById = BuildTemplateLookup(_roomPool);
            List<PlacedRoom> rooms = new(layout.Rooms);
            List<DoorConnection> connections = layout.Connections != null
                ? new List<DoorConnection>(layout.Connections)
                : new List<DoorConnection>();
            List<RoomNode> nodes = new(graph.Nodes);
            List<LevelGraphEdge> edges = new(graph.Edges);
            HashSet<string> usedSockets = BuildUsedSockets(connections);

            // Transform every placed room's walls once up front; candidate overlap tests then broadphase
            // against cached AABBs instead of re-transforming the whole layout per door (was O(rooms²)).
            Dictionary<string, RoomCollision> collisionByNode = new(StringComparer.Ordinal);
            for (int i = 0; i < rooms.Count; i++)
            {
                PlacedRoom r = rooms[i];
                if (r == null)
                {
                    continue;
                }

                templatesById.TryGetValue(r.RoomTemplateId, out IRoomTemplateDescriptor rTemplate);
                collisionByNode[r.NodeId] = BuildCollision(r, rTemplate);
            }

            int added = 0;
            int sealedCount = 0;
            for (int roomIndex = 0; roomIndex < rooms.Count; roomIndex++)
            {
                ct.ThrowIfCancellationRequested();
                PlacedRoom hostRoom = rooms[roomIndex];
                if (hostRoom == null
                    || !templatesById.TryGetValue(hostRoom.RoomTemplateId, out IRoomTemplateDescriptor hostTemplate)
                    || hostTemplate?.Doors == null)
                {
                    continue;
                }

                for (int doorIndex = 0; doorIndex < hostTemplate.Doors.Count; doorIndex++)
                {
                    IDoorSocketDescriptor hostDoor = hostTemplate.Doors[doorIndex];
                    if (!CanAcceptDeadEnd(hostRoom, hostDoor, usedSockets))
                    {
                        continue;
                    }

                    if (!TryPlaceDeadEnd(
                            hostRoom,
                            hostDoor,
                            deadEndTemplates,
                            collisionByNode,
                            rng,
                            added,
                            out PlacedRoom deadEndRoom,
                            out IRoomTemplateDescriptor deadEndTemplate,
                            out string deadEndSocketId,
                            out string failReason))
                    {
                        DeadEndPlacementTrace.RecordSkip(hostRoom.NodeId, hostDoor.SocketId, failReason);

                        // Already-Disabled doors are sealed walls already — leave the layout untouched.
                        if (hostRoom.DoorStates != null
                            && hostRoom.DoorStates.TryGetValue(hostDoor.SocketId, out DoorState existingState)
                            && existingState == DoorState.Disabled)
                        {
                            usedSockets.Add(SocketKey(hostRoom.NodeId, hostDoor.SocketId));
                            continue;
                        }

                        // Could not cap this free door with a dead-end room (перебрали все dead-end шаблоны),
                        // so seal it to Disabled — a wall, never a door-to-void. A MazeBranch wrong-answer door
                        // that lands here is an acceptable sealed wrong answer. Запечатанные двери — допустимый
                        // best-effort и НЕ отклоняют сид (см. MissionGenerator: только диагностика, без gate).
                        Dictionary<string, DoorState> sealedStates = CloneDoorStates(hostRoom.DoorStates);
                        sealedStates[hostDoor.SocketId] = DoorState.Disabled;
                        rooms[roomIndex] = new PlacedRoom(
                            hostRoom.NodeId,
                            hostRoom.RoomTemplateId,
                            hostRoom.Pose,
                            sealedStates,
                            hostRoom.WorldPose,
                            hostRoom.Footprint,
                            hostRoom.FootprintShape,
                            hostRoom.ContentId);
                        usedSockets.Add(SocketKey(hostRoom.NodeId, hostDoor.SocketId));
                        sealedCount++;
                        hostRoom = rooms[roomIndex];
                        continue;
                    }

                    DeadEndPlacementTrace.RecordCap(hostRoom.NodeId, hostDoor.SocketId, deadEndTemplate.RoomId);

                    Dictionary<string, DoorState> hostStates = CloneDoorStates(hostRoom.DoorStates);
                    hostStates[hostDoor.SocketId] = DoorState.Closed;
                    rooms[roomIndex] = new PlacedRoom(
                        hostRoom.NodeId,
                        hostRoom.RoomTemplateId,
                        hostRoom.Pose,
                        hostStates,
                        hostRoom.WorldPose,
                        hostRoom.Footprint,
                        hostRoom.FootprintShape,
                        hostRoom.ContentId);

                    rooms.Add(deadEndRoom);
                    collisionByNode[deadEndRoom.NodeId] = BuildCollision(deadEndRoom, deadEndTemplate);
                    connections.Add(new DoorConnection(
                        hostRoom.NodeId,
                        hostDoor.SocketId,
                        deadEndRoom.NodeId,
                        deadEndSocketId,
                        false));
                    nodes.Add(new RoomNode(
                        deadEndRoom.NodeId,
                        RoomKind.Passage,
                        1,
                        isOnMainPath: false,
                        isDeadEnd: true,
                        preferredRoomTemplateId: deadEndTemplate.RoomId));
                    edges.Add(new LevelGraphEdge(hostRoom.NodeId, deadEndRoom.NodeId, false));
                    usedSockets.Add(SocketKey(hostRoom.NodeId, hostDoor.SocketId));
                    usedSockets.Add(SocketKey(deadEndRoom.NodeId, deadEndSocketId));
                    added++;
                    hostRoom = rooms[roomIndex];
                }
            }

            if (added == 0 && sealedCount == 0)
            {
                return new DeadEndRoomPostProcessResult(layout, graph, 0);
            }

            LevelLayout processedLayout = new(rooms, connections, layout.StartNodeId, layout.ExitNodeIds);
            LevelGraph processedGraph = new(nodes, edges);
            return new DeadEndRoomPostProcessResult(processedLayout, processedGraph, added, sealedCount);
        }

        private bool TryPlaceDeadEnd(
            PlacedRoom hostRoom,
            IDoorSocketDescriptor hostDoor,
            IReadOnlyList<IRoomTemplateDescriptor> deadEndTemplates,
            IReadOnlyDictionary<string, RoomCollision> collisionByNode,
            IDeterministicRng rng,
            int index,
            out PlacedRoom deadEndRoom,
            out IRoomTemplateDescriptor deadEndTemplate,
            out string deadEndSocketId,
            out string failReason)
        {
            deadEndRoom = null;
            deadEndTemplate = null;
            deadEndSocketId = null;
            failReason = null;

            List<IRoomTemplateDescriptor> candidates = new(deadEndTemplates);
            Shuffle(candidates, rng);
            int noDoor = 0, sizeFail = 0, frameFail = 0, notExit = 0, notEntry = 0, overlapFail = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                IRoomTemplateDescriptor template = candidates[i];
                IDoorSocketDescriptor deadEndDoor = FindSingleConnectableDoor(template);
                if (deadEndDoor == null)
                {
                    noDoor++;
                    continue;
                }

                if (hostDoor.Size != deadEndDoor.Size)
                {
                    sizeFail++;
                    continue;
                }

                if (!DoorFramesCompatible(hostDoor, deadEndDoor))
                {
                    frameFail++;
                    continue;
                }

                if (!hostDoor.CanBeExit())
                {
                    notExit++;
                    continue;
                }

                if (!deadEndDoor.CanBeEntry())
                {
                    notEntry++;
                    continue;
                }

                (float x, float y, float z) hostPoint = DoorWorldPointMeters(hostRoom, hostDoor);
                float hostDoorYaw = NormalizeYaw(YawDegrees(hostRoom) + hostDoor.LocalYawDegrees);
                float deadEndYaw = NormalizeYaw(hostDoorYaw + 180f - deadEndDoor.LocalYawDegrees);
                (float x, float z) deadEndLocalDoor = RotateMeters(
                    (deadEndDoor.ConnectionLocalXMeters(), deadEndDoor.ConnectionLocalZMeters()),
                    deadEndYaw);
                float originX = hostPoint.x - deadEndLocalDoor.x;
                float originY = hostPoint.y - deadEndDoor.ConnectionLocalYMeters();
                float originZ = hostPoint.z - deadEndLocalDoor.z;
                string nodeId = $"{hostRoom.NodeId}__dead_end_{index}";
                PlacedRoom candidate = new(
                    nodeId,
                    template.RoomId,
                    new GridPose(originX / DoorSnapMeters, originZ / DoorSnapMeters, RoundYaw(deadEndYaw)),
                    ConnectedDeadEndDoorStates(template, deadEndDoor.SocketId),
                    CreateWorldPose(originX, originY, originZ, deadEndYaw),
                    template.Footprint,
                    RoomFootprintShapeResolver.ShapeFor(template));

                if (OverlapsAny(
                        candidate,
                        template,
                        hostRoom.NodeId,
                        (hostPoint.x, hostPoint.z),
                        collisionByNode))
                {
                    overlapFail++;
                    continue;
                }

                deadEndRoom = candidate;
                deadEndTemplate = template;
                deadEndSocketId = deadEndDoor.SocketId;
                return true;
            }

            failReason = candidates.Count == 0
                ? "no-deadend-templates"
                : $"size:{sizeFail} frame:{frameFail} hostNotExit:{notExit} capNotEntry:{notEntry} "
                  + $"noCapDoor:{noDoor} overlap:{overlapFail} of {candidates.Count} templates";
            return false;
        }

        private List<IRoomTemplateDescriptor> FindDeadEndTemplates()
        {
            List<IRoomTemplateDescriptor> result = new();
            for (int i = 0; i < _roomPool.Count; i++)
            {
                IRoomTemplateDescriptor room = _roomPool[i];
                // Dedicated DeadEnd rooms are the intended caps; one-door Passage rooms still qualify for
                // backward compatibility with tilesets that have no DeadEnd-kind room yet.
                if (room == null
                    || (room.Kind != RoomKind.DeadEnd && room.Kind != RoomKind.Passage)
                    || !room.CanBeSideBranch
                    || room.Weight <= 0
                    || CountConnectableDoors(room) != 1)
                {
                    continue;
                }

                result.Add(room);
            }

            result.Sort(static (a, b) => string.CompareOrdinal(a.RoomId, b.RoomId));
            return result;
        }

        private static bool CanAcceptDeadEnd(
            PlacedRoom room,
            IDoorSocketDescriptor door,
            ISet<string> usedSockets)
        {
            // Любая дверь, у которой ЕСТЬ направление потока (вход/выход/в обе стороны — door.IsConnectable),
            // но которая никуда не ведёт, должна получить тупиковую комнату вместо глухой заглушки.
            // Раньше ловились только Disabled-двери; теперь также Closed — это «должна была коннектиться,
            // но соединения не нашлось» (без неё игрок упирается в стену). Сокеты, уже занятые реальным
            // соединением, отсекаются проверкой usedSockets.
            return room != null
                   && door != null
                   && door.IsConnectable()
                   && room.DoorStates != null
                   && room.DoorStates.TryGetValue(door.SocketId, out DoorState state)
                   && (state == DoorState.Disabled || state == DoorState.Closed)
                   && !usedSockets.Contains(SocketKey(room.NodeId, door.SocketId));
        }

        private static bool OverlapsAny(
            PlacedRoom candidate,
            IRoomTemplateDescriptor candidateTemplate,
            string connectedNodeId,
            (float x, float z) connectedSeamPointMeters,
            IReadOnlyDictionary<string, RoomCollision> collisionByNode)
        {
            bool hasCandidateWalls = RoomWallCollision.TryTransform(
                candidate, candidateTemplate, out RoomWallCollision.TransformedWallSegments candidateWalls);
            RoomWallCollision.Aabb candidateBounds = hasCandidateWalls
                ? RoomWallCollision.BoundsOf(candidateWalls)
                : RoomWallCollision.Aabb.Empty;

            foreach (KeyValuePair<string, RoomCollision> kv in collisionByNode)
            {
                RoomCollision placed = kv.Value;
                bool isSeamNeighbor = string.Equals(kv.Key, connectedNodeId, StringComparison.Ordinal);

                if (hasCandidateWalls && placed.HasWalls)
                {
                    // Broadphase: wall AABBs too far apart can never overlap. Seam neighbours share a door, so
                    // their bounds always touch and the detailed seam-aware test still runs for them.
                    if (!candidateBounds.Overlaps(placed.Bounds, BroadphaseMarginMeters))
                    {
                        continue;
                    }

                    (float x, float z)? seam = isSeamNeighbor
                        ? connectedSeamPointMeters
                        : ((float x, float z)?)null;
                    if (RoomWallCollision.TryOverlaps(candidateWalls, placed.Walls, seam, out bool wallOverlaps))
                    {
                        if (wallOverlaps)
                        {
                            return true;
                        }

                        continue;
                    }
                }

                if (RoomPlacer.Overlaps(candidate.Pose, candidateTemplate, placed.Pose, placed.Template))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, IRoomTemplateDescriptor> BuildTemplateLookup(
            IReadOnlyList<IRoomTemplateDescriptor> rooms)
        {
            Dictionary<string, IRoomTemplateDescriptor> result = new(StringComparer.Ordinal);
            for (int i = 0; i < rooms.Count; i++)
            {
                IRoomTemplateDescriptor room = rooms[i];
                if (room != null && !string.IsNullOrEmpty(room.RoomId))
                {
                    result[room.RoomId] = room;
                }
            }

            return result;
        }

        private static HashSet<string> BuildUsedSockets(IReadOnlyList<DoorConnection> connections)
        {
            HashSet<string> result = new(StringComparer.Ordinal);
            if (connections == null)
            {
                return result;
            }

            for (int i = 0; i < connections.Count; i++)
            {
                DoorConnection connection = connections[i];
                result.Add(SocketKey(connection.FromNodeId, connection.FromSocketId));
                result.Add(SocketKey(connection.ToNodeId, connection.ToSocketId));
            }

            return result;
        }

        private static Dictionary<string, DoorState> CloneDoorStates(IReadOnlyDictionary<string, DoorState> states)
        {
            Dictionary<string, DoorState> result = new(StringComparer.Ordinal);
            if (states == null)
            {
                return result;
            }

            foreach (KeyValuePair<string, DoorState> kv in states)
            {
                result[kv.Key] = kv.Value;
            }

            return result;
        }

        private static IReadOnlyDictionary<string, DoorState> ConnectedDeadEndDoorStates(
            IRoomTemplateDescriptor template,
            string connectedSocketId)
        {
            Dictionary<string, DoorState> result = new(StringComparer.Ordinal);
            if (template?.Doors == null)
            {
                return result;
            }

            for (int i = 0; i < template.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = template.Doors[i];
                result[door.SocketId] = string.Equals(door.SocketId, connectedSocketId, StringComparison.Ordinal)
                    ? DoorState.Closed
                    : door.IsConnectable()
                        ? InitialState(door)
                        : DoorState.Disabled;
            }

            return result;
        }

        private static DoorState InitialState(IDoorSocketDescriptor door) => door is IDoorSocketInitialStateDescriptor state ? state.InitialState : DoorState.Closed;

        private static IDoorSocketDescriptor FindSingleConnectableDoor(IRoomTemplateDescriptor room)
        {
            if (room?.Doors == null)
            {
                return null;
            }

            IDoorSocketDescriptor result = null;
            for (int i = 0; i < room.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = room.Doors[i];
                if (door == null || !door.IsConnectable())
                {
                    continue;
                }

                if (result != null)
                {
                    return null;
                }

                result = door;
            }

            return result;
        }

        private static int CountConnectableDoors(IRoomTemplateDescriptor room)
        {
            int count = 0;
            if (room?.Doors == null)
            {
                return count;
            }

            for (int i = 0; i < room.Doors.Count; i++)
            {
                if (room.Doors[i] != null && room.Doors[i].IsConnectable())
                {
                    count++;
                }
            }

            return count;
        }

        private static bool DoorFramesCompatible(IDoorSocketDescriptor a, IDoorSocketDescriptor b)
        {
            const float toleranceMeters = 0.35f;
            return Math.Abs(a.FrameWidthMeters() - b.FrameWidthMeters()) <= toleranceMeters;
        }

        private static (float x, float y, float z) DoorWorldPointMeters(
            PlacedRoom room,
            IDoorSocketDescriptor door)
        {
            (float x, float y, float z) origin = room.WorldPose.HasValue
                ? (x: room.WorldPose.Value.X, y: room.WorldPose.Value.Y, z: room.WorldPose.Value.Z)
                : (x: room.Pose.CellX * DoorSnapMeters, y: 0f, z: room.Pose.CellY * DoorSnapMeters);
            float rotation = YawDegrees(room);
            (float x, float z) local = RotateMeters(
                (door.ConnectionLocalXMeters(), door.ConnectionLocalZMeters()),
                rotation);
            return (origin.x + local.x, origin.y + door.ConnectionLocalYMeters(), origin.z + local.z);
        }

        private static float YawDegrees(PlacedRoom room)
        {
            if (!room.WorldPose.HasValue)
            {
                return room.Pose.RotationDegrees;
            }

            RoomWorldPose pose = room.WorldPose.Value;
            float siny = 2f * (pose.RotationW * pose.RotationY + pose.RotationZ * pose.RotationX);
            float cosy = 1f - 2f * (pose.RotationY * pose.RotationY + pose.RotationX * pose.RotationX);
            return NormalizeYaw((float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI));
        }

        private static RoomWorldPose CreateWorldPose(float x, float y, float z, float yawDegrees)
        {
            double halfRadians = yawDegrees * Math.PI / 360.0;
            return new RoomWorldPose(
                x,
                y,
                z,
                0f,
                (float)Math.Sin(halfRadians),
                0f,
                (float)Math.Cos(halfRadians));
        }

        private static (float x, float z) RotateMeters((float x, float z) point, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        private static void Shuffle<T>(IList<T> list, IDeterministicRng rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int swapIndex = rng.NextInt(0, i + 1);
                (list[i], list[swapIndex]) = (list[swapIndex], list[i]);
            }
        }

        private static string SocketKey(string nodeId, string socketId) => $"{nodeId}|{socketId}";

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private static int RoundYaw(float yaw) => (int)Math.Round(NormalizeYaw(yaw));

        private sealed class NullRng : IDeterministicRng
        {
            public int NextInt(int minInclusive, int maxExclusive) => minInclusive;

            public float NextFloat() => 0f;

            public void Shuffle<T>(IList<T> list)
            {
            }
        }
    }

    public sealed class DeadEndRoomPostProcessResult
    {
        public DeadEndRoomPostProcessResult(
            LevelLayout layout,
            LevelGraph graph,
            int addedRoomCount,
            int sealedCount = 0)
        {
            Layout = layout;
            Graph = graph;
            AddedRoomCount = addedRoomCount;
            SealedCount = sealedCount;
        }

        public LevelLayout Layout { get; }
        public LevelGraph Graph { get; }

        /// <summary>Свободные двери, закрытые тупиковой комнатой.</summary>
        public int AddedRoomCount { get; }

        /// <summary>Свободные двери, которые закрыть dead-end-комнатой не удалось — запечатаны в стену.</summary>
        public int SealedCount { get; }
    }
}
