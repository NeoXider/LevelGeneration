using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Что делать со «случайным касанием» — парой свободных дверей двух комнат, которые совпали геометрически
    /// (точка и встречный угол), но не являются ребром <see cref="LevelGraph"/>.
    /// </summary>
    public enum IncidentalDoorContactMode
    {
        /// <summary>
        /// По умолчанию: обе двери запечатываются (Disabled), как любой неиспользованный сокет. Топология миссии
        /// задаётся только графом — касание не открывает обход квиза, замка или главного пути. Единственный режим
        /// для <see cref="MissionGenerator"/>: его проверка с графом отклоняет соединения вне графа.
        /// </summary>
        Seal = 0,

        /// <summary>
        /// Песочница/отладка: касание становится соединением вне графа. <see cref="LevelValidator"/> с графом
        /// отклоняет такую раскладку (<c>ConnectionNotInGraph</c>), поэтому в <see cref="MissionGenerator"/> этот режим
        /// только сжигает сиды; генератор предупреждает о нём в <see cref="MissionGenerator.LastWarnings"/>.
        /// </summary>
        ConnectOutsideGraph = 1
    }

    /// <summary>
    /// Connects graph edges by matching already placed door anchors. A pair is valid when socket size matches,
    /// world-space door anchor points coincide and door yaws face opposite directions.
    /// <para>
    /// Для генерации миссий создавайте <c>new DoorConnector()</c> (<see cref="IncidentalDoorContactMode.Seal"/>):
    /// случайные касания дверей запечатываются. <c>new DoorConnector(true)</c> — только для песочниц без проверки графа.
    /// </para>
    /// </summary>
    public sealed class DoorConnector : IDoorConnector
    {
        public DoorConnector()
            : this(IncidentalDoorContactMode.Seal)
        {
        }

        public DoorConnector(IncidentalDoorContactMode incidentalDoorContacts)
        {
            IncidentalDoorContacts = incidentalDoorContacts;
        }

        /// <param name="connectIncidentalDoorContacts">
        /// <c>true</c> = <see cref="IncidentalDoorContactMode.ConnectOutsideGraph"/> (песочница/отладка). Для миссий,
        /// которые проверяет <see cref="MissionGenerator"/>, передавайте <c>false</c> или используйте конструктор без аргументов.
        /// </param>
        public DoorConnector(bool connectIncidentalDoorContacts)
            : this(connectIncidentalDoorContacts
                ? IncidentalDoorContactMode.ConnectOutsideGraph
                : IncidentalDoorContactMode.Seal)
        {
        }

        public IncidentalDoorContactMode IncidentalDoorContacts { get; }

        /// <summary>
        /// Disabled by default: generated mission topology should come from <see cref="LevelGraph"/> only.
        /// Enable only for debug/sandbox layouts where accidental adjacent doors are intentionally allowed.
        /// </summary>
        public bool ConnectIncidentalDoorContacts => IncidentalDoorContacts == IncidentalDoorContactMode.ConnectOutsideGraph;

        public LevelLayout Connect(
            LevelLayout layout,
            LevelGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections)
        {
            if (layout == null || graph == null)
            {
                return layout;
            }

            Dictionary<string, PlacedRoom> roomsByNodeId = BuildRoomLookup(layout);
            Dictionary<string, Dictionary<string, DoorState>> doorStates = new(layout.Rooms.Count,
                StringComparer.Ordinal);
            foreach (PlacedRoom room in layout.Rooms)
            {
                Dictionary<string, DoorState> states = new(StringComparer.Ordinal);
                if (room.DoorStates != null)
                {
                    foreach (KeyValuePair<string, DoorState> kv in room.DoorStates)
                    {
                        states[kv.Key] = kv.Value;
                    }
                }

                doorStates[room.NodeId] = states;
            }

            Dictionary<string, bool> graphEdgeMainPathByKey = BuildGraphEdgeLookup(graph);
            List<DoorConnection> connections = new(graph.Edges.Count + layout.Rooms.Count);
            HashSet<string> connectedEdges = new(StringComparer.Ordinal);
            HashSet<string> usedSockets = new(StringComparer.Ordinal);

            PreserveExistingConnections(
                layout,
                selections,
                roomsByNodeId,
                doorStates,
                graphEdgeMainPathByKey,
                connections,
                connectedEdges,
                usedSockets);

            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (edge.FromNodeId == edge.ToNodeId)
                {
                    continue;
                }

                string edgeKey = ConnectionKey(edge.FromNodeId, edge.ToNodeId);
                if (connectedEdges.Contains(edgeKey))
                {
                    continue;
                }

                if (!roomsByNodeId.TryGetValue(edge.FromNodeId, out PlacedRoom fromRoom)
                    || !roomsByNodeId.TryGetValue(edge.ToNodeId, out PlacedRoom toRoom)
                    || selections == null
                    || !selections.TryGetValue(edge.FromNodeId, out IRoomTemplateDescriptor fromTemplate)
                    || !selections.TryGetValue(edge.ToNodeId, out IRoomTemplateDescriptor toTemplate))
                {
                    continue;
                }

                (string fromSocket, string toSocket)? pair = FindPair(
                    fromRoom,
                    fromTemplate,
                    toRoom,
                    toTemplate,
                    doorStates[edge.FromNodeId],
                    doorStates[edge.ToNodeId],
                    edge.IsMainPath,
                    true);
                if (pair == null)
                {
                    continue;
                }

                (string fromSocket, string toSocket) = pair.Value;
                string fromSocketKey = SocketKey(edge.FromNodeId, fromSocket);
                string toSocketKey = SocketKey(edge.ToNodeId, toSocket);
                if (!usedSockets.Add(fromSocketKey))
                {
                    continue;
                }

                if (!usedSockets.Add(toSocketKey))
                {
                    usedSockets.Remove(fromSocketKey);
                    continue;
                }

                doorStates[edge.FromNodeId][fromSocket] = ConnectedState(doorStates[edge.FromNodeId], fromSocket);
                doorStates[edge.ToNodeId][toSocket] = IncomingConnectedState(doorStates[edge.ToNodeId], toSocket);
                connections.Add(new DoorConnection(
                    edge.FromNodeId,
                    fromSocket,
                    edge.ToNodeId,
                    toSocket,
                    edge.IsMainPath));
                connectedEdges.Add(edgeKey);
            }

            if (ConnectIncidentalDoorContacts)
            {
                AddIncidentalDoorConnections(layout, selections, doorStates, connections, connectedEdges, usedSockets);
            }

            SealUnusedSockets(doorStates, usedSockets, selections);

            List<PlacedRoom> rooms = new(layout.Rooms.Count);
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                rooms.Add(new PlacedRoom(
                    room.NodeId,
                    room.RoomTemplateId,
                    room.Pose,
                    doorStates[room.NodeId],
                    room.WorldPose,
                    room.Footprint,
                    room.FootprintShape,
                    room.ContentId));
            }

            return new LevelLayout(rooms, connections, layout.StartNodeId, layout.ExitNodeIds);
        }

        private static Dictionary<string, PlacedRoom> BuildRoomLookup(LevelLayout layout)
        {
            Dictionary<string, PlacedRoom> result = new(layout.Rooms.Count, StringComparer.Ordinal);
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (room != null && !string.IsNullOrEmpty(room.NodeId))
                {
                    result[room.NodeId] = room;
                }
            }

            return result;
        }

        private static Dictionary<string, bool> BuildGraphEdgeLookup(LevelGraph graph)
        {
            Dictionary<string, bool> result = new(graph.Edges.Count, StringComparer.Ordinal);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (edge.FromNodeId == edge.ToNodeId)
                {
                    continue;
                }

                string key = ConnectionKey(edge.FromNodeId, edge.ToNodeId);
                result[key] = result.TryGetValue(key, out bool existing)
                    ? existing || edge.IsMainPath
                    : edge.IsMainPath;
            }

            return result;
        }

        private static void PreserveExistingConnections(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IReadOnlyDictionary<string, PlacedRoom> roomsByNodeId,
            Dictionary<string, Dictionary<string, DoorState>> doorStates,
            IReadOnlyDictionary<string, bool> graphEdgeMainPathByKey,
            ICollection<DoorConnection> connections,
            ISet<string> connectedEdges,
            ISet<string> usedSockets)
        {
            if (layout.Connections == null || selections == null)
            {
                return;
            }

            for (int i = 0; i < layout.Connections.Count; i++)
            {
                DoorConnection connection = layout.Connections[i];
                string key = ConnectionKey(connection.FromNodeId, connection.ToNodeId);
                if (!graphEdgeMainPathByKey.TryGetValue(key, out bool mainPath) || connectedEdges.Contains(key))
                {
                    continue;
                }

                if (!TryValidateExistingConnection(
                        connection,
                        selections,
                        roomsByNodeId,
                        doorStates,
                        usedSockets,
                        mainPath))
                {
                    continue;
                }

                doorStates[connection.FromNodeId][connection.FromSocketId] =
                    ConnectedState(doorStates[connection.FromNodeId], connection.FromSocketId);
                doorStates[connection.ToNodeId][connection.ToSocketId] =
                    IncomingConnectedState(doorStates[connection.ToNodeId], connection.ToSocketId);
                connections.Add(connection);
                connectedEdges.Add(key);
            }
        }

        private static bool TryValidateExistingConnection(
            DoorConnection connection,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IReadOnlyDictionary<string, PlacedRoom> roomsByNodeId,
            IReadOnlyDictionary<string, Dictionary<string, DoorState>> doorStates,
            ISet<string> usedSockets,
            bool mainPath)
        {
            if (string.IsNullOrEmpty(connection.FromNodeId)
                || string.IsNullOrEmpty(connection.ToNodeId)
                || string.IsNullOrEmpty(connection.FromSocketId)
                || string.IsNullOrEmpty(connection.ToSocketId)
                || !roomsByNodeId.TryGetValue(connection.FromNodeId, out PlacedRoom fromRoom)
                || !roomsByNodeId.TryGetValue(connection.ToNodeId, out PlacedRoom toRoom)
                || !selections.TryGetValue(connection.FromNodeId, out IRoomTemplateDescriptor fromTemplate)
                || !selections.TryGetValue(connection.ToNodeId, out IRoomTemplateDescriptor toTemplate)
                || !doorStates.TryGetValue(connection.FromNodeId, out Dictionary<string, DoorState> fromStates)
                || !doorStates.TryGetValue(connection.ToNodeId, out Dictionary<string, DoorState> toStates)
                || !TryGetDoor(fromTemplate, connection.FromSocketId, out IDoorSocketDescriptor fromDoor)
                || !TryGetDoor(toTemplate, connection.ToSocketId, out IDoorSocketDescriptor toDoor))
            {
                return false;
            }

            string fromSocketKey = SocketKey(connection.FromNodeId, connection.FromSocketId);
            string toSocketKey = SocketKey(connection.ToNodeId, connection.ToSocketId);
            if (usedSockets.Contains(fromSocketKey) || usedSockets.Contains(toSocketKey))
            {
                return false;
            }

            if (!IsConnectableState(fromStates, connection.FromSocketId)
                || !IsConnectableState(toStates, connection.ToSocketId)
                || IsLockedState(toStates, connection.ToSocketId)
                || !fromDoor.CanBeExit()
                || !toDoor.CanBeEntry()
                || !SocketsAreGeometricallyCompatible(fromRoom, fromDoor, toRoom, toDoor, mainPath))
            {
                return false;
            }

            usedSockets.Add(fromSocketKey);
            usedSockets.Add(toSocketKey);
            return true;
        }

        private static void AddIncidentalDoorConnections(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDictionary<string, Dictionary<string, DoorState>> doorStates,
            ICollection<DoorConnection> connections,
            ISet<string> connectedEdges,
            ISet<string> usedSockets)
        {
            if (layout.Rooms == null || selections == null)
            {
                return;
            }

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom fromRoom = layout.Rooms[i];
                if (!selections.TryGetValue(fromRoom.NodeId, out IRoomTemplateDescriptor fromTemplate)
                    || !doorStates.TryGetValue(fromRoom.NodeId, out Dictionary<string, DoorState> fromStates))
                {
                    continue;
                }

                for (int j = i + 1; j < layout.Rooms.Count; j++)
                {
                    PlacedRoom toRoom = layout.Rooms[j];
                    string key = ConnectionKey(fromRoom.NodeId, toRoom.NodeId);
                    if (connectedEdges.Contains(key)
                        || !selections.TryGetValue(toRoom.NodeId, out IRoomTemplateDescriptor toTemplate)
                        || !doorStates.TryGetValue(toRoom.NodeId, out Dictionary<string, DoorState> toStates))
                    {
                        continue;
                    }

                    (string fromSocket, string toSocket)? pair = FindPair(
                        fromRoom,
                        fromTemplate,
                        toRoom,
                        toTemplate,
                        fromStates,
                        toStates,
                        false,
                        false);
                    if (pair == null)
                    {
                        continue;
                    }

                    (string fromSocket, string toSocket) = pair.Value;
                    string fromSocketKey = SocketKey(fromRoom.NodeId, fromSocket);
                    string toSocketKey = SocketKey(toRoom.NodeId, toSocket);
                    if (!usedSockets.Add(fromSocketKey))
                    {
                        continue;
                    }

                    if (!usedSockets.Add(toSocketKey))
                    {
                        usedSockets.Remove(fromSocketKey);
                        continue;
                    }

                    fromStates[fromSocket] = ConnectedState(fromStates, fromSocket);
                    toStates[toSocket] = IncomingConnectedState(toStates, toSocket);
                    connections.Add(new DoorConnection(fromRoom.NodeId, fromSocket, toRoom.NodeId, toSocket));
                    connectedEdges.Add(key);
                }
            }
        }

        private static string ConnectionKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

        private static string SocketKey(string nodeId, string socketId) => $"{nodeId}|{socketId}";

        private static (string fromSocket, string toSocket)? FindPair(
            PlacedRoom fromRoom,
            IRoomTemplateDescriptor fromTemplate,
            PlacedRoom toRoom,
            IRoomTemplateDescriptor toTemplate,
            IReadOnlyDictionary<string, DoorState> fromStates,
            IReadOnlyDictionary<string, DoorState> toStates,
            bool mainPath,
            bool enforceDirection)
        {
            if (fromTemplate.Doors == null || toTemplate.Doors == null)
            {
                return null;
            }

            for (int i = 0; i < fromTemplate.Doors.Count; i++)
            {
                IDoorSocketDescriptor fromDoor = fromTemplate.Doors[i];
                if (!CanUseDoor(fromDoor, mainPath)
                    || (enforceDirection && !fromDoor.CanBeExit())
                    || !IsFree(fromStates, fromDoor.SocketId)
                    || (!enforceDirection && IsLockedState(fromStates, fromDoor.SocketId)))
                {
                    continue;
                }

                for (int j = 0; j < toTemplate.Doors.Count; j++)
                {
                    IDoorSocketDescriptor toDoor = toTemplate.Doors[j];
                    if (!CanUseDoor(toDoor, mainPath)
                        || (enforceDirection && !toDoor.CanBeEntry())
                        || !IsFree(toStates, toDoor.SocketId)
                        || IsLockedState(toStates, toDoor.SocketId))
                    {
                        continue;
                    }

                    if (SocketsAreGeometricallyCompatible(fromRoom, fromDoor, toRoom, toDoor, mainPath))
                    {
                        return (fromDoor.SocketId, toDoor.SocketId);
                    }
                }
            }

            return null;
        }

        private static bool TryGetDoor(
            IRoomTemplateDescriptor template,
            string socketId,
            out IDoorSocketDescriptor door)
        {
            if (template?.Doors != null)
            {
                for (int i = 0; i < template.Doors.Count; i++)
                {
                    IDoorSocketDescriptor candidate = template.Doors[i];
                    if (candidate != null && string.Equals(candidate.SocketId, socketId, StringComparison.Ordinal))
                    {
                        door = candidate;
                        return true;
                    }
                }
            }

            door = null;
            return false;
        }

        private static bool SocketsAreGeometricallyCompatible(
            PlacedRoom fromRoom,
            IDoorSocketDescriptor fromDoor,
            PlacedRoom toRoom,
            IDoorSocketDescriptor toDoor,
            bool mainPath)
        {
            if (!CanUseDoor(fromDoor, mainPath)
                || !CanUseDoor(toDoor, mainPath)
                || fromDoor.Size != toDoor.Size
                || !DoorFramesCompatible(fromDoor, toDoor))
            {
                return false;
            }

            (float x, float z) fromPoint = DoorWorldPoint(fromRoom, fromDoor);
            (float x, float z) toPoint = DoorWorldPoint(toRoom, toDoor);
            if (!Approximately(fromPoint.x, toPoint.x) || !Approximately(fromPoint.z, toPoint.z))
            {
                return false;
            }

            return Opposes(DoorWorldYaw(fromRoom, fromDoor), DoorWorldYaw(toRoom, toDoor));
        }

        private static bool CanUseDoor(IDoorSocketDescriptor door, bool mainPath)
        {
            if (!door.IsConnectable())
            {
                return false;
            }

            return mainPath ? door.CanBeMainPath : door.CanBeSidePath;
        }

        private static bool DoorFramesCompatible(IDoorSocketDescriptor a, IDoorSocketDescriptor b)
        {
            const float toleranceMeters = 0.35f;
            return Math.Abs(a.FrameWidthMeters() - b.FrameWidthMeters()) <= toleranceMeters;
        }

        private static bool IsFree(IReadOnlyDictionary<string, DoorState> states, string socketId)
        {
            return !states.TryGetValue(socketId, out DoorState state)
                   || state == DoorState.Closed
                   || state == DoorState.Locked;
        }

        private static bool IsConnectableState(IReadOnlyDictionary<string, DoorState> states, string socketId) => !states.TryGetValue(socketId, out DoorState state) || state != DoorState.Disabled;

        private static DoorState ConnectedState(IReadOnlyDictionary<string, DoorState> states, string socketId)
        {
            return states.TryGetValue(socketId, out DoorState state) && state == DoorState.Locked
                ? DoorState.Locked
                : DoorState.Open;
        }

        private static DoorState IncomingConnectedState(IReadOnlyDictionary<string, DoorState> states, string socketId)
        {
            return states.TryGetValue(socketId, out DoorState state) && state == DoorState.Disabled
                ? DoorState.Disabled
                : DoorState.Open;
        }

        private static bool IsLockedState(IReadOnlyDictionary<string, DoorState> states, string socketId) => states.TryGetValue(socketId, out DoorState state) && state == DoorState.Locked;

        private static void SealUnusedSockets(
            IDictionary<string, Dictionary<string, DoorState>> doorStates,
            ISet<string> usedSockets,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections)
        {
            // A socket that is not part of any DoorConnection leads nowhere (empty space / a duplicate seam).
            // It must end Disabled so the player can never open it and escape the maze.
            // FlowDirection.None sockets are authored runtime-only doors, not procedural room connectors, so
            // usable generated states are preserved for them and LevelSpawner can bind an auto-open trigger.
            foreach (KeyValuePair<string, Dictionary<string, DoorState>> kv in doorStates)
            {
                Dictionary<string, DoorState> room = kv.Value;
                List<string> socketIds = new(room.Keys);
                for (int i = 0; i < socketIds.Count; i++)
                {
                    string socketId = socketIds[i];
                    if (!usedSockets.Contains(SocketKey(kv.Key, socketId))
                        && !ShouldPreserveRuntimeOnlySocket(kv.Key, socketId, room[socketId], selections))
                    {
                        room[socketId] = DoorState.Disabled;
                    }
                }
            }
        }

        private static bool ShouldPreserveRuntimeOnlySocket(
            string nodeId,
            string socketId,
            DoorState state,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections)
        {
            if (!IsRuntimeOnlyDoorState(state)
                || selections == null
                || !selections.TryGetValue(nodeId, out IRoomTemplateDescriptor template)
                || template?.Doors == null)
            {
                return false;
            }

            for (int i = 0; i < template.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = template.Doors[i];
                if (door != null
                    && string.Equals(door.SocketId, socketId, StringComparison.Ordinal)
                    && door.FlowDirection() == DoorSocketFlowDirection.None)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRuntimeOnlyDoorState(DoorState state)
        {
            return state == DoorState.Open
                   || state == DoorState.Closed
                   || state == DoorState.ExitGate;
        }

        private static (float x, float z) DoorWorldPoint(PlacedRoom room, IDoorSocketDescriptor door)
        {
            if (!room.WorldPose.HasValue)
            {
                return RoomPlacer.DoorWorldPoint(room.Pose, null, door);
            }

            RoomWorldPose pose = room.WorldPose.Value;
            float rotation = YawDegrees(pose);
            (float x, float z) local = Rotate((door.ConnectionLocalXMeters(), door.ConnectionLocalZMeters()), rotation);
            return (pose.X + local.x, pose.Z + local.z);
        }

        private static float DoorWorldYaw(PlacedRoom room, IDoorSocketDescriptor door)
        {
            float roomYaw = room.WorldPose.HasValue ? YawDegrees(room.WorldPose.Value) : room.Pose.RotationDegrees;
            return NormalizeYaw(roomYaw + door.LocalYawDegrees);
        }

        private static float YawDegrees(RoomWorldPose pose)
        {
            float x = pose.RotationX;
            float y = pose.RotationY;
            float z = pose.RotationZ;
            float w = pose.RotationW;
            float siny = 2f * (w * y + z * x);
            float cosy = 1f - 2f * (y * y + x * x);
            return NormalizeYaw((float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI));
        }

        private static (float x, float z) Rotate((float x, float z) point, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        // The placer snaps/quantizes poses (RoundYaw, GridPose, DoorSnapMeters), so re-confirmed door
        // world points can drift by a fraction of a snap step. Tie the coincidence tolerance to the snap
        // constant instead of a tight literal so FindPair does not drop edges the placer already validated.
        private const float DoorCoincidenceToleranceMeters = LevelGenerationGridMetrics.DoorSnapMeters * 0.25f;

        private static bool Approximately(float a, float b) => Math.Abs(a - b) <= DoorCoincidenceToleranceMeters;

        private static bool Opposes(float aYaw, float bYaw)
        {
            float diff = Math.Abs(NormalizeYaw(aYaw - bYaw));
            if (diff > 180f)
            {
                diff = 360f - diff;
            }

            return Math.Abs(diff - 180f) <= 0.1f;
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }
    }
}
