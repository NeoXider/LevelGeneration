using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application.DependencyHints;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Default <see cref="ILevelValidator"/>. Enforces the rules listed in PLAN.md §3.7.
    /// </summary>
    public sealed class LevelValidator : IGraphAwareLevelValidator
    {
        private const float ConnectedDoorSeamOverlapToleranceMeters = 0.25f;

        // Запас broadphase-отсечения пар комнат — тот же, что у размещения (SocketBacktrackingRoomPlacer).
        private const float OverlapBroadphaseMarginMeters = LevelGenerationGridMetrics.DoorSnapMeters;

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
            ValidateDoorSocketIds(layout, templatesById, issues);
            ValidateOverlaps(layout, templatesById, issues);

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
            ValidateDependencyHints(layout, tileset, hasStart, issues);

            return new LevelValidationResult(issues);
        }

        private static void ValidateDoorSocketIds(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> templatesById,
            List<LevelValidationIssue> issues)
        {
            HashSet<string> checkedTemplates = new(StringComparer.Ordinal);
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                if (room == null
                    || !templatesById.TryGetValue(room.RoomTemplateId, out IRoomTemplateDescriptor template)
                    || template == null
                    || !checkedTemplates.Add(room.RoomTemplateId))
                {
                    continue;
                }

                if (DoorSocketIdRules.TryFindDuplicateConnectableSocketId(template, out string socketId))
                {
                    issues.Add(DoorSocketIdRules.CreateDuplicateIssue(template.RoomId, socketId));
                }
            }
        }

        // Все пары комнат проверяются до конца, без выхода на первом пересечении: иначе безобидное предупреждение
        // о двух тупиковых заглушках прятало бы настоящую ошибку пересечения той же комнаты с другой.
        private static void ValidateOverlaps(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> templatesById,
            List<LevelValidationIssue> issues)
        {
            int count = layout.Rooms.Count;
            RoomOverlapShape[] shapes = new RoomOverlapShape[count];
            for (int i = 0; i < count; i++)
            {
                PlacedRoom room = layout.Rooms[i];
                templatesById.TryGetValue(room.RoomTemplateId, out IRoomTemplateDescriptor template);
                shapes[i] = RoomOverlapShape.Create(room, template);
            }

            Dictionary<(string, string), DoorConnection> connectionsByPair = BuildConnectionLookup(layout.Connections);
            for (int i = 0; i < count; i++)
            {
                RoomOverlapShape room = shapes[i];
                for (int j = 0; j < i; j++)
                {
                    RoomOverlapShape other = shapes[j];

                    // Broadphase: стены, чьи AABB не сходятся даже с запасом, пересечься не могут — точная SAT-проверка
                    // для них дала бы тот же «нет». Запас как у размещения, поэтому отсечение ничего не прячет.
                    if (room.HasWalls
                        && other.HasWalls
                        && !room.Bounds.Overlaps(other.Bounds, OverlapBroadphaseMarginMeters))
                    {
                        continue;
                    }

                    DoorConnection? directConnection =
                        FindDirectConnection(connectionsByPair, room.Room.NodeId, other.Room.NodeId);
                    if (!RoomsOverlap(room, other, directConnection))
                    {
                        continue;
                    }

                    // Пересечение с основным путём или с любой не-тупиковой комнатой — всегда ошибка: сид отклоняется.
                    // Предупреждением остаётся только наложение двух тупиковых заглушек (обе шаблона DeadEnd).
                    // Генератор такие раскладки больше не строит: заглушки неверных ответов не ставятся с наложением.
                    bool deadEndCapsOnly = room.Template?.Kind == RoomKind.DeadEnd
                                           && other.Template?.Kind == RoomKind.DeadEnd;
                    issues.Add(new LevelValidationIssue(
                        "RoomsOverlap",
                        $"Room '{room.Room.NodeId}' overlaps room '{other.Room.NodeId}'."
                        + (deadEndCapsOnly
                            ? " Both are dead-end caps — non-fatal WARNING; review geometry or use a narrower cap."
                            : string.Empty),
                        deadEndCapsOnly ? LevelValidationSeverity.Warning : LevelValidationSeverity.Error));
                }
            }
        }

        private static Dictionary<(string, string), DoorConnection> BuildConnectionLookup(
            IReadOnlyList<DoorConnection> connections)
        {
            Dictionary<(string, string), DoorConnection> result = new();
            if (connections == null)
            {
                return result;
            }

            for (int i = 0; i < connections.Count; i++)
            {
                DoorConnection connection = connections[i];
                if (string.IsNullOrEmpty(connection.FromNodeId) || string.IsNullOrEmpty(connection.ToNodeId))
                {
                    continue;
                }

                // Первое соединение пары выигрывает — как прежний линейный поиск по списку.
                (string, string) key = PairKey(connection.FromNodeId, connection.ToNodeId);
                if (!result.ContainsKey(key))
                {
                    result[key] = connection;
                }
            }

            return result;
        }

        private static (string, string) PairKey(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

        private static bool HasPlannedInteractiveContent(IMissionConfigView config)
        {
            return config is IPlannedInteractiveMissionContentProvider planned
                   && planned.PlannedInteractiveContent != null
                   && planned.PlannedInteractiveContent.Count > 0;
        }

        private static DoorConnection? FindDirectConnection(
            IReadOnlyDictionary<(string, string), DoorConnection> connectionsByPair,
            string a,
            string b)
        {
            if (connectionsByPair == null || string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return null;
            }

            return connectionsByPair.TryGetValue(PairKey(a, b), out DoorConnection connection)
                ? connection
                : (DoorConnection?)null;
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

        private void ValidateDependencyHints(LevelLayout layout, ITilesetView tileset, bool hasStart,
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
            Dictionary<string, List<string>> adjacency =
                KeyLockReachability.BuildAdjacency(EnumerateConnectionLinks(layout.Connections));
            Dictionary<string, Dictionary<string, int>> distancesByLock = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> reachableBeforeLock = new(StringComparer.Ordinal);
            for (int i = 0; i < hints.Count; i++)
            {
                RoomDependencyHint hint = hints[i];
                if (!hint.IsValid)
                {
                    continue;
                }

                if (!nodesByTemplateId.TryGetValue(hint.TargetRoomTemplateId, out List<string> targetNodeIds)
                    || targetNodeIds.Count == 0)
                {
                    continue;
                }

                // Замок поставлен, а ключа нет вовсе — дверь не откроется никогда. Раньше такая раскладка проходила
                // (проверка молча пропускала подсказку), теперь это ошибка для каждого замка.
                if (!nodesByTemplateId.TryGetValue(hint.SourceRoomTemplateId, out List<string> sourceNodeIds)
                    || sourceNodeIds.Count == 0)
                {
                    for (int t = 0; t < targetNodeIds.Count; t++)
                    {
                        issues.Add(new LevelValidationIssue(
                            "KeyDependencyUnsatisfied",
                            $"Lock room '{targetNodeIds[t]}' ('{hint.TargetRoomTemplateId}') is placed, but no key room '{hint.SourceRoomTemplateId}' is placed.",
                            LevelValidationSeverity.Error));
                    }

                    continue;
                }

                // Каждый замок проверяется отдельно: ключ годится ему, только если до ключа можно дойти от старта,
                // не входя в этот замок (тот же обход, что выбирает узел под ключ в генераторе).
                for (int t = 0; t < targetNodeIds.Count; t++)
                {
                    ValidateKeyForLock(
                        layout,
                        hint,
                        sourceNodeIds,
                        targetNodeIds[t],
                        hasStart,
                        adjacency,
                        distancesByLock,
                        reachableBeforeLock,
                        issues);
                }
            }
        }

        private static void ValidateKeyForLock(
            LevelLayout layout,
            RoomDependencyHint hint,
            IReadOnlyList<string> sourceNodeIds,
            string lockNodeId,
            bool hasStart,
            IReadOnlyDictionary<string, List<string>> adjacency,
            Dictionary<string, Dictionary<string, int>> distancesByLock,
            Dictionary<string, HashSet<string>> reachableBeforeLock,
            List<LevelValidationIssue> issues)
        {
            if (!distancesByLock.TryGetValue(lockNodeId, out Dictionary<string, int> fromLock))
            {
                fromLock = KeyLockReachability.Distances(lockNodeId, adjacency);
                distancesByLock[lockNodeId] = fromLock;
            }

            HashSet<string> beforeLock = null;
            if (hasStart && !reachableBeforeLock.TryGetValue(lockNodeId, out beforeLock))
            {
                beforeLock = KeyLockReachability.CollectReachableBeforeLock(layout.StartNodeId, lockNodeId, adjacency);
                reachableBeforeLock[lockNodeId] = beforeLock;
            }

            int bestDistance = int.MaxValue;
            bool foundConnectedKey = false;
            bool foundKeyBeforeLock = false;
            for (int s = 0; s < sourceNodeIds.Count; s++)
            {
                string keyNodeId = sourceNodeIds[s];
                if (string.Equals(keyNodeId, lockNodeId, StringComparison.Ordinal)
                    || !fromLock.TryGetValue(keyNodeId, out int distance))
                {
                    continue;
                }

                foundConnectedKey = true;

                // Без старта (MissingStart уже Error) достижимость «до замка» не определена — проверяем только связность.
                if (beforeLock != null && !beforeLock.Contains(keyNodeId))
                {
                    continue;
                }

                foundKeyBeforeLock = true;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                }
            }

            // C2: a GENUINE unsatisfied key dependency must be a hard Error so the layout is rejected and the
            // generator retries — it must never ship as a mere Warning. Genuine cases: no connected path between
            // key and lock; the key is reachable only THROUGH its own lock; the key is further from the lock
            // than MaxDistance.
            if (!foundConnectedKey)
            {
                issues.Add(new LevelValidationIssue(
                    "KeyDependencyUnsatisfied",
                    $"Dependency '{hint.SourceRoomTemplateId}' -> '{hint.TargetRoomTemplateId}' is disconnected (lock room '{lockNodeId}').",
                    LevelValidationSeverity.Error));
                return;
            }

            if (!foundKeyBeforeLock)
            {
                issues.Add(new LevelValidationIssue(
                    "KeyDependencyUnsatisfied",
                    $"Dependency '{hint.SourceRoomTemplateId}' -> '{hint.TargetRoomTemplateId}': every key room is reachable from Start only through its lock room '{lockNodeId}' (key behind its own lock).",
                    LevelValidationSeverity.Error));
                return;
            }

            if (bestDistance > hint.MaxDistance)
            {
                issues.Add(new LevelValidationIssue(
                    "KeyDependencyUnsatisfied",
                    $"Dependency '{hint.SourceRoomTemplateId}' -> '{hint.TargetRoomTemplateId}' distance is {bestDistance}, expected {hint.MinDistance}..{hint.MaxDistance} (lock room '{lockNodeId}').",
                    LevelValidationSeverity.Error));
                return;
            }

            if (bestDistance < hint.MinDistance)
            {
                // DISTINCT from the genuine cases above: key and lock ARE connected, the key is before the lock and
                // within MaxDistance, they are merely closer together than the preferred MinDistance. On small
                // layouts the available room budget physically cannot honour MinDistance, so this is a soft
                // "distance too close" heuristic. We keep it as a Warning only in that small-layout case;
                // otherwise it is a real layout problem and stays an Error.
                LevelValidationSeverity severity = layout.Rooms.Count - 1 <= hint.MinDistance
                    ? LevelValidationSeverity.Warning
                    : LevelValidationSeverity.Error;
                string code = severity == LevelValidationSeverity.Warning
                    ? "DependencyDistanceShrunk"
                    : "KeyDependencyUnsatisfied";
                issues.Add(new LevelValidationIssue(
                    code,
                    $"Dependency '{hint.SourceRoomTemplateId}' -> '{hint.TargetRoomTemplateId}' distance is {bestDistance}, min {hint.MinDistance} (lock room '{lockNodeId}').",
                    severity));
            }
        }

        private static IEnumerable<(string a, string b)> EnumerateConnectionLinks(IReadOnlyList<DoorConnection> connections)
        {
            if (connections == null)
            {
                yield break;
            }

            for (int i = 0; i < connections.Count; i++)
            {
                yield return (connections[i].FromNodeId, connections[i].ToNodeId);
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
            in RoomOverlapShape aRoom,
            in RoomOverlapShape bRoom,
            DoorConnection? directConnection)
        {
            PlacedRoom a = aRoom.Room;
            IRoomTemplateDescriptor aTemplate = aRoom.Template;
            PlacedRoom b = bRoom.Room;
            IRoomTemplateDescriptor bTemplate = bRoom.Template;
            (float x, float z)? seamPointMeters = directConnection.HasValue
                ? ResolveConnectedDoorSeamMeters(directConnection.Value, a, aTemplate, b, bTemplate)
                : null;

            // Стены каждой комнаты трансформированы один раз (RoomOverlapShape), а не заново на каждую пару.
            if (aRoom.HasWalls
                && bRoom.HasWalls
                && RoomWallCollision.TryOverlaps(aRoom.Walls, bRoom.Walls, seamPointMeters, out bool wallOverlaps))
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

        // Комната для проверки пересечений: стены трансформированы один раз на валидацию, AABB — для broadphase.
        private readonly struct RoomOverlapShape
        {
            public readonly PlacedRoom Room;
            public readonly IRoomTemplateDescriptor Template;
            public readonly bool HasWalls;
            public readonly RoomWallCollision.TransformedWallSegments Walls;
            public readonly RoomWallCollision.Aabb Bounds;

            private RoomOverlapShape(
                PlacedRoom room,
                IRoomTemplateDescriptor template,
                bool hasWalls,
                RoomWallCollision.TransformedWallSegments walls,
                RoomWallCollision.Aabb bounds)
            {
                Room = room;
                Template = template;
                HasWalls = hasWalls;
                Walls = walls;
                Bounds = bounds;
            }

            public static RoomOverlapShape Create(PlacedRoom room, IRoomTemplateDescriptor template)
            {
                if (room != null
                    && RoomWallCollision.TryTransform(room, template,
                        out RoomWallCollision.TransformedWallSegments walls)
                    && walls.HasSegments)
                {
                    return new RoomOverlapShape(room, template, true, walls, RoomWallCollision.BoundsOf(walls));
                }

                return new RoomOverlapShape(room, template, false, default, RoomWallCollision.Aabb.Empty);
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

    /// <summary>
    /// Правило уникальности id дверных сокетов комнаты. Состояние двери адресуется по <c>SocketId</c>
    /// (<see cref="PlacedRoom.DoorStates"/>, занятость сокетов при размещении, поиск двери в <see cref="DoorConnector"/>),
    /// поэтому две двери с одним id молча делят одно состояние: соединение одной «открывает» другую, запечатывание
    /// одной запечатывает обе. Шаблон с таким дефектом отклоняется явной ошибкой с именем комнаты и сокета —
    /// до размещения (<see cref="MissionGenerator"/>), при проверке раскладки (<see cref="LevelValidator"/>) и
    /// при проверке префаба (<c>RoomPrefabGridValidator</c>).
    /// </summary>
    public static class DoorSocketIdRules
    {
        public const string DuplicateIssueCode = "DuplicateDoorSocketId";

        /// <summary>
        /// Ищет id, который носят несколько дверей шаблона, хотя бы одна из которых подключаемая
        /// (<see cref="DoorSocketDescriptorMetrics.IsConnectable"/>). Пустой/null id считается обычным id.
        /// </summary>
        public static bool TryFindDuplicateConnectableSocketId(IRoomTemplateDescriptor template, out string socketId)
        {
            socketId = null;
            return template?.Doors != null && TryFindDuplicateConnectableSocketId(template.Doors, out socketId);
        }

        public static bool TryFindDuplicateConnectableSocketId(
            IReadOnlyList<IDoorSocketDescriptor> doors,
            out string socketId)
        {
            socketId = null;
            if (doors == null || doors.Count < 2)
            {
                return false;
            }

            for (int i = 0; i < doors.Count; i++)
            {
                IDoorSocketDescriptor first = doors[i];
                if (first == null)
                {
                    continue;
                }

                string id = first.SocketId ?? string.Empty;
                for (int j = i + 1; j < doors.Count; j++)
                {
                    IDoorSocketDescriptor second = doors[j];
                    if (second == null
                        || !string.Equals(id, second.SocketId ?? string.Empty, StringComparison.Ordinal)
                        || (!first.IsConnectable() && !second.IsConnectable()))
                    {
                        continue;
                    }

                    socketId = id;
                    return true;
                }
            }

            return false;
        }

        public static string DescribeDuplicate(string roomId, string socketId) =>
            $"Room template '{roomId}' has several door sockets with id '{socketId}' (at least one is connectable). "
            + "Door state is keyed by socket id, so these doors would silently share one state; give every door "
            + "socket of the room a unique id.";

        public static LevelValidationIssue CreateDuplicateIssue(string roomId, string socketId) =>
            new(DuplicateIssueCode, DescribeDuplicate(roomId, socketId), LevelValidationSeverity.Error);
    }
}
