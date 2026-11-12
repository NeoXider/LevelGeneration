using System;
using System.Collections.Generic;
using UnityEngine;

namespace Neoxider.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// Плоская (XZ) геометрия уровня для подсказки маршрута: пол каждой комнаты в виде ячеек сетки и
    /// мировые точки дверей между комнатами. Собирается один раз на уровень: комнаты и двери не двигаются.
    /// Координаты — те же, что у миникарты (<see cref="MinimapRoomSnapshot"/>), поэтому путь ложится ровно
    /// на нарисованные комнаты.
    /// </summary>
    public sealed class RouteHintGeometry
    {
        private const float FallbackPortalWidthMeters = 0.9f;
        private const float PreferredRoomSlackMeters = 0.25f;
        private readonly Dictionary<string, RouteHintRoom> _rooms = new(StringComparer.Ordinal);
        private readonly List<RouteHintDoor> _doors = new();

        public IReadOnlyList<RouteHintDoor> Doors => _doors;
        public int RoomCount => _rooms.Count;

        /// <summary>
        /// Растёт при каждой правке геометрии (комната, дверь, очистка): производные кэши — например, отбор стен
        /// рядом с подсказкой на миникарте — пересобираются по нему, а не каждый кадр.
        /// </summary>
        public int Version { get; private set; }

        public void Clear()
        {
            _rooms.Clear();
            _doors.Clear();
            BumpVersion();
        }

        private void BumpVersion()
        {
            unchecked
            {
                Version++;
            }
        }

        public void AddRoom(MinimapRoomSnapshot room) =>
            AddRoom(room.NodeId, new Vector2(room.X, room.Y), room.RotationDegrees, room.Cells);

        public void AddRoom(
            string nodeId,
            Vector2 origin,
            float rotationDegrees,
            IReadOnlyList<MinimapCellSnapshot> cells,
            IReadOnlyList<RouteHintWall> walls = null)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            _rooms[nodeId] = new RouteHintRoom(nodeId, origin, rotationDegrees, cells, walls);
            BumpVersion();
        }

        public void AddDoor(string nodeA, string socketA, string nodeB, string socketB, Vector2 point,
            float openingWidthMeters = FallbackPortalWidthMeters)
        {
            if (string.IsNullOrEmpty(nodeA) || string.IsNullOrEmpty(nodeB))
            {
                return;
            }

            _doors.Add(new RouteHintDoor(nodeA, socketA, nodeB, socketB, point));
            float halfWidthMeters = Mathf.Max(FallbackPortalWidthMeters, openingWidthMeters) * 0.5f;
            if (_rooms.TryGetValue(nodeA, out RouteHintRoom roomA))
            {
                roomA.RegisterPortal(point, halfWidthMeters);
            }

            if (_rooms.TryGetValue(nodeB, out RouteHintRoom roomB))
            {
                roomB.RegisterPortal(point, halfWidthMeters);
            }

            BumpVersion();
        }

        /// <summary>
        /// Сплошной предмет на полу комнаты (мебель, реквизит): путь обходит его контур с тем же запасом, что и
        /// стены, и не пересекает его. Предмет, накрывающий дверной проём, не учитывается: проём — проход по
        /// раскладке уровня, и стоять в нём может только часть двери.
        /// </summary>
        public void AddObstacle(string nodeId, RouteHintObstacle obstacle)
        {
            if (string.IsNullOrEmpty(nodeId) || !_rooms.TryGetValue(nodeId, out RouteHintRoom room))
            {
                return;
            }

            room.AddObstacle(obstacle);
            BumpVersion();
        }

        public bool TryGetRoom(string nodeId, out RouteHintRoom room)
        {
            room = null;
            return !string.IsNullOrEmpty(nodeId) && _rooms.TryGetValue(nodeId, out room);
        }

        /// <summary>
        /// Точка двери критического пути. Сначала точное совпадение по комнатам и сокетам, затем — по
        /// паре комнат: у одной пары комнат дверь, как правило, одна.
        /// </summary>
        public bool TryFindDoor(CriticalPathDoor door, out RouteHintDoor match)
        {
            for (int i = 0; i < _doors.Count; i++)
            {
                if (_doors[i].Matches(door, true))
                {
                    match = _doors[i];
                    return true;
                }
            }

            for (int i = 0; i < _doors.Count; i++)
            {
                if (_doors[i].Matches(door, false))
                {
                    match = _doors[i];
                    return true;
                }
            }

            match = default;
            return false;
        }

        /// <summary>
        /// Distance from a world point to the nearest wall surface of the rooms around it, capped at
        /// <paramref name="capMeters"/>. Used to tell how much free width the route has at a point.
        /// </summary>
        public float MeasureWallClearance(Vector2 world, float capMeters)
        {
            float best = Mathf.Max(0f, capMeters);
            foreach (KeyValuePair<string, RouteHintRoom> kv in _rooms)
            {
                RouteHintRoom room = kv.Value;
                if (room.Contains(world, best))
                {
                    best = Mathf.Min(best, room.MeasureWallClearance(world, best));
                }
            }

            return best;
        }

        /// <summary>
        /// Nearest floor of a room the filter allows. A player standing in a doorway or door frame is a few
        /// decimetres outside the floor cells of both neighbouring rooms; the route has to start from the
        /// floor next to him rather than give up. <paramref name="preferredNodeId"/> wins near-ties.
        /// </summary>
        public bool TryFindNearestFloor(Vector2 world, Func<string, bool> filter, string preferredNodeId,
            float maxDistanceMeters, out string roomId, out Vector2 floorPoint)
        {
            roomId = null;
            floorPoint = default;
            float best = maxDistanceMeters;
            foreach (KeyValuePair<string, RouteHintRoom> kv in _rooms)
            {
                if (filter != null && !filter(kv.Key))
                {
                    continue;
                }

                if (!kv.Value.TryGetNearestFloorPoint(world, out Vector2 point, out float distance))
                {
                    continue;
                }

                bool preferred = string.Equals(kv.Key, preferredNodeId, StringComparison.Ordinal);
                if (distance < best - 0.0001f || (preferred && distance <= best + PreferredRoomSlackMeters))
                {
                    best = Mathf.Min(best, distance);
                    roomId = kv.Key;
                    floorPoint = point;
                }
            }

            return roomId != null;
        }

        /// <summary>
        /// Комната под точкой среди разрешённых фильтром. На стыке двух комнат точка лежит в обеих —
        /// тогда предпочитается <paramref name="preferredNodeId"/> (текущая комната игрока).
        /// </summary>
        public string FindRoomAt(Vector2 worldPoint, Func<string, bool> filter, string preferredNodeId)
        {
            string found = null;
            foreach (KeyValuePair<string, RouteHintRoom> kv in _rooms)
            {
                if (filter != null && !filter(kv.Key))
                {
                    continue;
                }

                if (!kv.Value.Contains(worldPoint, RouteHintRoom.ContainmentEpsilonMeters))
                {
                    continue;
                }

                if (string.Equals(kv.Key, preferredNodeId, StringComparison.Ordinal))
                {
                    return kv.Key;
                }

                found ??= kv.Key;
            }

            return found;
        }
    }

    /// <summary>Непроходимый отрезок стены в мировых координатах XZ.</summary>
    public readonly struct RouteHintWall
    {
        public readonly Vector2 A;
        public readonly Vector2 B;
        public readonly float WidthMeters;

        /// <summary>
        /// Сторона контура препятствия на полу (<see cref="RouteHintObstacle"/>), а не стена. Пересекать её
        /// нельзя так же, как стену; отличие одно — последний, «узкий» поиск пути держится от предмета на
        /// расстоянии плеча игрока, а не на запасе стены, иначе ряды парт оставались бы без маршрута.
        /// </summary>
        public readonly bool IsObstacleEdge;

        public RouteHintWall(Vector2 a, Vector2 b, float widthMeters) : this(a, b, widthMeters, false)
        {
        }

        public RouteHintWall(Vector2 a, Vector2 b, float widthMeters, bool isObstacleEdge)
        {
            A = a;
            B = b;
            WidthMeters = widthMeters;
            IsObstacleEdge = isObstacleEdge;
        }
    }

    /// <summary>
    /// Сплошное препятствие на полу комнаты в плане (мебель, реквизит, пульт): выпуклый четырёхугольник в мире XZ,
    /// углы по кругу. Путь подсказки не пересекает его контур и держится от него на том же запасе, что и от стен;
    /// точка внутри (игрок стоит на столе) — не пол, а место, с которого сначала надо сойти.
    /// </summary>
    public readonly struct RouteHintObstacle
    {
        public readonly Vector2 A;
        public readonly Vector2 B;
        public readonly Vector2 C;
        public readonly Vector2 D;

        public RouteHintObstacle(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            A = a;
            B = b;
            C = c;
            D = d;
        }

        /// <summary>Повёрнутый прямоугольник: центр и полуоси (векторы от центра к серединам сторон).</summary>
        public static RouteHintObstacle FromBox(Vector2 center, Vector2 halfAxisX, Vector2 halfAxisZ) => new(
            center - halfAxisX - halfAxisZ, center + halfAxisX - halfAxisZ, center + halfAxisX + halfAxisZ,
            center - halfAxisX + halfAxisZ);

        /// <summary>Прямоугольник по осям мира.</summary>
        public static RouteHintObstacle FromRect(Rect rect) => new(new Vector2(rect.xMin, rect.yMin),
            new Vector2(rect.xMax, rect.yMin), new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax));

        public Vector2 Corner(int index) => (index & 3) switch
        {
            0 => A,
            1 => B,
            2 => C,
            _ => D
        };

        public Vector2 Center => (A + B + C + D) * 0.25f;

        /// <summary>Площадь контура, м².</summary>
        public float Area => Mathf.Abs(DoubledSignedArea()) * 0.5f;

        public bool HasArea => Area > 0.00005f;

        /// <summary>Точка внутри контура (на границе — тоже). Вырожденный контур (без площади) ничего не содержит.</summary>
        public bool Contains(Vector2 point)
        {
            if (!HasArea)
            {
                return false;
            }

            bool anyPositive = false;
            bool anyNegative = false;
            for (int i = 0; i < 4; i++)
            {
                Vector2 from = Corner(i);
                Vector2 to = Corner(i + 1);
                float cross = (to.x - from.x) * (point.y - from.y) - (to.y - from.y) * (point.x - from.x);
                anyPositive |= cross > 0.000001f;
                anyNegative |= cross < -0.000001f;
            }

            return !(anyPositive && anyNegative);
        }

        /// <summary>Расстояние от точки до контура; внутри — 0.</summary>
        public float DistanceTo(Vector2 point)
        {
            if (Contains(point))
            {
                return 0f;
            }

            float best = float.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 from = Corner(i);
                Vector2 segment = Corner(i + 1) - from;
                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared > 0.000001f
                    ? Mathf.Clamp01(Vector2.Dot(point - from, segment) / lengthSquared) : 0f;
                best = Mathf.Min(best, (point - (from + segment * t)).magnitude);
            }

            return best;
        }

        private float DoubledSignedArea()
        {
            float doubled = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector2 from = Corner(i);
                Vector2 to = Corner(i + 1);
                doubled += from.x * to.y - to.x * from.y;
            }

            return doubled;
        }
    }

    /// <summary>Чем коллайдер комнаты приходится подсказке маршрута (<see cref="ClassifyRouteHintColliderUseCase"/>).</summary>
    public enum RouteHintColliderRole
    {
        /// <summary>Сплошной предмет на полу: обходить — не перешагнуть и не пройти под ним.</summary>
        Obstacle = 0,

        /// <summary>Пол, ковёр, порожек: верх не выше шага — по нему ходят.</summary>
        Walkable = 1,

        /// <summary>Висит выше роста игрока (светильник, балка): под ним проходят.</summary>
        Overhead = 2,

        /// <summary>
        /// Накрывает большую часть пола комнаты: это объём комнаты (границы, оболочка), а не предмет — как
        /// препятствие он закрыл бы всю комнату. Дефект настройки коллайдера, а не повод терять маршрут.
        /// </summary>
        TooLarge = 3,

        /// <summary>Не сплошной предмет: триггер, выключен, часть двери или стены, архитектурный меш.</summary>
        Ignored = 4
    }

    /// <summary>
    /// Решает, препятствие ли коллайдер для подсказки маршрута, по его высотам над полом комнаты и площади в
    /// плане. Без состояния: факты (сплошной ли это предмет, высоты, площадь) собирает адаптер физики, решение —
    /// здесь.
    /// </summary>
    public static class ClassifyRouteHintColliderUseCase
    {
        /// <summary>Выше этого над полом низ предмета висит над головой: под ним проходят.</summary>
        public const float HeadroomMeters = 1.8f;

        /// <summary>Доля пола комнаты, которую предмет не может накрыть (иначе это объём комнаты).</summary>
        public const float MaxRoomFloorFraction = 0.5f;

        public static RouteHintColliderRole Execute(bool solidProp, float bottomAboveFloorMeters,
            float topAboveFloorMeters, float footprintAreaSquareMeters, float roomFloorAreaSquareMeters,
            float stepMeters)
        {
            if (!solidProp)
            {
                return RouteHintColliderRole.Ignored;
            }

            if (topAboveFloorMeters <= Mathf.Max(0f, stepMeters))
            {
                return RouteHintColliderRole.Walkable;
            }

            if (bottomAboveFloorMeters >= HeadroomMeters)
            {
                return RouteHintColliderRole.Overhead;
            }

            if (roomFloorAreaSquareMeters > 0f
                && footprintAreaSquareMeters > roomFloorAreaSquareMeters * MaxRoomFloorFraction)
            {
                return RouteHintColliderRole.TooLarge;
            }

            return RouteHintColliderRole.Obstacle;
        }
    }

    public readonly struct RouteHintDoor
    {
        public readonly string NodeA;
        public readonly string SocketA;
        public readonly string NodeB;
        public readonly string SocketB;
        public readonly Vector2 Point;

        public RouteHintDoor(string nodeA, string socketA, string nodeB, string socketB, Vector2 point)
        {
            NodeA = nodeA;
            SocketA = socketA;
            NodeB = nodeB;
            SocketB = socketB;
            Point = point;
        }

        public bool Touches(string nodeId) =>
            string.Equals(NodeA, nodeId, StringComparison.Ordinal) || string.Equals(NodeB, nodeId, StringComparison.Ordinal);

        public bool Matches(CriticalPathDoor door, bool compareSockets)
        {
            bool forward = Same(NodeA, door.FromNodeId) && Same(NodeB, door.ToNodeId)
                           && (!compareSockets || (Same(SocketA, door.FromSocketId) && Same(SocketB, door.ToSocketId)));
            bool backward = Same(NodeB, door.FromNodeId) && Same(NodeA, door.ToNodeId)
                            && (!compareSockets || (Same(SocketB, door.FromSocketId) && Same(SocketA, door.ToSocketId)));
            return forward || backward;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>Пол одной комнаты: ячейки сетки в локальных координатах и поза комнаты в мире.</summary>
    public sealed class RouteHintRoom
    {
        /// <summary>Допуск «точка внутри комнаты» для поиска комнаты игрока.</summary>
        public const float ContainmentEpsilonMeters = 0.05f;

        private const float AdjacencyEpsilonMeters = 0.01f;

        /// <summary>Предмет ближе этого к центру дверного проёма — часть двери, а не препятствие.</summary>
        public const float PortalObstacleKeepOutMeters = 0.35f;

        private readonly float _cos;
        private readonly float _sin;
        private readonly List<RouteHintWall> _walls;
        private readonly List<Vector2> _portals = new();
        private readonly Dictionary<Vector2, Vector2> _portalTangents = new();
        private readonly List<RouteHintObstacle> _obstacles = new();
        private readonly List<RouteHintObstacle> _activeObstacles = new();
        private readonly List<RouteHintWall> _blockers = new();

        public RouteHintRoom(string nodeId, Vector2 origin, float rotationDegrees,
            IReadOnlyList<MinimapCellSnapshot> cells, IReadOnlyList<RouteHintWall> walls = null)
        {
            NodeId = nodeId;
            Origin = origin;
            RotationDegrees = rotationDegrees;
            Cells = cells ?? Array.Empty<MinimapCellSnapshot>();
            _walls = walls != null ? new List<RouteHintWall>(walls) : new List<RouteHintWall>();
            float radians = rotationDegrees * Mathf.Deg2Rad;
            _cos = Mathf.Cos(radians);
            _sin = Mathf.Sin(radians);
            RebuildBlockers();
        }

        public string NodeId { get; }
        public Vector2 Origin { get; }
        public float RotationDegrees { get; }
        public IReadOnlyList<MinimapCellSnapshot> Cells { get; }
        /// <summary>Стены комнаты (то, что рисует миникарта), с вырезанными дверными проёмами.</summary>
        public IReadOnlyList<RouteHintWall> Walls => _walls;

        /// <summary>Препятствия на полу, которые путь обходит (без тех, что стоят в дверном проёме).</summary>
        public IReadOnlyList<RouteHintObstacle> Obstacles => _activeObstacles;

        /// <summary>
        /// Всё, что путь не пересекает и от чего держит запас: стены и стороны контуров препятствий
        /// (<see cref="RouteHintWall.IsObstacleEdge"/>). Планировщик проверяет отрезки по этому списку.
        /// </summary>
        public IReadOnlyList<RouteHintWall> Blockers => _blockers;

        /// <summary>Точка внутри препятствия: не пол — отсюда путь начинается с шага на свободное место.</summary>
        public bool IsInsideObstacle(Vector2 world)
        {
            for (int i = 0; i < _activeObstacles.Count; i++)
            {
                if (_activeObstacles[i].Contains(world))
                {
                    return true;
                }
            }

            return false;
        }

        internal void AddObstacle(RouteHintObstacle obstacle)
        {
            _obstacles.Add(obstacle);
            RebuildBlockers();
            InvalidateNavigation();
        }

        /// <summary>
        /// Стены плюс стороны препятствий, кроме тех, что накрывают дверной проём (их ставит дверь, а проём по
        /// раскладке — проход).
        /// </summary>
        private void RebuildBlockers()
        {
            _blockers.Clear();
            _blockers.AddRange(_walls);
            _activeObstacles.Clear();
            for (int i = 0; i < _obstacles.Count; i++)
            {
                RouteHintObstacle obstacle = _obstacles[i];
                if (CoversPortal(obstacle))
                {
                    continue;
                }

                _activeObstacles.Add(obstacle);
                for (int corner = 0; corner < 4; corner++)
                {
                    _blockers.Add(new RouteHintWall(obstacle.Corner(corner), obstacle.Corner(corner + 1), 0f, true));
                }
            }
        }

        private bool CoversPortal(RouteHintObstacle obstacle)
        {
            for (int i = 0; i < _portals.Count; i++)
            {
                if (obstacle.DistanceTo(_portals[i]) < PortalObstacleKeepOutMeters)
                {
                    return true;
                }
            }

            return false;
        }

        private void InvalidateNavigation()
        {
            NavigationPoints = null;
            NavigationDistances = null;
            LastTaskAnchor = null;
            LastTaskDestination = null;
            DoorPairPaths.Clear();
            ClearanceGraphs.Clear();
            ClearanceDoorPairPaths.Clear();
        }
        internal bool IsPortal(Vector2 point)
        {
            for (int i = 0; i < _portals.Count; i++)
            {
                if ((_portals[i] - point).sqrMagnitude < 0.0025f)
                {
                    return true;
                }
            }

            return false;
        }

        // Статический граф обхода перегородок строится один раз на уровень и используется при каждом
        // перепланировании с новой позицией игрока.
        internal List<Vector2> NavigationPoints { get; set; }
        internal float[,] NavigationDistances { get; set; }
        internal Vector2? LastTaskAnchor { get; set; }
        internal Vector2? LastTaskDestination { get; set; }
        internal readonly Dictionary<(Vector2 From, Vector2 To), List<Vector2>> DoorPairPaths = new();

        // Clearance-aware planning keeps its own caches per clearance level; they die with the portals.
        internal readonly Dictionary<(float Clearance, bool Dense), RouteHintNavigationGraph> ClearanceGraphs = new();
        internal readonly Dictionary<(float Clearance, float Approach, Vector2 From, Vector2 To), List<Vector2>> ClearanceDoorPairPaths = new();

        /// <summary>
        /// Связь layout подтверждает проём в стене. Renderer может иметь единый bounds поверх двери;
        /// вырезаем только узкий участок вокруг настоящего сокета, не делая всю стену проходимой.
        /// </summary>
        internal void RegisterPortal(Vector2 point, float halfWidthMeters)
        {
            _portals.Add(point);
            List<RouteHintWall> remaining = new(_walls.Count + 2);
            for (int i = 0; i < _walls.Count; i++)
            {
                RouteHintWall wall = _walls[i];
                Vector2 axis = wall.B - wall.A;
                float length = axis.magnitude;
                if (length < 0.001f)
                {
                    continue;
                }

                Vector2 tangent = axis / length;
                float along = Vector2.Dot(point - wall.A, tangent);
                float perpendicular = Mathf.Abs(tangent.x * (point.y - wall.A.y)
                                               - tangent.y * (point.x - wall.A.x));
                if (along < -halfWidthMeters || along > length + halfWidthMeters
                    || perpendicular > wall.WidthMeters * 0.5f + 0.2f)
                {
                    remaining.Add(wall);
                    continue;
                }

                _portalTangents[point] = tangent;

                float before = Mathf.Clamp(along - halfWidthMeters, 0f, length);
                float after = Mathf.Clamp(along + halfWidthMeters, 0f, length);
                if (before > 0.05f)
                {
                    remaining.Add(new RouteHintWall(wall.A, wall.A + tangent * before, wall.WidthMeters));
                }

                if (length - after > 0.05f)
                {
                    remaining.Add(new RouteHintWall(wall.A + tangent * after, wall.B, wall.WidthMeters));
                }
            }

            _walls.Clear();
            _walls.AddRange(remaining);
            RebuildBlockers();
            InvalidateNavigation();
        }

        /// <summary>
        /// Unit vector from a registered doorway into this room, perpendicular to the wall the doorway cuts
        /// (the wall direction is remembered at registration; without walls the room axes are probed against
        /// the floor).
        /// </summary>
        internal bool TryGetPortalInward(Vector2 point, out Vector2 inward)
        {
            inward = default;
            int index = -1;
            for (int i = 0; i < _portals.Count; i++)
            {
                if ((_portals[i] - point).sqrMagnitude < 0.0025f)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return false;
            }

            Vector2 first;
            Vector2 second;
            Vector2 third = default;
            Vector2 fourth = default;
            int count;
            if (_portalTangents.TryGetValue(_portals[index], out Vector2 tangent))
            {
                first = new Vector2(-tangent.y, tangent.x);
                second = -first;
                count = 2;
            }
            else
            {
                first = (LocalToWorld(Vector2.right) - Origin).normalized;
                second = -first;
                third = (LocalToWorld(Vector2.up) - Origin).normalized;
                fourth = -third;
                count = 4;
            }

            float bestScore = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector2 candidate = i == 0 ? first : i == 1 ? second : i == 2 ? third : fourth;
                float score = ProbeFloor(point, candidate);
                if (score > bestScore && score > ProbeFloor(point, -candidate))
                {
                    bestScore = score;
                    inward = candidate;
                }
            }

            return bestScore > 0f;
        }

        private float ProbeFloor(Vector2 point, Vector2 direction)
        {
            float score = 0f;
            if (Contains(point + direction * 0.35f, 0.02f))
            {
                score += 1f;
            }

            if (Contains(point + direction * 0.9f, 0.02f))
            {
                score += 1f;
            }

            return score;
        }

        /// <summary>
        /// Nearest point of this room's floor, nudged just inside, and how far it is from
        /// <paramref name="world"/>.
        /// </summary>
        public bool TryGetNearestFloorPoint(Vector2 world, out Vector2 point, out float distance)
        {
            point = default;
            distance = float.MaxValue;
            if (Cells.Count == 0)
            {
                return false;
            }

            Vector2 local = WorldToLocal(world);
            Vector2 bestLocal = default;
            Vector2 bestCenter = default;
            for (int i = 0; i < Cells.Count; i++)
            {
                MinimapCellSnapshot cell = Cells[i];
                Vector2 clamped = new(Mathf.Clamp(local.x, cell.X, cell.X + cell.Width),
                    Mathf.Clamp(local.y, cell.Y, cell.Y + cell.Depth));
                float candidate = Vector2.Distance(clamped, local);
                if (candidate < distance)
                {
                    distance = candidate;
                    bestLocal = clamped;
                    bestCenter = new Vector2(cell.X + cell.Width * 0.5f, cell.Y + cell.Depth * 0.5f);
                }
            }

            point = LocalToWorld(Vector2.MoveTowards(bestLocal, bestCenter, 0.06f));
            return true;
        }

        /// <summary>
        /// Distance from a world point to the nearest wall or obstacle surface of this room, capped; zero inside an
        /// obstacle.
        /// </summary>
        internal float MeasureWallClearance(Vector2 world, float capMeters)
        {
            if (IsInsideObstacle(world))
            {
                return 0f;
            }

            float best = Mathf.Max(0f, capMeters);
            for (int i = 0; i < _blockers.Count; i++)
            {
                RouteHintWall wall = _blockers[i];
                float reach = best + wall.WidthMeters * 0.5f;
                if (world.x < Mathf.Min(wall.A.x, wall.B.x) - reach || world.x > Mathf.Max(wall.A.x, wall.B.x) + reach
                    || world.y < Mathf.Min(wall.A.y, wall.B.y) - reach
                    || world.y > Mathf.Max(wall.A.y, wall.B.y) + reach)
                {
                    continue;
                }

                best = Mathf.Min(best,
                    Mathf.Max(0f, DistanceToSegment(world, wall.A, wall.B) - wall.WidthMeters * 0.5f));
            }

            return best;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 direction = b - a;
            float lengthSquared = direction.sqrMagnitude;
            float t = lengthSquared > 0.000001f
                ? Mathf.Clamp01(Vector2.Dot(point - a, direction) / lengthSquared) : 0f;
            return (point - (a + direction * t)).magnitude;
        }

        /// <summary>Тот же поворот, что у отрисовки миникарты (<c>LevelMinimapController.Rotate</c>).</summary>
        public Vector2 LocalToWorld(Vector2 local) =>
            Origin + new Vector2(local.x * _cos + local.y * _sin, -local.x * _sin + local.y * _cos);

        public Vector2 WorldToLocal(Vector2 world)
        {
            Vector2 delta = world - Origin;
            return new Vector2(delta.x * _cos - delta.y * _sin, delta.x * _sin + delta.y * _cos);
        }

        public bool Contains(Vector2 world, float toleranceMeters)
        {
            if (Cells.Count == 0)
            {
                return false;
            }

            Vector2 local = WorldToLocal(world);
            for (int i = 0; i < Cells.Count; i++)
            {
                MinimapCellSnapshot cell = Cells[i];
                if (local.x >= cell.X - toleranceMeters && local.x <= cell.X + cell.Width + toleranceMeters
                    && local.y >= cell.Y - toleranceMeters && local.y <= cell.Y + cell.Depth + toleranceMeters)
                {
                    return true;
                }
            }

            return false;
        }

        public int FindNearestCell(Vector2 world)
        {
            Vector2 local = WorldToLocal(world);
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Cells.Count; i++)
            {
                MinimapCellSnapshot cell = Cells[i];
                float dx = Mathf.Max(cell.X - local.x, 0f, local.x - (cell.X + cell.Width));
                float dy = Mathf.Max(cell.Y - local.y, 0f, local.y - (cell.Y + cell.Depth));
                float distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        public Vector2 CellCenterWorld(int index)
        {
            MinimapCellSnapshot cell = Cells[index];
            return LocalToWorld(new Vector2(cell.X + cell.Width * 0.5f, cell.Y + cell.Depth * 0.5f));
        }

        /// <summary>Ячейки соседствуют, если делят целую сторону (диагональ — это угол стены, не проход).</summary>
        public bool AreAdjacent(int a, int b)
        {
            MinimapCellSnapshot first = Cells[a];
            MinimapCellSnapshot second = Cells[b];
            bool sameRow = Mathf.Abs(first.Y - second.Y) < AdjacencyEpsilonMeters;
            bool sameColumn = Mathf.Abs(first.X - second.X) < AdjacencyEpsilonMeters;
            bool horizontal = sameRow && (Mathf.Abs(first.X + first.Width - second.X) < AdjacencyEpsilonMeters
                                          || Mathf.Abs(second.X + second.Width - first.X) < AdjacencyEpsilonMeters);
            bool vertical = sameColumn && (Mathf.Abs(first.Y + first.Depth - second.Y) < AdjacencyEpsilonMeters
                                           || Mathf.Abs(second.Y + second.Depth - first.Y) < AdjacencyEpsilonMeters);
            return horizontal || vertical;
        }
    }
}
