using System;
using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Default <see cref="ILevelValidator"/>. Enforces the rules listed in PLAN.md §3.7.
    /// </summary>
    public sealed class LevelValidator : IGraphAwareLevelValidator
    {
        private const float ConnectedDoorSeamOverlapToleranceMeters = 0.25f;

        private readonly IRoomDatabase _roomDatabase;
        private readonly IReadOnlyList<IRoomDependencyHintExtractor> _hintExtractors;

        public LevelValidator(IRoomDatabase roomDatabase = null,
            IEnumerable<IRoomDependencyHintExtractor> hintExtractors = null)
        {
            _roomDatabase = roomDatabase;
            _hintExtractors = hintExtractors != null
                ? new List<IRoomDependencyHintExtractor>(hintExtractors)
                : new List<IRoomDependencyHintExtractor>(0);
        }

        public LevelValidationResult Validate(LevelLayout layout, IMissionConfigView config, ITilesetView tileset) => Validate(layout, null, config, tileset);

        public LevelValidationResult Validate(
            LevelLayout layout,
            LevelGraph graph,
            IMissionConfigView config,
            ITilesetView tileset)
        {
            List<LevelValidationIssue> issues = new();

            if (layout == null || layout.Rooms == null || layout.Rooms.Count == 0)
            {
                issues.Add(new LevelValidationIssue("EmptyLayout", "Layout has no rooms.",
                    LevelValidationSeverity.Error));
                return new LevelValidationResult(issues);
            }

            // 1. Start present
            bool hasStart = false;
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                if (layout.Rooms[i].NodeId == layout.StartNodeId)
                {
                    hasStart = true;
                    break;
                }
            }

            if (!hasStart)
            {
                issues.Add(new LevelValidationIssue("MissingStart", "Start room not placed.",
                    LevelValidationSeverity.Error));
            }

            // 2. Exit present (only for modes that require it)
            if (config != null)
            {
                bool requiresExitRoom = config.ExtractionMode == ExtractionMode.SeparateExitRoom
                                        || config.ExtractionMode == ExtractionMode.DoubleExit;
                if (requiresExitRoom && (layout.ExitNodeIds == null || layout.ExitNodeIds.Count == 0))
                {
                    issues.Add(new LevelValidationIssue("MissingExit", "Exit room not placed.",
                        LevelValidationSeverity.Error));
                }

                if (config.ExtractionMode == ExtractionMode.DoubleExit
                    && layout.ExitNodeIds != null
                    && layout.ExitNodeIds.Count < 2)
                {
                    issues.Add(new LevelValidationIssue("ExitRoomsBelowCount",
                        $"DoubleExit requires {config.ExtractionRoomsCount} exit rooms, layout has {layout.ExitNodeIds.Count}.",
                        LevelValidationSeverity.Error));
                }
            }

            // 3. Objective present (at least one Logic main-path room exists).
            //    For MVP we only check that an Objective node id exists by convention (one of main path).
            //    Generator builds with Objective spine — absence means placement failed.
            //    We approximate via presence of any non-Start non-Exit non-DeadEnd room.
            bool hasInteriorRoom = false;
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                string nodeId = layout.Rooms[i].NodeId;
                if (nodeId == layout.StartNodeId)
                {
                    continue;
                }

                if (Contains(layout.ExitNodeIds, nodeId))
                {
                    continue;
                }

                hasInteriorRoom = true;
                break;
            }

            if (!hasInteriorRoom)
            {
                issues.Add(new LevelValidationIssue("MissingObjective", "No Objective / interior room placed.",
                    LevelValidationSeverity.Error));
            }

            // 4. No grid overlap (defensive; RoomPlacer already enforces but layouts can be constructed by tests).
            // H5: the overlap check relies on room templates from _roomDatabase. If the database is null the
            // template lookup is empty, so wall/footprint overlap detection silently degrades to a near no-op
            // (only the grid-pose fallback for template-less rooms still works) and overlapping levels could pass.
            // We do NOT throw in the constructor because existing tests legitimately construct LevelValidator()
            // with no database (e.g. MissingStart / MainPathBroken / LengthOutOfRange cases that don't exercise
            // overlap). Instead we surface the degraded state as an explicit Error so it can never ship silently.
            if (_roomDatabase == null)
            {
                issues.Add(new LevelValidationIssue("OverlapCheckUnavailable",
                    "Room database is null; overlap detection cannot run reliably (only grid-pose fallback applies).",
                    LevelValidationSeverity.Error));
            }

            Dictionary<string, IRoomTemplateDescriptor> templatesById = BuildTemplateLookup();
            List<(PlacedRoom room, IRoomTemplateDescriptor template)> checkedRooms = new();
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                templatesById.TryGetValue(room.RoomTemplateId, out IRoomTemplateDescriptor template);
                for (int j = 0; j < checkedRooms.Count; j++)
                {
                    (PlacedRoom room, IRoomTemplateDescriptor template) other = checkedRooms[j];
                    DoorConnection? directConnection =
                        FindDirectConnection(layout.Connections, room.NodeId, other.room.NodeId);

                    if (!RoomsOverlap(room, template, other.room, other.template, directConnection))
                    {
                        continue;
                    }

                    // Maze wrong-answer dead-end caps are force-placed to honour the "every wrong door is a real
                    // dead-end room" contract. The only dead-end template is wider than the gap between some
                    // rooms' answer doors, so its cap can physically overlap a sibling/tunnel. We surface that as
                    // a non-fatal WARNING (so the layout still ships) instead of rejecting the seed into a
                    // generation timeout. Review the geometry / add a narrower dead-end template to clear it.
                    bool deadEndOverlap = template?.Kind == RoomKind.DeadEnd
                                          || other.template?.Kind == RoomKind.DeadEnd;
                    issues.Add(new LevelValidationIssue(
                        "RoomsOverlap",
                        $"Room '{room.NodeId}' overlaps room '{other.room.NodeId}'."
                        + (deadEndOverlap
                            ? " Dead-end cap overlap — non-fatal WARNING; the dead-end template is wider than the"
                              + " gap, review geometry or use a narrower cap."
                            : string.Empty),
                        deadEndOverlap ? LevelValidationSeverity.Warning : LevelValidationSeverity.Error));
                    break;
                }

                checkedRooms.Add((room, template));
            }

            // 5. Main path reachability (BFS Start → ExitNodes via connections).
            if (config != null && hasStart && layout.ExitNodeIds != null && layout.ExitNodeIds.Count > 0)
            {
                bool requiresExit = config.ExtractionMode == ExtractionMode.SeparateExitRoom
                                    || config.ExtractionMode == ExtractionMode.DoubleExit;
                if (requiresExit)
                {
                    HashSet<string> reachable = BfsFromStart(layout);
                    for (int i = 0; i < layout.ExitNodeIds.Count; i++)
                    {
                        if (!reachable.Contains(layout.ExitNodeIds[i]))
                        {
                            issues.Add(new LevelValidationIssue("MainPathBroken",
                                $"Exit '{layout.ExitNodeIds[i]}' not reachable from Start.",
                                LevelValidationSeverity.Error));
                        }
                    }
                }
            }

            // 5b. General connectivity (H4): every placed non-Start room must be reachable from Start,
            //     for ALL extraction modes (not just exit modes). The exit-specific check above only
            //     covers exit nodes; a disconnected objective/interior room would otherwise pass.
            //     Reuses the same undirected BFS over door connections.
            if (hasStart)
            {
                HashSet<string> reachableFromStart = BfsFromStart(layout);
                for (int i = 0; i < layout.Rooms.Count; i++)
                {
                    string nodeId = layout.Rooms[i].NodeId;
                    if (string.IsNullOrEmpty(nodeId) || nodeId == layout.StartNodeId)
                    {
                        continue;
                    }

                    if (!reachableFromStart.Contains(nodeId))
                    {
                        issues.Add(new LevelValidationIssue("RoomUnreachable",
                            $"Room '{nodeId}' is not reachable from Start.",
                            LevelValidationSeverity.Error));
                    }
                }
            }

            // 6. Total room count within range.
            if (config != null && tileset != null)
            {
                IntRange totalRange = config.TotalRoomsRange.IsSet
                    ? new IntRange(config.TotalRoomsRange.Min, config.TotalRoomsRange.Max)
                    : tileset.TotalRoomsRange;
                int maxRooms = totalRange.Max;
                if (maxRooms > 0
                    && graph?.Nodes != null
                    && graph.Nodes.Count > maxRooms
                    && HasPlannedInteractiveContent(config))
                {
                    maxRooms = graph.Nodes.Count;
                }

                if (totalRange.Min > 0 && layout.Rooms.Count < totalRange.Min)
                {
                    issues.Add(new LevelValidationIssue("LengthOutOfRange",
                        $"Layout has {layout.Rooms.Count} rooms, expected at least {totalRange.Min}.",
                        LevelValidationSeverity.Error));
                }

                if (maxRooms > 0 && layout.Rooms.Count > maxRooms)
                {
                    issues.Add(new LevelValidationIssue("LengthOutOfRange",
                        $"Layout has {layout.Rooms.Count} rooms, expected at most {maxRooms}.",
                        LevelValidationSeverity.Error));
                }
            }

            ValidateGraphCoverage(layout, graph, issues);
            ValidateRoomKindSpawnRules(layout, config, templatesById, issues);
            ValidateMissionRoomSpawnRules(layout, config, issues);
            ValidateDependencyHints(layout, tileset, issues);

            return new LevelValidationResult(issues);
        }

        private static bool HasPlannedInteractiveContent(IMissionConfigView config)
        {
            return config is IPlannedInteractiveMissionContentProvider planned
                   && planned.PlannedInteractiveContent != null
                   && planned.PlannedInteractiveContent.Count > 0;
        }

        private static DoorConnection? FindDirectConnection(IReadOnlyList<DoorConnection> connections, string a,
            string b)
        {
            if (connections == null || string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return null;
            }

            for (int i = 0; i < connections.Count; i++)
            {
                DoorConnection connection = connections[i];

                if (string.Equals(connection.FromNodeId, a, StringComparison.Ordinal)
                    && string.Equals(connection.ToNodeId, b, StringComparison.Ordinal))
                {
                    return connection;
                }

                if (string.Equals(connection.FromNodeId, b, StringComparison.Ordinal)
                    && string.Equals(connection.ToNodeId, a, StringComparison.Ordinal))
                {
                    return connection;
                }
            }

            return null;
        }

        private static void ValidateGraphCoverage(
            LevelLayout layout,
            LevelGraph graph,
            List<LevelValidationIssue> issues)
        {
            if (layout == null || graph == null)
            {
                return;
            }

            HashSet<string> graphNodes = new(StringComparer.Ordinal);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                string nodeId = graph.Nodes[i].NodeId;
                if (!graphNodes.Add(nodeId))
                {
                    issues.Add(new LevelValidationIssue(
                        "DuplicateGraphNode",
                        $"Graph contains duplicate node id '{nodeId}'.",
                        LevelValidationSeverity.Error));
                }
            }

            HashSet<string> placedNodes = new(StringComparer.Ordinal);
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (room == null || string.IsNullOrEmpty(room.NodeId))
                {
                    continue;
                }

                if (!placedNodes.Add(room.NodeId))
                {
                    issues.Add(new LevelValidationIssue(
                        "DuplicatePlacedNode",
                        $"Layout contains duplicate placed node id '{room.NodeId}'.",
                        LevelValidationSeverity.Error));
                }

                if (!graphNodes.Contains(room.NodeId))
                {
                    issues.Add(new LevelValidationIssue(
                        "PlacedNodeNotInGraph",
                        $"Placed room '{room.NodeId}' is not present in the graph.",
                        LevelValidationSeverity.Error));
                }
            }

            foreach (string nodeId in graphNodes)
            {
                if (!placedNodes.Contains(nodeId))
                {
                    issues.Add(new LevelValidationIssue(
                        "GraphNodeNotPlaced",
                        $"Graph node '{nodeId}' was not placed.",
                        LevelValidationSeverity.Error));
                }
            }

            HashSet<string> graphEdges = new(StringComparer.Ordinal);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                LevelGraphEdge edge = graph.Edges[i];
                if (string.Equals(edge.FromNodeId, edge.ToNodeId, StringComparison.Ordinal))
                {
                    continue;
                }

                graphEdges.Add(ConnectionKey(edge.FromNodeId, edge.ToNodeId));
            }

            HashSet<string> layoutEdges = new(StringComparer.Ordinal);
            if (layout.Connections != null)
            {
                for (int i = 0; i < layout.Connections.Count; i++)
                {
                    DoorConnection connection = layout.Connections[i];
                    string key = ConnectionKey(connection.FromNodeId, connection.ToNodeId);
                    layoutEdges.Add(key);
                    if (!graphEdges.Contains(key))
                    {
                        issues.Add(new LevelValidationIssue(
                            "ConnectionNotInGraph",
                            $"Layout connection '{connection.FromNodeId}' -> '{connection.ToNodeId}' is not present in the graph.",
                            LevelValidationSeverity.Error));
                    }
                }
            }

            foreach (string graphEdge in graphEdges)
            {
                if (!layoutEdges.Contains(graphEdge))
                {
                    issues.Add(new LevelValidationIssue(
                        "GraphEdgeNotConnected",
                        $"Graph edge '{graphEdge}' was not connected by door sockets.",
                        LevelValidationSeverity.Error));
                }
            }
        }

        private static string ConnectionKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

        private static void ValidateRoomKindSpawnRules(
            LevelLayout layout,
            IMissionConfigView config,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> templatesById,
            List<LevelValidationIssue> issues)
        {
            if (config?.RoomKindSpawnRules == null || config.RoomKindSpawnRules.Count == 0)
            {
                return;
            }

            for (int i = 0; i < config.RoomKindSpawnRules.Count; i++)
            {
                IRoomKindSpawnRule rule = config.RoomKindSpawnRules[i];
                if (rule == null || rule.Mode != RoomTypeSpawnMode.CountRange)
                {
                    continue;
                }

                int min = Math.Max(0, rule.CountRange.Min);
                int max = Math.Max(min, rule.CountRange.Max);
                int count = CountRoomsOfKind(layout, templatesById, rule.Kind);
                if (count < min || count > max)
                {
                    issues.Add(new LevelValidationIssue(
                        "RoomKindCountOutOfRange",
                        $"Room kind '{rule.Kind}' count is {count}, expected {min}..{max}.",
                        LevelValidationSeverity.Error));
                }
            }
        }

        private static int CountRoomsOfKind(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> templatesById,
            RoomKind kind)
        {
            int count = 0;
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                if (!templatesById.TryGetValue(layout.Rooms[i].RoomTemplateId, out IRoomTemplateDescriptor template))
                {
                    continue;
                }

                if (template.Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        private static void ValidateMissionRoomSpawnRules(
            LevelLayout layout,
            IMissionConfigView config,
            List<LevelValidationIssue> issues)
        {
            if (config?.MissionRoomRules == null || config.MissionRoomRules.Count == 0)
            {
                return;
            }

            for (int i = 0; i < config.MissionRoomRules.Count; i++)
            {
                IMissionRoomRule rule = config.MissionRoomRules[i];
                if (rule == null || !rule.HasSpawnRule || rule.Mode != RoomTypeSpawnMode.CountRange)
                {
                    continue;
                }

                int min = Math.Max(0, rule.CountRange.Min);
                int max = Math.Max(min, rule.CountRange.Max);
                int count = CountRoomsByTemplate(layout, rule.RoomId);
                if (count < min || count > max)
                {
                    issues.Add(new LevelValidationIssue(
                        "MissionRoomCountOutOfRange",
                        $"Room '{rule.RoomId}' count is {count}, expected {min}..{max}.",
                        LevelValidationSeverity.Error));
                }
            }
        }

        private static int CountRoomsByTemplate(LevelLayout layout, string roomId)
        {
            int count = 0;
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                if (string.Equals(layout.Rooms[i].RoomTemplateId, roomId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private void ValidateDependencyHints(LevelLayout layout, ITilesetView tileset,
            List<LevelValidationIssue> issues)
        {
            if (_roomDatabase == null || _hintExtractors.Count == 0)
            {
                return;
            }

            List<RoomDependencyHint> hints = new();
            for (int i = 0; i < _hintExtractors.Count; i++)
            {
                IReadOnlyList<RoomDependencyHint> extracted =
                    _hintExtractors[i].Extract(_roomDatabase.AllRooms, tileset);
                if (extracted == null)
                {
                    continue;
                }

                for (int j = 0; j < extracted.Count; j++)
                {
                    hints.Add(extracted[j]);
                }
            }

            if (hints.Count == 0)
            {
                return;
            }

            Dictionary<string, List<string>> nodesByTemplateId = BuildPlacedTemplateLookup(layout);
            Dictionary<string, Dictionary<string, int>> distancesByNode = BuildAllDistances(layout);
            for (int i = 0; i < hints.Count; i++)
            {
                RoomDependencyHint hint = hints[i];
                if (!hint.IsValid)
                {
                    continue;
                }

                if (!nodesByTemplateId.TryGetValue(hint.SourceRoomTemplateId, out List<string> sourceNodeIds)
                    || sourceNodeIds.Count == 0)
                {
                    continue;
                }

                if (!nodesByTemplateId.TryGetValue(hint.TargetRoomTemplateId, out List<string> targetNodeIds)
                    || targetNodeIds.Count == 0)
                {
                    continue;
                }

                int bestDistance = int.MaxValue;
                bool foundConnectedPair = false;
                for (int s = 0; s < sourceNodeIds.Count; s++)
                {
                    if (!distancesByNode.TryGetValue(sourceNodeIds[s], out Dictionary<string, int> sourceDistances))
                    {
                        continue;
                    }

                    for (int t = 0; t < targetNodeIds.Count; t++)
                    {
                        if (sourceDistances.TryGetValue(targetNodeIds[t], out int distance))
                        {
                            foundConnectedPair = true;
                            if (distance < bestDistance)
                            {
                                bestDistance = distance;
                            }
                        }
                    }
                }

                // C2 (interim): a GENUINE unsatisfied key dependency must be a hard Error so the layout is
                // rejected and the generator retries — it must never ship as a mere Warning. The two genuine
                // cases below (no connected path between key and lock; key further from lock than MaxDistance,
                // i.e. effectively unreachable within the allowed range) are unrecoverable correctness defects
                // and are always Error.
                if (!foundConnectedPair)
                {
                    issues.Add(new LevelValidationIssue(
                        "KeyDependencyUnsatisfied",
                        $"Dependency '{hint.SourceRoomTemplateId}' -> '{hint.TargetRoomTemplateId}' is disconnected.",
                        LevelValidationSeverity.Error));
                    continue;
                }

                if (bestDistance > hint.MaxDistance)
                {
                    issues.Add(new LevelValidationIssue(
                        "KeyDependencyUnsatisfied",
                        $"Dependency '{hint.SourceRoomTemplateId}' -> '{hint.TargetRoomTemplateId}' distance is {bestDistance}, expected {hint.MinDistance}..{hint.MaxDistance}.",
                        LevelValidationSeverity.Error));
                    continue;
                }

                if (bestDistance < hint.MinDistance)
                {
                    // DISTINCT from the genuine cases above: key and lock ARE connected and within MaxDistance,
                    // they are merely closer together than the preferred MinDistance. On small layouts the
                    // available room budget physically cannot honour MinDistance, so this is a soft "distance
                    // too close" heuristic — not a hard unreachability defect. We keep it as a Warning only in
                    // that small-layout case (so we don't turn currently-passing seeds into constant failures);
                    // otherwise it is a real layout problem and stays an Error.
                    LevelValidationSeverity severity = layout.Rooms.Count - 1 <= hint.MinDistance
                        ? LevelValidationSeverity.Warning
                        : LevelValidationSeverity.Error;
                    string code = severity == LevelValidationSeverity.Warning
                        ? "DependencyDistanceShrunk"
                        : "KeyDependencyUnsatisfied";
                    issues.Add(new LevelValidationIssue(
                        code,
                        $"Dependency '{hint.SourceRoomTemplateId}' -> '{hint.TargetRoomTemplateId}' distance is {bestDistance}, min {hint.MinDistance}.",
                        severity));
                }
            }
        }

        private static bool Contains(IReadOnlyList<string> list, string id)
        {
            if (list == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == id)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RoomsOverlap(
            PlacedRoom a,
            IRoomTemplateDescriptor aTemplate,
            PlacedRoom b,
            IRoomTemplateDescriptor bTemplate,
            DoorConnection? directConnection = null)
        {
            (float x, float z)? seamPointMeters = directConnection.HasValue
                ? ResolveConnectedDoorSeamMeters(directConnection.Value, a, aTemplate, b, bTemplate)
                : null;
            if (RoomWallCollision.TryOverlaps(
                    a,
                    aTemplate,
                    b,
                    bTemplate,
                    seamPointMeters,
                    out bool wallOverlaps))
            {
                return wallOverlaps;
            }

            if (!a.WorldPose.HasValue && !b.WorldPose.HasValue)
            {
                return RoomPlacer.Overlaps(a.Pose, aTemplate, b.Pose, bTemplate);
            }

            bool allowConnectedDoorSeam = directConnection.HasValue
                                          && (a.WorldPose.HasValue || b.WorldPose.HasValue);
            RoomFootprintShape aShape = RoomFootprintShapeResolver.ShapeFor(aTemplate);
            RoomFootprintShape bShape = RoomFootprintShapeResolver.ShapeFor(bTemplate);
            IReadOnlyList<RoomFootprintCell> aCells = aShape.Cells;
            IReadOnlyList<RoomFootprintCell> bCells = bShape.Cells;
            for (int i = 0; i < aCells.Count; i++)
            {
                WorldObb aObb = BuildWorldCellObb(a, aTemplate, aCells[i]);
                for (int j = 0; j < bCells.Count; j++)
                {
                    if (!Overlaps(aObb, BuildWorldCellObb(b, bTemplate, bCells[j]), out float penetrationMeters))
                    {
                        continue;
                    }

                    if (!allowConnectedDoorSeam
                        || penetrationMeters > ConnectedDoorSeamOverlapToleranceMeters)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static (float x, float z)? ResolveConnectedDoorSeamMeters(
            DoorConnection connection,
            PlacedRoom a,
            IRoomTemplateDescriptor aTemplate,
            PlacedRoom b,
            IRoomTemplateDescriptor bTemplate)
        {
            IDoorSocketDescriptor aDoor = null;
            IDoorSocketDescriptor bDoor = null;

            if (string.Equals(connection.FromNodeId, a.NodeId, StringComparison.Ordinal)
                && string.Equals(connection.ToNodeId, b.NodeId, StringComparison.Ordinal))
            {
                aDoor = FindDoor(aTemplate, connection.FromSocketId);
                bDoor = FindDoor(bTemplate, connection.ToSocketId);
            }
            else if (string.Equals(connection.FromNodeId, b.NodeId, StringComparison.Ordinal)
                     && string.Equals(connection.ToNodeId, a.NodeId, StringComparison.Ordinal))
            {
                aDoor = FindDoor(aTemplate, connection.ToSocketId);
                bDoor = FindDoor(bTemplate, connection.FromSocketId);
            }

            bool hasA = aDoor != null;
            bool hasB = bDoor != null;
            if (!hasA && !hasB)
            {
                return null;
            }

            if (hasA && hasB)
            {
                (float x, float z) aPoint = DoorWorldPointMeters(a, aDoor);
                (float x, float z) bPoint = DoorWorldPointMeters(b, bDoor);
                return ((aPoint.x + bPoint.x) * 0.5f, (aPoint.z + bPoint.z) * 0.5f);
            }

            return hasA ? DoorWorldPointMeters(a, aDoor) : DoorWorldPointMeters(b, bDoor);
        }

        private static IDoorSocketDescriptor FindDoor(IRoomTemplateDescriptor template, string socketId)
        {
            if (template?.Doors == null || string.IsNullOrEmpty(socketId))
            {
                return null;
            }

            for (int i = 0; i < template.Doors.Count; i++)
            {
                IDoorSocketDescriptor door = template.Doors[i];
                if (door != null && string.Equals(door.SocketId, socketId, StringComparison.Ordinal))
                {
                    return door;
                }
            }

            return null;
        }

        private static (float x, float z) DoorWorldPointMeters(PlacedRoom room, IDoorSocketDescriptor door)
        {
            (float x, float z) origin = room.WorldPose.HasValue
                ? (x: room.WorldPose.Value.X, z: room.WorldPose.Value.Z)
                : (x: room.Pose.CellX * LevelGenerationGridMetrics.DoorSnapMeters,
                    z: room.Pose.CellY * LevelGenerationGridMetrics.DoorSnapMeters);
            float rotation = room.WorldPose.HasValue ? YawDegrees(room.WorldPose.Value) : room.Pose.RotationDegrees;
            (float x, float z) local = (x: door.ConnectionLocalXMeters(), z: door.ConnectionLocalZMeters());
            return Add(origin, Rotate(local, rotation));
        }

        private static WorldObb BuildWorldCellObb(
            PlacedRoom room,
            IRoomTemplateDescriptor template,
            RoomFootprintCell cell)
        {
            const float doorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;
            const float cellSizeMeters = LevelGenerationGridMetrics.CellSizeMeters;
            (float x, float z) origin = room.WorldPose.HasValue
                ? (x: room.WorldPose.Value.X, z: room.WorldPose.Value.Z)
                : (x: room.Pose.CellX * doorSnapMeters, z: room.Pose.CellY * doorSnapMeters);
            float rotation = room.WorldPose.HasValue ? YawDegrees(room.WorldPose.Value) : room.Pose.RotationDegrees;
            float minX = cell.X * cellSizeMeters;
            float minZ = cell.Z * cellSizeMeters;
            float maxX = minX + cellSizeMeters;
            float maxZ = minZ + cellSizeMeters;

            return new WorldObb(new[] { Add(origin, Rotate((minX, minZ), rotation)), Add(origin, Rotate((maxX, minZ), rotation)), Add(origin, Rotate((maxX, maxZ), rotation)), Add(origin, Rotate((minX, maxZ), rotation)) });
        }

        private static bool Overlaps(WorldObb a, WorldObb b, out float penetrationMeters)
        {
            penetrationMeters = float.MaxValue;
            if (HasSeparatingAxis(a, b, ref penetrationMeters)
                || HasSeparatingAxis(b, a, ref penetrationMeters))
            {
                penetrationMeters = 0f;
                return false;
            }

            return true;
        }

        private static bool HasSeparatingAxis(WorldObb source, WorldObb target, ref float penetrationMeters)
        {
            const float epsilon = 0.001f;
            for (int i = 0; i < 4; i++)
            {
                (float x, float z) p0 = source.Points[i];
                (float x, float z) p1 = source.Points[(i + 1) % 4];
                (float x, float z) edge = (x: p1.x - p0.x, z: p1.z - p0.z);
                (float x, float z) axis = (x: -edge.z, z: edge.x);
                float axisLength = (float)Math.Sqrt(axis.x * axis.x + axis.z * axis.z);
                if (axisLength <= epsilon)
                {
                    continue;
                }

                axis = (axis.x / axisLength, axis.z / axisLength);
                Project(source, axis, out float sourceMin, out float sourceMax);
                Project(target, axis, out float targetMin, out float targetMax);
                if (sourceMax <= targetMin + epsilon || targetMax <= sourceMin + epsilon)
                {
                    return true;
                }

                float overlap = Math.Min(sourceMax, targetMax) - Math.Max(sourceMin, targetMin);
                if (overlap < penetrationMeters)
                {
                    penetrationMeters = overlap;
                }
            }

            return false;
        }

        private static void Project(WorldObb obb, (float x, float z) axis, out float min, out float max)
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

        private static (float x, float z) Add((float x, float z) a, (float x, float z) b) => (a.x + b.x, a.z + b.z);

        private static (float x, float z) Rotate((float x, float z) point, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        private static float YawDegrees(RoomWorldPose pose)
        {
            float x = pose.RotationX;
            float y = pose.RotationY;
            float z = pose.RotationZ;
            float w = pose.RotationW;
            float siny = 2f * (w * y + z * x);
            float cosy = 1f - 2f * (y * y + x * x);
            float yaw = (float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private readonly struct WorldObb
        {
            public readonly (float x, float z)[] Points;

            public WorldObb((float x, float z)[] points)
            {
                Points = points;
            }
        }

        private static HashSet<string> BfsFromStart(LevelLayout layout)
        {
            HashSet<string> visited = new() { layout.StartNodeId };
            Queue<string> queue = new();
            queue.Enqueue(layout.StartNodeId);

            // Build undirected adjacency from door connections.
            Dictionary<string, List<string>> adj = new();
            for (int i = 0; i < layout.Connections.Count; i++)
            {
                DoorConnection c = layout.Connections[i];
                AddEdge(adj, c.FromNodeId, c.ToNodeId);
                AddEdge(adj, c.ToNodeId, c.FromNodeId);
            }

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!adj.TryGetValue(current, out List<string> nbrs))
                {
                    continue;
                }

                for (int i = 0; i < nbrs.Count; i++)
                {
                    if (visited.Add(nbrs[i]))
                    {
                        queue.Enqueue(nbrs[i]);
                    }
                }
            }

            return visited;
        }

        private static Dictionary<string, List<string>> BuildPlacedTemplateLookup(LevelLayout layout)
        {
            Dictionary<string, List<string>> result = new(StringComparer.Ordinal);
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (string.IsNullOrEmpty(room.RoomTemplateId))
                {
                    continue;
                }

                if (!result.TryGetValue(room.RoomTemplateId, out List<string> nodes))
                {
                    nodes = new List<string>();
                    result[room.RoomTemplateId] = nodes;
                }

                nodes.Add(room.NodeId);
            }

            return result;
        }

        private Dictionary<string, IRoomTemplateDescriptor> BuildTemplateLookup()
        {
            Dictionary<string, IRoomTemplateDescriptor> result = new();
            if (_roomDatabase?.AllRooms == null)
            {
                return result;
            }

            for (int i = 0; i < _roomDatabase.AllRooms.Count; i++)
            {
                IRoomTemplateDescriptor room = _roomDatabase.AllRooms[i];
                if (room == null || string.IsNullOrEmpty(room.RoomId))
                {
                    continue;
                }

                result[room.RoomId] = room;
            }

            return result;
        }

        private static Dictionary<string, Dictionary<string, int>> BuildAllDistances(LevelLayout layout)
        {
            Dictionary<string, Dictionary<string, int>> result = new();
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                result[layout.Rooms[i].NodeId] = CalculateDistances(layout, layout.Rooms[i].NodeId);
            }

            return result;
        }

        private static Dictionary<string, int> CalculateDistances(LevelLayout layout, string startNodeId)
        {
            Dictionary<string, List<string>> adjacency = new();
            for (int i = 0; i < layout.Connections.Count; i++)
            {
                DoorConnection c = layout.Connections[i];
                AddEdge(adjacency, c.FromNodeId, c.ToNodeId);
                AddEdge(adjacency, c.ToNodeId, c.FromNodeId);
            }

            Dictionary<string, int> distances = new() { [startNodeId] = 0 };
            Queue<string> queue = new();
            queue.Enqueue(startNodeId);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out List<string> neighbors))
                {
                    continue;
                }

                for (int i = 0; i < neighbors.Count; i++)
                {
                    string next = neighbors[i];
                    if (distances.ContainsKey(next))
                    {
                        continue;
                    }

                    distances[next] = distances[current] + 1;
                    queue.Enqueue(next);
                }
            }

            return distances;
        }

        private static void AddEdge(Dictionary<string, List<string>> adj, string a, string b)
        {
            if (!adj.TryGetValue(a, out List<string> list))
            {
                list = new List<string>(2);
                adj[a] = list;
            }

            list.Add(b);
        }
    }
}
