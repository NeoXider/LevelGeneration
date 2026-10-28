using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Application
{
    public enum RouteHintPathOutcome
    {
        Planned = 0,

        /// <summary>Цели нет: подсказка не нужна или путь уже пройден.</summary>
        NoTarget = 1,

        /// <summary>У целевой двери нет геометрии (комната или точка двери не описаны) — дефект данных.</summary>
        MissingGeometry = 2,

        /// <summary>Комната перед целевой дверью ещё не раскрыта на карте: вести туда нельзя без спойлера.</summary>
        TargetRoomHidden = 3,

        /// <summary>Игрок стоит вне раскрытых комнат (например, телепорт ещё не завершён).</summary>
        PlayerRoomUnknown = 4,

        /// <summary>Между комнатой игрока и целью нет прохода через раскрытые комнаты.</summary>
        Unreachable = 5
    }

    /// <summary>
    /// Строит линию подсказки: от игрока через уже раскрытые комнаты к двери следующей комнаты пути.
    /// Кратчайший маршрут ищется по доступным дверям. Внутри комнаты отрезки проходят непрерывную
    /// проверку пола и препятствий; если прямая перекрыта, используется граф видимости центров ячеек
    /// и концов внутренних стен. Постоянная часть графа кэшируется на всё время жизни геометрии.
    /// Нераскрытые комнаты не участвуют ни как транзит, ни как цель: подсказка не выдаёт карту.
    /// С <see cref="RouteHintPathOptions"/> путь держится от стен не ближе заданного отступа, проходит
    /// центр дверных проёмов по перпендикуляру к стене и по возможности идёт по середине комнат; без
    /// настроек работает прежний кратчайший маршрут. Игрок, стоящий в дверном проёме (чуть за краем пола
    /// своей комнаты), начинает путь с ближайшей точки пола, а не получает «недостижимо».
    /// </summary>
    public static partial class PlanRouteHintPathUseCase
    {
        /// <summary>
        /// Небольшой допуск у границы пола для точки двери на шве комнат.
        /// </summary>
        public const float WallToleranceMeters = 0.03f;

        private const float DuplicatePointMeters = 0.05f;
        private const float PortalFloorExtensionMeters = 0.35f;
        private const float MinLeadOutMeters = 0.5f;
        private const float MinLeadOutDirectionSqrMeters = 0.04f;
        private static readonly float[] LeadOutFractions = { 1f, 0.75f, 0.5f, 0.3f };
        private const float WallPathInsetMeters = 0.45f;
        private const float PlayerWallInsetMeters = 0.025f;
        private const float PlayerClearanceRecoveryMeters = 0.75f;
        private const float DoorwayWallInsetMeters = 0.05f;
        private const float DoorwayWallRadiusMeters = 1.5f;

        /// <summary>
        /// Farthest a student may stand from the floor of a revealed room (doorway, door frame, a step past
        /// the floor edge) and still start a route from the nearest floor point.
        /// </summary>
        public const float StartSnapMeters = 1.2f;

        private const float StepWallInsetMeters = 0.02f;

        /// <summary>
        /// Последний, «узкий» поиск держит от предмета на полу только плечо ученика, а не запас стены: проход между
        /// партами шириной в шаг остаётся проходом. Сам контур предмета не пересекается никогда.
        /// </summary>
        private const float ObstacleNarrowInsetMeters = 0.3f;

        /// <summary>На сколько за край предмета ученик, стоящий на нём, сходит на пол.</summary>
        private const float ObstacleStepOffMeters = 0.35f;

        /// <summary>Свободное место после схода с предмета: не впритык к стене или соседнему предмету.</summary>
        private const float ObstacleStepOffMinClearanceMeters = 0.2f;

        private static readonly float[] ObstacleStepOffRadii = { 0.5f, 0.9f, 1.4f, 2f, 3f };
        private const int RecoveryDirections = 12;
        private static readonly float[] RecoveryRadii = { 0.4f, 0.8f, 1.2f, 1.6f };
        private static readonly float[] TaskApproachRadii = { 0.25f, 0.45f, 0.7f, 1f };
        private static readonly Vector2[] TaskApproachDirections =
        {
            Vector2.right, Vector2.left, Vector2.up, Vector2.down,
            new Vector2(1f, 1f).normalized, new Vector2(-1f, 1f).normalized,
            new Vector2(1f, -1f).normalized, new Vector2(-1f, -1f).normalized
        };

        public static RouteHintPathOutcome Execute(
            RouteHintGeometry geometry,
            Func<string, bool> isRevealed,
            Vector2 playerPosition,
            string currentRoomId,
            CriticalPathDoor target,
            List<Vector2> result,
            Func<RouteHintDoor, bool> isTransitDoorLocked = null) =>
            Execute(geometry, isRevealed, playerPosition, currentRoomId, target, result, isTransitDoorLocked,
                RouteHintPathOptions.Legacy);

        public static RouteHintPathOutcome Execute(
            RouteHintGeometry geometry,
            Func<string, bool> isRevealed,
            Vector2 playerPosition,
            string currentRoomId,
            CriticalPathDoor target,
            List<Vector2> result,
            Func<RouteHintDoor, bool> isTransitDoorLocked,
            RouteHintPathOptions options)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            result.Clear();
            if (!target.IsValid)
            {
                return RouteHintPathOutcome.NoTarget;
            }

            if (geometry == null
                || !geometry.TryGetRoom(target.FromNodeId, out RouteHintRoom _)
                || !geometry.TryFindDoor(target, out RouteHintDoor targetDoor))
            {
                return RouteHintPathOutcome.MissingGeometry;
            }

            RouteHintPathOutcome outcome = PlanTo(geometry, isRevealed, playerPosition, currentRoomId,
                target.FromNodeId, targetDoor.Point, target, result, isTransitDoorLocked, options);
            if (outcome == RouteHintPathOutcome.Planned && options.DoorLeadOutMeters > 0f)
            {
                AppendDoorLeadOut(geometry, target.ToNodeId, result, options.DoorLeadOutMeters);
            }

            return outcome;
        }

        /// <summary>
        /// Carries the route on through the target door, straight along the way it came in, so the trail does not
        /// stop at the door frame. The stub is clipped to what the next room's floor and walls allow (shorter
        /// steps down to <see cref="MinLeadOutMeters"/>); with no room for it the route simply ends at the door.
        /// The next room is not revealed: only this short straight stub is given away, never its layout.
        /// </summary>
        private static void AppendDoorLeadOut(RouteHintGeometry geometry, string nextRoomId, List<Vector2> result,
            float leadOutMeters)
        {
            if (result.Count < 2 || !geometry.TryGetRoom(nextRoomId, out RouteHintRoom next))
            {
                return;
            }

            Vector2 door = result[result.Count - 1];
            Vector2 direction = Vector2.zero;
            for (int i = result.Count - 2; i >= 0; i--)
            {
                Vector2 delta = door - result[i];
                if (delta.sqrMagnitude >= MinLeadOutDirectionSqrMeters)
                {
                    direction = delta.normalized;
                    break;
                }
            }

            if (direction == Vector2.zero)
            {
                return;
            }

            for (int step = 0; step < LeadOutFractions.Length; step++)
            {
                float length = leadOutMeters * LeadOutFractions[step];
                if (length < MinLeadOutMeters)
                {
                    return;
                }

                Vector2 end = door + direction * length;
                if (IsSegmentInside(next, door, end))
                {
                    result.Add(end);
                    return;
                }
            }
        }

        /// <summary>
        /// Ведёт к доступной стороне незавершённой панели. Точка панели может лежать в стене;
        /// каждый кандидат проверяется тем же поиском, что и обычный маршрут.
        /// </summary>
        public static RouteHintPathOutcome ExecuteToRoomTask(RouteHintGeometry geometry,
            Func<string, bool> isRevealed, Vector2 playerPosition, string currentRoomId,
            string taskRoomId, Vector2 taskPoint, List<Vector2> result,
            Func<RouteHintDoor, bool> isTransitDoorLocked = null) =>
            ExecuteToRoomTask(geometry, isRevealed, playerPosition, currentRoomId, taskRoomId, taskPoint, result,
                isTransitDoorLocked, RouteHintPathOptions.Legacy);

        public static RouteHintPathOutcome ExecuteToRoomTask(RouteHintGeometry geometry,
            Func<string, bool> isRevealed, Vector2 playerPosition, string currentRoomId,
            string taskRoomId, Vector2 taskPoint, List<Vector2> result,
            Func<RouteHintDoor, bool> isTransitDoorLocked, RouteHintPathOptions options)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            result.Clear();
            if (geometry == null || !geometry.TryGetRoom(taskRoomId, out RouteHintRoom room))
            {
                return RouteHintPathOutcome.MissingGeometry;
            }

            if (isRevealed != null && !isRevealed(taskRoomId))
            {
                return RouteHintPathOutcome.TargetRoomHidden;
            }

            if (room.LastTaskAnchor.HasValue && room.LastTaskDestination.HasValue
                && (room.LastTaskAnchor.Value - taskPoint).sqrMagnitude < 0.0001f)
            {
                RouteHintPathOutcome cached = PlanTo(geometry, isRevealed, playerPosition, currentRoomId,
                    taskRoomId, room.LastTaskDestination.Value, null, result, isTransitDoorLocked, options);
                if (cached == RouteHintPathOutcome.Planned)
                {
                    return cached;
                }

                result.Clear();
            }

            List<Vector2> scratch = new();
            List<Vector2> best = new();
            for (int ring = -1; ring < TaskApproachRadii.Length; ring++)
            {
                float bestLength = float.MaxValue;
                int count = ring < 0 ? 1 : TaskApproachDirections.Length;
                for (int i = 0; i < count; i++)
                {
                    Vector2 candidate = ring < 0 ? taskPoint
                        : taskPoint + TaskApproachDirections[i] * TaskApproachRadii[ring];
                    if (!IsSegmentInside(room, candidate, candidate))
                    {
                        continue;
                    }

                    scratch.Clear();
                    RouteHintPathOutcome outcome = PlanTo(geometry, isRevealed, playerPosition,
                        currentRoomId, taskRoomId, candidate, null, scratch, isTransitDoorLocked, options);
                    if (outcome == RouteHintPathOutcome.PlayerRoomUnknown)
                    {
                        return outcome;
                    }

                    if (outcome != RouteHintPathOutcome.Planned)
                    {
                        continue;
                    }

                    float length = PathLength(playerPosition, scratch);
                    if (length < bestLength)
                    {
                        bestLength = length;
                        best.Clear();
                        best.AddRange(scratch);
                    }
                }

                if (best.Count > 0)
                {
                    result.AddRange(best);
                    room.LastTaskAnchor = taskPoint;
                    room.LastTaskDestination = best[best.Count - 1];
                    return RouteHintPathOutcome.Planned;
                }
            }

            return RouteHintPathOutcome.Unreachable;
        }

        private static RouteHintPathOutcome PlanTo(
            RouteHintGeometry geometry,
            Func<string, bool> isRevealed,
            Vector2 playerPosition,
            string currentRoomId,
            string targetRoomId,
            Vector2 targetPoint,
            CriticalPathDoor? excludedDoor,
            List<Vector2> result,
            Func<RouteHintDoor, bool> isTransitDoorLocked,
            RouteHintPathOptions options)
        {
            Func<string, bool> revealed = isRevealed ?? (_ => true);
            if (!revealed(targetRoomId))
            {
                return RouteHintPathOutcome.TargetRoomHidden;
            }

            if (!TryResolveStart(geometry, revealed, playerPosition, currentRoomId, out string playerRoom,
                    out Vector2 start))
            {
                return RouteHintPathOutcome.PlayerRoomUnknown;
            }

            // Ученик стоит на столе или ящике: его точка в плане — внутри предмета, и путь от неё прошёл бы
            // сквозь соседние предметы (и лента поднялась бы на их высоту). Сначала шаг вниз — на ближайшее
            // свободное место пола за краем предмета, оттуда обычный обход.
            if (geometry.TryGetRoom(playerRoom, out RouteHintRoom startRoom) && startRoom.IsInsideObstacle(start)
                && TryStepOffObstacle(startRoom, start, out Vector2 floorPoint))
            {
                start = floorPoint;
            }

            List<Vertex> vertices = BuildVertices(geometry, revealed, start, playerRoom, targetRoomId,
                targetPoint, excludedDoor, isTransitDoorLocked);
            List<int> route = FindShortestRoute(geometry, vertices, options, out float _);
            if (route == null)
            {
                route = TryRecoverStart(geometry, vertices, playerRoom, options);
                if (route == null)
                {
                    return RouteHintPathOutcome.Unreachable;
                }
            }

            AppendPoint(result, playerPosition);
            AppendPoint(result, vertices[0].Point);
            for (int i = 0; i + 1 < route.Count; i++)
            {
                Vertex from = vertices[route[i]];
                Vertex to = vertices[route[i + 1]];
                geometry.TryGetRoom(SharedRoom(from, to), out RouteHintRoom room);
                bool stableDoorPair = route[i] >= 2 && route[i + 1] >= 2;
                IReadOnlyList<Vector2> doorPath = stableDoorPair
                    ? FindDoorPairPath(room, from.Point, to.Point, options) : null;
                if (stableDoorPair && doorPath == null
                    || !stableDoorPair && !TryFindRoomPath(room, from.Point, to.Point, result, route[i] == 0,
                        options))
                {
                    result.Clear();
                    return RouteHintPathOutcome.Unreachable;
                }

                if (stableDoorPair)
                {
                    for (int j = 0; j < doorPath.Count; j++)
                    {
                        AppendPoint(result, doorPath[j]);
                    }
                }
            }

            if (result.Count < 2)
            {
                result.Add(targetPoint);
            }

            return RouteHintPathOutcome.Planned;
        }

        /// <summary>
        /// Where the route starts and in which room. The student's own point when it lies on a revealed floor;
        /// otherwise the nearest floor point of a revealed room within <see cref="StartSnapMeters"/> (a doorway
        /// sits a few decimetres outside the floor cells of both neighbours); otherwise the current room as
        /// before, and the path search reports the failure.
        /// </summary>
        private static bool TryResolveStart(RouteHintGeometry geometry, Func<string, bool> revealed,
            Vector2 playerPosition, string currentRoomId, out string roomId, out Vector2 start)
        {
            start = playerPosition;
            roomId = geometry.FindRoomAt(playerPosition, revealed, currentRoomId);
            if (roomId != null)
            {
                return true;
            }

            if (geometry.TryFindNearestFloor(playerPosition, revealed, currentRoomId, StartSnapMeters,
                    out string nearestRoom, out Vector2 floorPoint))
            {
                roomId = nearestRoom;
                start = floorPoint;
                return true;
            }

            if (!string.IsNullOrEmpty(currentRoomId) && revealed(currentRoomId)
                && geometry.TryGetRoom(currentRoomId, out RouteHintRoom _))
            {
                roomId = currentRoomId;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The nearest free floor point next to the obstacle the student stands on: just past the nearest edge or
        /// corner of that obstacle, on this room's floor, outside every obstacle and not pressed against a wall.
        /// Rings around the point are the fallback when the obstacle stands in a corner.
        /// </summary>
        internal static bool TryStepOffObstacle(RouteHintRoom room, Vector2 point, out Vector2 floorPoint) =>
            // Сначала только шаги, которые не пересекают ни стену, ни соседний предмет. Если стол обставлен со всех
            // сторон и такого шага нет, — ближайший свободный пол без этой проверки: короткий шаг через скамью
            // лучше, чем подсказка, пропавшая целиком.
            TryStepOffObstacle(room, point, true, out floorPoint)
            || TryStepOffObstacle(room, point, false, out floorPoint);

        private static bool TryStepOffObstacle(RouteHintRoom room, Vector2 point, bool requireClearStep,
            out Vector2 floorPoint)
        {
            floorPoint = point;
            float bestDistance = float.MaxValue;
            IReadOnlyList<RouteHintObstacle> obstacles = room.Obstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                RouteHintObstacle obstacle = obstacles[i];
                if (!obstacle.Contains(point))
                {
                    continue;
                }

                Vector2 center = obstacle.Center;
                for (int edge = 0; edge < 4; edge++)
                {
                    Vector2 a = obstacle.Corner(edge);
                    Vector2 b = obstacle.Corner(edge + 1);
                    Vector2 segment = b - a;
                    float lengthSquared = segment.sqrMagnitude;
                    if (lengthSquared < 0.000001f)
                    {
                        continue;
                    }

                    Vector2 onEdge = a + segment * Mathf.Clamp01(Vector2.Dot(point - a, segment) / lengthSquared);
                    Vector2 outward = new Vector2(-segment.y, segment.x).normalized;
                    if (Vector2.Dot(outward, onEdge - center) < 0f)
                    {
                        outward = -outward;
                    }

                    TryStepOffCandidate(room, point, onEdge + outward * ObstacleStepOffMeters, requireClearStep,
                        ref floorPoint, ref bestDistance);
                    Vector2 cornerOut = (a - center).normalized;
                    TryStepOffCandidate(room, point, a + cornerOut * ObstacleStepOffMeters, requireClearStep,
                        ref floorPoint, ref bestDistance);
                }
            }

            if (bestDistance < float.MaxValue)
            {
                return true;
            }

            for (int ring = 0; ring < ObstacleStepOffRadii.Length; ring++)
            {
                for (int i = 0; i < RecoveryDirections * 2; i++)
                {
                    float angle = i * (Mathf.PI / RecoveryDirections);
                    TryStepOffCandidate(room, point,
                        point + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * ObstacleStepOffRadii[ring],
                        requireClearStep, ref floorPoint, ref bestDistance);
                }

                if (bestDistance < float.MaxValue)
                {
                    return true;
                }
            }

            return false;
        }

        private static void TryStepOffCandidate(RouteHintRoom room, Vector2 point, Vector2 candidate,
            bool requireClearStep, ref Vector2 best, ref float bestDistance)
        {
            float distance = Vector2.Distance(point, candidate);
            if (distance >= bestDistance || !room.Contains(candidate, 0f) || room.IsInsideObstacle(candidate)
                || room.MeasureWallClearance(candidate, ObstacleStepOffMinClearanceMeters)
                < ObstacleStepOffMinClearanceMeters - 0.0001f
                || requireClearStep && !IsStepOffClear(room, point, candidate))
            {
                return;
            }

            best = candidate;
            bestDistance = distance;
        }

        /// <summary>
        /// The step down from the prop the student stands on is drawn as a straight segment, so the whole segment —
        /// not only its end — must stay clear: it may cross only the edges of the props that contain
        /// <paramref name="from"/> (the ones being stepped off), never a wall or any other prop. Without this a
        /// bench pushed against the table let the nearest free corner win and the first segment ran through the
        /// bench, the very defect the obstacle-aware planner exists to remove.
        /// </summary>
        private static bool IsStepOffClear(RouteHintRoom room, Vector2 from, Vector2 to)
        {
            IReadOnlyList<RouteHintWall> blockers = room.Blockers;
            for (int i = 0; i < blockers.Count; i++)
            {
                RouteHintWall wall = blockers[i];
                if (wall.IsObstacleEdge)
                {
                    continue;
                }

                float clearance = wall.WidthMeters * 0.5f + StepWallInsetMeters;
                if (SegmentsDistanceSquared(from, to, wall.A, wall.B) < clearance * clearance)
                {
                    return false;
                }
            }

            IReadOnlyList<RouteHintObstacle> obstacles = room.Obstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                RouteHintObstacle obstacle = obstacles[i];
                if (obstacle.Contains(from))
                {
                    continue;
                }

                for (int edge = 0; edge < 4; edge++)
                {
                    if (SegmentsDistanceSquared(from, to, obstacle.Corner(edge), obstacle.Corner(edge + 1))
                        < StepWallInsetMeters * StepWallInsetMeters)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// The student stands where no first step is allowed: in a doorway between jambs, in a nook, against a
        /// wall that the recovery rule cannot leave in a straight line. He did walk there, so a short step to
        /// a nearby floor point that does not cross a wall is trusted, and the route starts from that point.
        /// Rings are tried from the closest outwards and the shortest total route of the first working ring wins.
        /// Replaces vertex 0 on success.
        /// </summary>
        private static List<int> TryRecoverStart(RouteHintGeometry geometry, List<Vertex> vertices, string playerRoom,
            RouteHintPathOptions options)
        {
            if (!geometry.TryGetRoom(playerRoom, out RouteHintRoom room)
                || !AreRoomsConnected(vertices, playerRoom, vertices[1].RoomA))
            {
                return null;
            }

            Vertex original = vertices[0];
            for (int ring = 0; ring < RecoveryRadii.Length; ring++)
            {
                List<int> best = null;
                Vector2 bestPoint = original.Point;
                float bestLength = float.MaxValue;
                for (int i = 0; i < RecoveryDirections; i++)
                {
                    float angle = i * (2f * Mathf.PI / RecoveryDirections);
                    Vector2 candidate = original.Point
                                        + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * RecoveryRadii[ring];
                    if (!room.Contains(candidate, 0f) || room.IsInsideObstacle(candidate)
                        || !IsStepWalkable(room, original.Point, candidate))
                    {
                        continue;
                    }

                    vertices[0] = new Vertex(candidate, original.RoomA, original.RoomB);
                    List<int> route = FindShortestRoute(geometry, vertices, options, out float length);
                    if (route != null && RecoveryRadii[ring] + length < bestLength)
                    {
                        best = route;
                        bestPoint = candidate;
                        bestLength = RecoveryRadii[ring] + length;
                    }
                }

                if (best != null)
                {
                    vertices[0] = new Vertex(bestPoint, original.RoomA, original.RoomB);
                    return best;
                }
            }

            vertices[0] = original;
            return null;
        }

        /// <summary>
        /// Open doors (the door vertices that survived the reveal and lock filters) connect the two rooms.
        /// A locked door that cuts the level is not something a step to the side can fix.
        /// </summary>
        private static bool AreRoomsConnected(List<Vertex> vertices, string fromRoom, string toRoom)
        {
            if (string.Equals(fromRoom, toRoom, StringComparison.Ordinal))
            {
                return true;
            }

            HashSet<string> reached = new(StringComparer.Ordinal) { fromRoom };
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 2; i < vertices.Count; i++)
                {
                    string a = vertices[i].RoomA;
                    string b = vertices[i].RoomB;
                    if (reached.Contains(a) && reached.Add(b) || reached.Contains(b) && reached.Add(a))
                    {
                        grew = true;
                    }
                }
            }

            return reached.Contains(toRoom);
        }

        /// <summary>A short step is walkable when it does not touch a wall; floor coverage is not asked.</summary>
        private static bool IsStepWalkable(RouteHintRoom room, Vector2 from, Vector2 to)
        {
            for (int i = 0; i < room.Blockers.Count; i++)
            {
                RouteHintWall wall = room.Blockers[i];
                float clearance = wall.WidthMeters * 0.5f + StepWallInsetMeters;
                if (SegmentsDistanceSquared(from, to, wall.A, wall.B) < clearance * clearance)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<Vertex> BuildVertices(
            RouteHintGeometry geometry,
            Func<string, bool> revealed,
            Vector2 playerPosition,
            string playerRoom,
            string targetRoomId,
            Vector2 targetPoint,
            CriticalPathDoor? excludedDoor,
            Func<RouteHintDoor, bool> isTransitDoorLocked)
        {
            List<Vertex> vertices = new()
            {
                new Vertex(playerPosition, playerRoom, null),
                new Vertex(targetPoint, targetRoomId, null)
            };

            IReadOnlyList<RouteHintDoor> doors = geometry.Doors;
            for (int i = 0; i < doors.Count; i++)
            {
                RouteHintDoor door = doors[i];
                if ((excludedDoor.HasValue && door.Matches(excludedDoor.Value, true))
                    || (isTransitDoorLocked != null && isTransitDoorLocked(door))
                    || !revealed(door.NodeA) || !revealed(door.NodeB)
                    || !geometry.TryGetRoom(door.NodeA, out RouteHintRoom _)
                    || !geometry.TryGetRoom(door.NodeB, out RouteHintRoom _))
                {
                    continue;
                }

                vertices.Add(new Vertex(door.Point, door.NodeA, door.NodeB));
            }

            return vertices;
        }

        /// <summary>Дейкстра от игрока (вершина 0) до цели (вершина 1); рёбра — пары точек в общей комнате.</summary>
        private static List<int> FindShortestRoute(RouteHintGeometry geometry, List<Vertex> vertices,
            RouteHintPathOptions options, out float totalLength)
        {
            totalLength = 0f;
            int count = vertices.Count;
            float[] distance = new float[count];
            int[] previous = new int[count];
            bool[] done = new bool[count];
            Dictionary<long, float> edgeCosts = new();
            for (int i = 0; i < count; i++)
            {
                distance[i] = float.MaxValue;
                previous[i] = -1;
            }

            distance[0] = 0f;
            for (int step = 0; step < count; step++)
            {
                int current = -1;
                for (int i = 0; i < count; i++)
                {
                    if (!done[i] && distance[i] < float.MaxValue && (current < 0 || distance[i] < distance[current]))
                    {
                        current = i;
                    }
                }

                if (current < 0 || current == 1)
                {
                    break;
                }

                done[current] = true;
                for (int next = 0; next < count; next++)
                {
                    string roomId = SharedRoom(vertices[current], vertices[next]);
                    if (done[next] || roomId == null || !geometry.TryGetRoom(roomId, out RouteHintRoom room))
                    {
                        continue;
                    }

                    int low = Math.Min(current, next);
                    int high = Math.Max(current, next);
                    long key = ((long)low << 32) | (uint)high;
                    if (!edgeCosts.TryGetValue(key, out float cost))
                    {
                        if (current >= 2 && next >= 2)
                        {
                            IReadOnlyList<Vector2> doorPath = FindDoorPairPath(room,
                                vertices[current].Point, vertices[next].Point, options);
                            cost = doorPath != null
                                ? PathLength(vertices[current].Point, doorPath) : float.MaxValue;
                        }
                        else
                        {
                            List<Vector2> segment = new();
                            cost = TryFindRoomPath(room, vertices[current].Point, vertices[next].Point, segment,
                                    current == 0, options)
                                ? PathLength(vertices[current].Point, segment) : float.MaxValue;
                        }
                        edgeCosts.Add(key, cost);
                    }

                    if (cost == float.MaxValue)
                    {
                        continue;
                    }

                    float candidate = distance[current] + cost;
                    if (candidate < distance[next])
                    {
                        distance[next] = candidate;
                        previous[next] = current;
                    }
                }
            }

            if (distance[1] >= float.MaxValue)
            {
                return null;
            }

            totalLength = distance[1];
            List<int> route = new();
            for (int cursor = 1; cursor >= 0; cursor = previous[cursor])
            {
                route.Add(cursor);
            }

            route.Reverse();
            return route;
        }

        private static float PathLength(Vector2 start, IReadOnlyList<Vector2> points)
        {
            float length = 0f;
            Vector2 previous = start;
            for (int i = 0; i < points.Count; i++)
            {
                length += Vector2.Distance(previous, points[i]);
                previous = points[i];
            }

            return length;
        }

        private static IReadOnlyList<Vector2> FindDoorPairPath(RouteHintRoom room, Vector2 from, Vector2 to,
            RouteHintPathOptions options)
        {
            if (!options.UsesClearance)
            {
                return FindDoorPairPathLegacy(room, from, to);
            }

            (float Clearance, float Approach, Vector2 From, Vector2 To) key =
                (options.ClearanceMeters, options.DoorApproachMeters, from, to);
            if (!room.ClearanceDoorPairPaths.TryGetValue(key, out List<Vector2> clearPath))
            {
                clearPath = new List<Vector2>();
                if (!TryFindRoomPath(room, from, to, clearPath, false, options))
                {
                    clearPath = null;
                }

                room.ClearanceDoorPairPaths.Add(key, clearPath);
            }

            return clearPath;
        }

        private static IReadOnlyList<Vector2> FindDoorPairPathLegacy(RouteHintRoom room, Vector2 from, Vector2 to)
        {
            (Vector2 From, Vector2 To) key = (from, to);
            if (!room.DoorPairPaths.TryGetValue(key, out List<Vector2> path))
            {
                path = new List<Vector2>();
                if (!TryFindRoomPathLegacy(room, from, to, path))
                {
                    path = null;
                }

                room.DoorPairPaths.Add(key, path);
            }

            return path;
        }

        private static bool TryFindRoomPath(RouteHintRoom room, Vector2 from, Vector2 to, List<Vector2> result,
            bool fromPlayer, RouteHintPathOptions options) =>
            options.UsesClearance
                ? TryFindRoomPathClear(room, from, to, result, fromPlayer, options)
                : TryFindRoomPathLegacy(room, from, to, result, fromPlayer);

        private static bool TryFindRoomPathLegacy(RouteHintRoom room, Vector2 from, Vector2 to,
            List<Vector2> result, bool fromPlayer = false)
        {
            if (room == null || room.Cells.Count == 0)
            {
                return false;
            }

            if (CanTraverse(from, to))
            {
                AppendPoint(result, to);
                return true;
            }

            EnsureNavigationGraph(room);
            List<Vector2> candidates = new() { from, to };
            candidates.AddRange(room.NavigationPoints);
            int count = candidates.Count;
            float[] startCosts = new float[count];
            float[] targetCosts = new float[count];
            startCosts[1] = float.MaxValue;
            for (int i = 2; i < count; i++)
            {
                startCosts[i] = CanTraverse(from, candidates[i])
                    ? Vector2.Distance(from, candidates[i]) : float.MaxValue;
                targetCosts[i] = IsSegmentInside(room, to, candidates[i])
                    ? Vector2.Distance(to, candidates[i]) : float.MaxValue;
            }

            float[] distance = new float[count];
            int[] previous = new int[count];
            bool[] done = new bool[count];
            for (int i = 0; i < count; i++)
            {
                distance[i] = float.MaxValue;
                previous[i] = -1;
            }

            distance[0] = 0f;
            for (int step = 0; step < count; step++)
            {
                int current = -1;
                for (int i = 0; i < count; i++)
                {
                    if (!done[i] && distance[i] < float.MaxValue && (current < 0 || distance[i] < distance[current]))
                    {
                        current = i;
                    }
                }

                if (current < 0 || current == 1)
                {
                    break;
                }

                done[current] = true;
                for (int next = 1; next < count; next++)
                {
                    if (done[next])
                    {
                        continue;
                    }

                    float edge = current == 0 ? startCosts[next]
                        : next == 1 ? targetCosts[current]
                        : room.NavigationDistances[current - 2, next - 2];
                    if (edge == float.MaxValue)
                    {
                        continue;
                    }

                    float candidate = distance[current] + edge;
                    if (candidate < distance[next])
                    {
                        distance[next] = candidate;
                        previous[next] = current;
                    }
                }
            }

            if (distance[1] == float.MaxValue)
            {
                return false;
            }

            List<Vector2> path = new();
            for (int cursor = 1; cursor > 0; cursor = previous[cursor])
            {
                path.Add(candidates[cursor]);
            }

            for (int i = path.Count - 1; i >= 0; i--)
            {
                AppendPoint(result, path[i]);
            }

            return true;

            bool CanTraverse(Vector2 start, Vector2 end) => fromPlayer
                ? IsSegmentInsideFromPlayer(room, start, end)
                : IsSegmentInside(room, start, end);
        }

        /// <summary>Builds the room's visibility graph once (internal: tests measure its size).</summary>
        internal static void EnsureNavigationGraph(RouteHintRoom room)
        {
            if (room.NavigationPoints != null)
            {
                return;
            }

            List<Vector2> candidates = new();
            for (int i = 0; i < room.Cells.Count; i++)
            {
                AddCandidate(room, candidates, room.CellCenterWorld(i));
            }

            // Обход концов перегородок работает и когда весь пол комнаты описан одной большой ячейкой.
            for (int i = 0; i < room.Blockers.Count; i++)
            {
                RouteHintWall wall = room.Blockers[i];
                Vector2 direction = (wall.B - wall.A).normalized;
                if (wall.IsObstacleEdge || direction.sqrMagnitude < 0.5f)
                {
                    continue;
                }

                Vector2 normal = new(-direction.y, direction.x);
                float offset = Mathf.Max(0.22f, wall.WidthMeters * 0.5f + WallPathInsetMeters + 0.05f);
                Vector2 midpoint = (wall.A + wall.B) * 0.5f;
                if (!room.Contains(midpoint + normal * offset, 0f)
                    || !room.Contains(midpoint - normal * offset, 0f))
                {
                    continue;
                }

                AddCandidate(room, candidates, wall.A - direction * offset + normal * offset);
                AddCandidate(room, candidates, wall.A - direction * offset - normal * offset);
                AddCandidate(room, candidates, wall.B + direction * offset + normal * offset);
                AddCandidate(room, candidates, wall.B + direction * offset - normal * offset);
            }

            float obstacleOffset = Mathf.Max(0.22f, ObstacleNarrowInsetMeters + 0.05f);
            for (int i = 0; i < room.Obstacles.Count; i++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    if (TryObstacleCornerDetour(room.Obstacles[i], corner, obstacleOffset, out Vector2 detour))
                    {
                        AddCandidate(room, candidates, detour);
                    }
                }
            }

            int count = candidates.Count;
            float[,] distances = new float[count, count];
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    float cost = IsSegmentInside(room, candidates[i], candidates[j])
                        ? Vector2.Distance(candidates[i], candidates[j]) : float.MaxValue;
                    distances[i, j] = cost;
                    distances[j, i] = cost;
                }
            }

            room.NavigationDistances = distances;
            room.NavigationPoints = candidates;
        }

        private static void AddCandidate(RouteHintRoom room, List<Vector2> candidates, Vector2 point)
        {
            if (!room.Contains(point, 0f) || IsInsideWall(room, point) || room.IsInsideObstacle(point))
            {
                return;
            }

            candidates.Add(point);
        }

        /// <summary>
        /// The detour point of one prop corner: on the corner's outer miter, <paramref name="offset"/> away from
        /// both sides that meet there. A convex prop is passed around by its corners alone. Props used to get the
        /// four end points per side that partitions get — 16 points per box, half of them duplicates, and the
        /// visibility graph is quadratic in points, so a furnished room cost hundreds of milliseconds on WebGL.
        /// </summary>
        internal static bool TryObstacleCornerDetour(RouteHintObstacle obstacle, int corner, float offset,
            out Vector2 detour)
        {
            Vector2 point = obstacle.Corner(corner);
            Vector2 center = obstacle.Center;
            Vector2 before = OutwardNormal(obstacle.Corner(corner + 3), point, center);
            Vector2 after = OutwardNormal(point, obstacle.Corner(corner + 1), center);
            detour = point;
            if (before == Vector2.zero || after == Vector2.zero)
            {
                return false;
            }

            // Miter: (n1 + n2) / (1 + n1·n2) keeps the point exactly `offset` from both side lines. A sharp corner
            // would push it far out, so the miter is capped at twice the offset.
            Vector2 sum = before + after;
            float denominator = 1f + Vector2.Dot(before, after);
            Vector2 miter = denominator > 0.25f ? sum / denominator : sum.normalized * 2f;
            if (miter.magnitude > 2f)
            {
                miter = miter.normalized * 2f;
            }

            detour = point + miter * offset;
            return true;
        }

        private static Vector2 OutwardNormal(Vector2 a, Vector2 b, Vector2 center)
        {
            Vector2 side = b - a;
            if (side.sqrMagnitude < 0.000001f)
            {
                return Vector2.zero;
            }

            Vector2 normal = new Vector2(-side.y, side.x).normalized;
            return Vector2.Dot(normal, (a + b) * 0.5f - center) < 0f ? -normal : normal;
        }

        public static bool IsSegmentInside(RouteHintRoom room, Vector2 from, Vector2 to)
            => IsSegmentInside(room, from, to, WallPathInsetMeters);

        public static bool IsSegmentInsideFromPlayer(RouteHintRoom room, Vector2 from, Vector2 to)
        {
            if (!IsSegmentInside(room, from, to, PlayerWallInsetMeters)
                || !room.IsPortal(to) && !IsSegmentInside(room, to, to))
            {
                return false;
            }

            float length = Vector2.Distance(from, to);
            return length <= PlayerClearanceRecoveryMeters
                   || IsSegmentInside(room,
                       Vector2.MoveTowards(from, to, PlayerClearanceRecoveryMeters), to);
        }

        private static bool IsSegmentInside(RouteHintRoom room, Vector2 from, Vector2 to, float wallInsetMeters)
        {
            if (room == null || room.Cells.Count == 0)
            {
                return false;
            }

            Vector2 floorFrom = from;
            Vector2 floorTo = to;
            float length = Vector2.Distance(from, to);
            if (length > 0.0001f)
            {
                float trim = Mathf.Min(PortalFloorExtensionMeters, length * 0.49f);
                if (room.IsPortal(from))
                {
                    floorFrom = Vector2.MoveTowards(from, to, trim);
                }

                if (room.IsPortal(to))
                {
                    floorTo = Vector2.MoveTowards(to, from, trim);
                }
            }

            if (!IsCoveredByFloor(room, floorFrom, floorTo))
            {
                return false;
            }

            // Отрезок целиком внутри предмета не задевает его сторон — его ловит проверка концов.
            if (room.IsInsideObstacle(from) || room.IsInsideObstacle(to))
            {
                return false;
            }

            for (int i = 0; i < room.Blockers.Count; i++)
            {
                RouteHintWall wall = room.Blockers[i];
                float inset = wall.IsObstacleEdge ? Mathf.Min(wallInsetMeters, ObstacleNarrowInsetMeters)
                    : wallInsetMeters;
                if ((room.IsPortal(from)
                     && PointSegmentDistanceSquared(from, wall.A, wall.B)
                     < DoorwayWallRadiusMeters * DoorwayWallRadiusMeters)
                    || (room.IsPortal(to)
                        && PointSegmentDistanceSquared(to, wall.A, wall.B)
                        < DoorwayWallRadiusMeters * DoorwayWallRadiusMeters))
                {
                    inset = DoorwayWallInsetMeters;
                }

                float clearance = wall.WidthMeters * 0.5f + inset;
                if (Mathf.Max(from.x, to.x) + clearance < Mathf.Min(wall.A.x, wall.B.x)
                    || Mathf.Min(from.x, to.x) - clearance > Mathf.Max(wall.A.x, wall.B.x)
                    || Mathf.Max(from.y, to.y) + clearance < Mathf.Min(wall.A.y, wall.B.y)
                    || Mathf.Min(from.y, to.y) - clearance > Mathf.Max(wall.A.y, wall.B.y))
                {
                    continue;
                }

                if (SegmentsDistanceSquared(from, to, wall.A, wall.B) < clearance * clearance)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCoveredByFloor(RouteHintRoom room, Vector2 from, Vector2 to)
        {
            Vector2 a = room.WorldToLocal(from);
            Vector2 delta = room.WorldToLocal(to) - a;
            float covered = 0f;
            for (int step = 0; step < room.Cells.Count; step++)
            {
                float furthest = covered;
                for (int i = 0; i < room.Cells.Count; i++)
                {
                    MinimapCellSnapshot cell = room.Cells[i];
                    float start = 0f;
                    float end = 1f;
                    if (Clip(-delta.x, a.x - (cell.X - WallToleranceMeters), ref start, ref end)
                        && Clip(delta.x, cell.X + cell.Width + WallToleranceMeters - a.x, ref start, ref end)
                        && Clip(-delta.y, a.y - (cell.Y - WallToleranceMeters), ref start, ref end)
                        && Clip(delta.y, cell.Y + cell.Depth + WallToleranceMeters - a.y, ref start, ref end)
                        && start <= covered + 0.00001f)
                    {
                        furthest = Mathf.Max(furthest, end);
                    }
                }

                if (furthest <= covered + 0.00001f)
                {
                    return false;
                }

                covered = furthest;
                if (covered >= 1f - 0.00001f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Clip(float p, float q, ref float start, ref float end)
        {
            if (Mathf.Abs(p) < 0.000001f)
            {
                return q >= 0f;
            }

            float ratio = q / p;
            if (p < 0f)
            {
                start = Mathf.Max(start, ratio);
            }
            else
            {
                end = Mathf.Min(end, ratio);
            }

            return start <= end;
        }

        private static bool IsInsideWall(RouteHintRoom room, Vector2 point)
        {
            for (int i = 0; i < room.Blockers.Count; i++)
            {
                RouteHintWall wall = room.Blockers[i];
                float inset = wall.IsObstacleEdge ? ObstacleNarrowInsetMeters : WallPathInsetMeters;
                float clearance = wall.WidthMeters * 0.5f + inset;
                if (PointSegmentDistanceSquared(point, wall.A, wall.B) < clearance * clearance)
                {
                    return true;
                }
            }

            return false;
        }

        private static float SegmentsDistanceSquared(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 ab = b - a;
            Vector2 cd = d - c;
            float cross1 = Cross(ab, c - a);
            float cross2 = Cross(ab, d - a);
            float cross3 = Cross(cd, a - c);
            float cross4 = Cross(cd, b - c);
            if (Mathf.Abs(Cross(ab, cd)) > 0.000001f
                && cross1 * cross2 <= 0f && cross3 * cross4 <= 0f)
            {
                return 0f;
            }

            return Mathf.Min(
                Mathf.Min(PointSegmentDistanceSquared(a, c, d), PointSegmentDistanceSquared(b, c, d)),
                Mathf.Min(PointSegmentDistanceSquared(c, a, b), PointSegmentDistanceSquared(d, a, b)));
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static float PointSegmentDistanceSquared(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 direction = b - a;
            float lengthSquared = direction.sqrMagnitude;
            float t = lengthSquared > 0.000001f
                ? Mathf.Clamp01(Vector2.Dot(point - a, direction) / lengthSquared)
                : 0f;
            return (point - (a + direction * t)).sqrMagnitude;
        }

        private static void AppendPoint(List<Vector2> result, Vector2 point)
        {
            if (result.Count > 0 && Vector2.Distance(result[result.Count - 1], point) < DuplicatePointMeters)
            {
                return;
            }

            result.Add(point);
        }

        private static string SharedRoom(Vertex a, Vertex b)
        {
            if (a.RoomA != null && (Same(a.RoomA, b.RoomA) || Same(a.RoomA, b.RoomB)))
            {
                return a.RoomA;
            }

            if (a.RoomB != null && (Same(a.RoomB, b.RoomA) || Same(a.RoomB, b.RoomB)))
            {
                return a.RoomB;
            }

            return null;
        }

        private static bool Same(string a, string b) => b != null && string.Equals(a, b, StringComparison.Ordinal);

        private readonly struct Vertex
        {
            public readonly Vector2 Point;
            public readonly string RoomA;
            public readonly string RoomB;

            public Vertex(Vector2 point, string roomA, string roomB)
            {
                Point = point;
                RoomA = roomA;
                RoomB = roomB;
            }
        }
    }
}
