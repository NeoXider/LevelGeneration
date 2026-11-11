using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using RedoSchool.LevelGeneration.Runtime.Domain.Events;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// Состояние миникарты одной миссии. Обе карты показывают посещённые комнаты и комнаты за открытыми
    /// дверями. Комната раскрывается при входе игрока (<see cref="RoomEnteredEvent"/>) или через задержку
    /// после открытия двери в неё (<see cref="ScheduleReveal"/>, владелец 2026-10-14). Ожидающее раскрытие в
    /// checkpoint не переносится; уже раскрытая по двери комната переносится — ученик её видел.
    /// Время приходит снаружи явным аргументом, поэтому сессия тестируется без сцены.
    /// </summary>
    public sealed class LevelMinimapSession
    {
        public const float DoorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;
        public const float CellSizeMeters = LevelGenerationGridMetrics.CellSizeMeters;
        public const float LogicalCellSize = LevelGenerationGridMetrics.FootprintCellDoorSteps;

        private readonly HashSet<string> _revealedRoomIds = new(StringComparer.Ordinal);

        /// <summary>
        /// Комнаты, в которые игрок входил (или подтверждённо посещённые из чекпоинта). Раскрытая открытой
        /// дверью комната видна на карте, но сюда не попадает: в чекпоинт уходят только посещённые.
        /// </summary>
        private readonly HashSet<string> _enteredRoomIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _pendingRevealAt = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _revealAnimationStartedAt = new(StringComparer.Ordinal);
        private readonly List<string> _scratchNodeIds = new();
        private int _visibleRoomsRevealVersion = -1;
        private string _visibleRoomsCurrentRoomId;
        private readonly List<MinimapRoomSnapshot> _visibleRooms = new();
        private readonly List<MinimapConnectionSnapshot> _visibleConnections = new();
        private readonly List<MinimapRoomSnapshot> _allRooms = new();
        private readonly List<MinimapConnectionSnapshot> _allConnections = new();
        private readonly Dictionary<string, Vector2> _floorOffsetsByNodeId = new(StringComparer.Ordinal);
        private LevelLayout _layout;

        public bool HasActiveLevel => _layout != null;
        public bool IsFullMapOpen { get; private set; }
        public string CurrentRoomId { get; private set; }

        /// <summary>Растёт при каждом раскрытии комнаты: подсказке маршрута пора пересчитать путь.</summary>
        public int RevealVersion { get; private set; }

        public bool HasPendingReveals => _pendingRevealAt.Count > 0;

        public void Bind(LevelLayout layout, bool revealStartRoom = true, bool preserveRevealedRooms = false,
            IReadOnlyDictionary<string, Vector2> floorOffsetsByNodeId = null)
        {
            HashSet<string> previousRevealed = preserveRevealedRooms
                ? new HashSet<string>(_revealedRoomIds, StringComparer.Ordinal)
                : null;
            HashSet<string> previousEntered = preserveRevealedRooms
                ? new HashSet<string>(_enteredRoomIds, StringComparer.Ordinal)
                : null;
            string previousCurrentRoomId = preserveRevealedRooms ? CurrentRoomId : null;

            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _floorOffsetsByNodeId.Clear();
            if (floorOffsetsByNodeId != null)
            {
                foreach (KeyValuePair<string, Vector2> offset in floorOffsetsByNodeId)
                {
                    _floorOffsetsByNodeId[offset.Key] = offset.Value;
                }
            }
            _revealedRoomIds.Clear();
            _enteredRoomIds.Clear();
            _pendingRevealAt.Clear();
            _revealAnimationStartedAt.Clear();
            BumpRevealVersion();
            IsFullMapOpen = false;
            CurrentRoomId = null;

            if (previousRevealed != null)
            {
                foreach (string nodeId in previousRevealed)
                {
                    if (FindRoom(nodeId) != null)
                    {
                        _revealedRoomIds.Add(nodeId);
                        if (previousEntered.Contains(nodeId))
                        {
                            _enteredRoomIds.Add(nodeId);
                        }
                    }
                }

                if (FindRoom(previousCurrentRoomId) != null
                    && _revealedRoomIds.Contains(previousCurrentRoomId))
                {
                    CurrentRoomId = previousCurrentRoomId;
                }
            }

            if (revealStartRoom && !string.IsNullOrWhiteSpace(layout.StartNodeId))
            {
                _revealedRoomIds.Add(layout.StartNodeId);
                _enteredRoomIds.Add(layout.StartNodeId);
                CurrentRoomId ??= layout.StartNodeId;
            }
        }

        public void Reset()
        {
            _layout = null;
            _floorOffsetsByNodeId.Clear();
            _revealedRoomIds.Clear();
            _enteredRoomIds.Clear();
            _pendingRevealAt.Clear();
            _revealAnimationStartedAt.Clear();
            BumpRevealVersion();
            _visibleRooms.Clear();
            _visibleConnections.Clear();
            _allRooms.Clear();
            _allConnections.Clear();
            IsFullMapOpen = false;
            CurrentRoomId = null;
        }

        public bool ToggleFullMap()
        {
            if (!HasActiveLevel)
            {
                IsFullMapOpen = false;
                return false;
            }

            IsFullMapOpen = !IsFullMapOpen;
            return true;
        }

        public bool RevealRoom(RoomEnteredEvent evt) => RevealRoom(evt.NodeId);

        /// <summary>
        /// Игрок вошёл в комнату: она становится текущей и сразу раскрывается. Без <paramref name="animationStartTime"/>
        /// раскрытие мгновенное (восстановление, тесты), с ним — с анимацией от этого момента.
        /// </summary>
        public bool RevealRoom(string nodeId, float? animationStartTime = null)
        {
            if (!HasActiveLevel || string.IsNullOrWhiteSpace(nodeId) || FindRoom(nodeId) == null)
            {
                return false;
            }

            CurrentRoomId = nodeId;
            _enteredRoomIds.Add(nodeId);
            _pendingRevealAt.Remove(nodeId);
            return RevealWithoutEntering(nodeId, animationStartTime);
        }

        /// <summary>
        /// Дверь в комнату открылась: комната раскроется в момент <paramref name="revealAtTime"/>. Уже
        /// раскрытая комната и неизвестный узел игнорируются; повторное открытие той же двери не
        /// откладывает раскрытие, а сохраняет более ранний срок.
        /// </summary>
        public bool ScheduleReveal(string nodeId, float revealAtTime)
        {
            if (!HasActiveLevel || string.IsNullOrWhiteSpace(nodeId) || FindRoom(nodeId) == null
                || _revealedRoomIds.Contains(nodeId))
            {
                return false;
            }

            if (_pendingRevealAt.TryGetValue(nodeId, out float existing) && existing <= revealAtTime)
            {
                return false;
            }

            _pendingRevealAt[nodeId] = revealAtTime;
            return true;
        }

        public bool IsRevealPending(string nodeId) =>
            !string.IsNullOrWhiteSpace(nodeId) && _pendingRevealAt.ContainsKey(nodeId);

        /// <summary>
        /// Раскрывает комнаты, чей срок наступил к <paramref name="now"/>; текущую комнату не меняет —
        /// игрок только открыл дверь, а не вошёл. Анимация раскрытия отсчитывается от <paramref name="now"/>.
        /// </summary>
        public int RevealDueRooms(float now)
        {
            if (_pendingRevealAt.Count == 0)
            {
                return 0;
            }

            _scratchNodeIds.Clear();
            foreach (KeyValuePair<string, float> kv in _pendingRevealAt)
            {
                if (kv.Value <= now)
                {
                    _scratchNodeIds.Add(kv.Key);
                }
            }

            int revealed = 0;
            for (int i = 0; i < _scratchNodeIds.Count; i++)
            {
                if (RevealWithoutEntering(_scratchNodeIds[i], now))
                {
                    revealed++;
                }
            }

            return revealed;
        }

        /// <summary>
        /// Возврат из чекпоинта: карта заменяется подтверждённо посещёнными комнатами без анимации; они же
        /// считаются посещёнными. Ожидавшие раскрытия по двери отбрасываются.
        /// </summary>
        public void RestoreRevealedRooms(IEnumerable<string> nodeIds, string currentNodeId)
        {
            if (!HasActiveLevel)
            {
                return;
            }

            _revealedRoomIds.Clear();
            _enteredRoomIds.Clear();
            _pendingRevealAt.Clear();
            _revealAnimationStartedAt.Clear();
            BumpRevealVersion();
            _revealedRoomIds.Add(_layout.StartNodeId);
            _enteredRoomIds.Add(_layout.StartNodeId);
            CurrentRoomId = _layout.StartNodeId;

            if (nodeIds != null)
            {
                foreach (string nodeId in nodeIds)
                {
                    if (!string.IsNullOrWhiteSpace(nodeId) && FindRoom(nodeId) != null)
                    {
                        _enteredRoomIds.Add(nodeId);
                        RevealWithoutEntering(nodeId, null);
                    }
                }
            }

            RevealRoom(currentNodeId);
        }

        /// <summary>
        /// В чекпоинт попадают только посещённые комнаты: раскрытые открытой дверью и ожидающие раскрытия
        /// исключены — после возврата они не должны считаться посещёнными для подсказки маршрута.
        /// </summary>
        public IReadOnlyList<string> GetPersistableRevealedRoomIds()
        {
            List<string> result = new(_revealedRoomIds.Count);
            if (!HasActiveLevel)
            {
                return result;
            }

            for (int i = 0; i < _layout.Rooms.Count; i++)
            {
                string nodeId = _layout.Rooms[i].NodeId;
                if (_revealedRoomIds.Contains(nodeId) && _enteredRoomIds.Contains(nodeId))
                {
                    result.Add(nodeId);
                }
            }

            return result;
        }

        /// <summary>
        /// Дверь <paramref name="socketId"/> комнаты <paramref name="nodeId"/> открылась перед игроком: обе комнаты
        /// её связи раскроются в момент <paramref name="revealAtTime"/> (уже раскрытые пропускаются). Обе — потому
        /// что живая дверь у связи одна: игра оставляет дверь From-комнаты, а дубль в To-комнате выключает и
        /// прячет. Эту одну дверь открывают с обеих сторон, и ученик, вернувшийся к ней из To-комнаты (обходной
        /// путь, возврат из чекпоинта), обязан увидеть комнату за ней, а не «раскрыть» ту, где стоит. Комната, где
        /// он стоит, уже раскрыта, поэтому фактически проявляется только дальняя сторона.
        /// Возвращает, сколько комнат поставлено в очередь.
        /// </summary>
        public int ScheduleRevealAcrossDoor(string nodeId, string socketId, float revealAtTime)
        {
            if (!TryFindConnection(nodeId, socketId, out DoorConnection connection))
            {
                return 0;
            }

            int scheduled = 0;
            if (ScheduleReveal(connection.FromNodeId, revealAtTime))
            {
                scheduled++;
            }

            if (ScheduleReveal(connection.ToNodeId, revealAtTime))
            {
                scheduled++;
            }

            return scheduled;
        }

        /// <summary>Комната по ту сторону двери <paramref name="socketId"/> комнаты <paramref name="nodeId"/>.</summary>
        public string FindRoomBehindDoor(string nodeId, string socketId)
        {
            if (!TryFindConnection(nodeId, socketId, out DoorConnection connection))
            {
                return null;
            }

            return string.Equals(connection.FromNodeId, nodeId, StringComparison.Ordinal)
                   && string.Equals(connection.FromSocketId, socketId, StringComparison.Ordinal)
                ? connection.ToNodeId
                : connection.FromNodeId;
        }

        /// <summary>Связь, одна из сторон которой — дверь <paramref name="socketId"/> комнаты <paramref name="nodeId"/>.</summary>
        private bool TryFindConnection(string nodeId, string socketId, out DoorConnection found)
        {
            found = default;
            if (!HasActiveLevel || string.IsNullOrWhiteSpace(nodeId))
            {
                return false;
            }

            for (int i = 0; i < _layout.Connections.Count; i++)
            {
                DoorConnection connection = _layout.Connections[i];
                if ((string.Equals(connection.FromNodeId, nodeId, StringComparison.Ordinal)
                     && string.Equals(connection.FromSocketId, socketId, StringComparison.Ordinal))
                    || (string.Equals(connection.ToNodeId, nodeId, StringComparison.Ordinal)
                        && string.Equals(connection.ToSocketId, socketId, StringComparison.Ordinal)))
                {
                    found = connection;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Доля проигранной анимации раскрытия комнаты в <c>[0, 1]</c>. Комната без анимации (стартовая,
        /// восстановленная из чекпоинта) — сразу 1.
        /// </summary>
        public float GetRevealProgress(string nodeId, float now, float durationSeconds)
        {
            if (durationSeconds <= 0f || string.IsNullOrEmpty(nodeId)
                || !_revealAnimationStartedAt.TryGetValue(nodeId, out float startedAt))
            {
                return 1f;
            }

            return Mathf.Clamp01((now - startedAt) / durationSeconds);
        }

        public bool HasRevealAnimations => _revealAnimationStartedAt.Count > 0;

        /// <summary>
        /// Комната ещё проявляется: запись анимации есть и не снята <see cref="PruneFinishedRevealAnimations"/>.
        /// Карта рисует такую комнату живым слоем и не кладёт в кэш геометрии, пока запись не снята.
        /// </summary>
        public bool IsRevealAnimating(string nodeId) =>
            !string.IsNullOrEmpty(nodeId) && _revealAnimationStartedAt.ContainsKey(nodeId);

        /// <summary>
        /// Снимает записи доигравших анимаций и возвращает их число: столько комнат пора запечь в кэш.
        /// </summary>
        public int PruneFinishedRevealAnimations(float now, float durationSeconds)
        {
            if (_revealAnimationStartedAt.Count == 0)
            {
                return 0;
            }

            _scratchNodeIds.Clear();
            foreach (KeyValuePair<string, float> kv in _revealAnimationStartedAt)
            {
                if (durationSeconds <= 0f || now - kv.Value >= durationSeconds)
                {
                    _scratchNodeIds.Add(kv.Key);
                }
            }

            for (int i = 0; i < _scratchNodeIds.Count; i++)
            {
                _revealAnimationStartedAt.Remove(_scratchNodeIds[i]);
            }

            return _scratchNodeIds.Count;
        }

        private bool RevealWithoutEntering(string nodeId, float? animationStartTime)
        {
            _pendingRevealAt.Remove(nodeId);
            if (!_revealedRoomIds.Add(nodeId))
            {
                return false;
            }

            if (animationStartTime.HasValue)
            {
                _revealAnimationStartedAt[nodeId] = animationStartTime.Value;
            }

            BumpRevealVersion();
            return true;
        }

        private void BumpRevealVersion()
        {
            unchecked
            {
                RevealVersion++;
            }
        }

        public bool IsRoomRevealed(string nodeId) => !string.IsNullOrWhiteSpace(nodeId) && _revealedRoomIds.Contains(nodeId);

        /// <summary>
        /// Раскрытые комнаты. Список пересобирается только при раскрытии или смене текущей комнаты: карта
        /// спрашивает его несколько раз за кадр, а снимок комнаты несёт список её ячеек.
        /// </summary>
        public IReadOnlyList<MinimapRoomSnapshot> GetVisibleRooms()
        {
            if (_visibleRoomsRevealVersion == RevealVersion
                && string.Equals(_visibleRoomsCurrentRoomId, CurrentRoomId, StringComparison.Ordinal))
            {
                return _visibleRooms;
            }

            _visibleRooms.Clear();
            _visibleRoomsRevealVersion = RevealVersion;
            _visibleRoomsCurrentRoomId = CurrentRoomId;
            if (!HasActiveLevel)
            {
                return _visibleRooms;
            }

            for (int i = 0; i < _layout.Rooms.Count; i++)
            {
                PlacedRoom room = _layout.Rooms[i];
                if (!_revealedRoomIds.Contains(room.NodeId))
                {
                    continue;
                }

                _visibleRooms.Add(CreateRoomSnapshot(room));
            }

            return _visibleRooms;
        }

        public IReadOnlyList<MinimapRoomSnapshot> GetAllRooms()
        {
            _allRooms.Clear();
            if (!HasActiveLevel)
            {
                return _allRooms;
            }

            for (int i = 0; i < _layout.Rooms.Count; i++)
            {
                _allRooms.Add(CreateRoomSnapshot(_layout.Rooms[i]));
            }

            return _allRooms;
        }

        public IReadOnlyList<MinimapConnectionSnapshot> GetVisibleConnections()
        {
            _visibleConnections.Clear();
            if (!HasActiveLevel)
            {
                return _visibleConnections;
            }

            for (int i = 0; i < _layout.Connections.Count; i++)
            {
                DoorConnection connection = _layout.Connections[i];
                if (!_revealedRoomIds.Contains(connection.FromNodeId) ||
                    !_revealedRoomIds.Contains(connection.ToNodeId))
                {
                    continue;
                }

                _visibleConnections.Add(new MinimapConnectionSnapshot(connection.FromNodeId, connection.ToNodeId));
            }

            return _visibleConnections;
        }

        public IReadOnlyList<MinimapConnectionSnapshot> GetAllConnections()
        {
            _allConnections.Clear();
            if (!HasActiveLevel)
            {
                return _allConnections;
            }

            for (int i = 0; i < _layout.Connections.Count; i++)
            {
                DoorConnection connection = _layout.Connections[i];
                _allConnections.Add(new MinimapConnectionSnapshot(connection.FromNodeId, connection.ToNodeId));
            }

            return _allConnections;
        }

        private MinimapRoomSnapshot CreateRoomSnapshot(PlacedRoom room)
        {
            return new MinimapRoomSnapshot(
                room.NodeId,
                room.RoomTemplateId,
                room.WorldPose.HasValue ? room.WorldPose.Value.X : room.Pose.CellX,
                room.WorldPose.HasValue ? room.WorldPose.Value.Z : room.Pose.CellY,
                room.WorldPose.HasValue
                    ? Mathf.RoundToInt(ToQuaternion(room.WorldPose.Value).eulerAngles.y)
                    : room.Pose.RotationDegrees,
                room.NodeId == _layout.StartNodeId,
                Contains(_layout.ExitNodeIds, room.NodeId),
                RoomMapSize(room, room.Footprint.WidthCells),
                RoomMapSize(room, room.Footprint.DepthCells),
                room.NodeId == CurrentRoomId,
                room.WorldPose.HasValue,
                BuildCells(room));
        }

        private PlacedRoom FindRoom(string nodeId)
        {
            if (_layout?.Rooms == null)
            {
                return null;
            }

            for (int i = 0; i < _layout.Rooms.Count; i++)
            {
                PlacedRoom room = _layout.Rooms[i];
                if (room.NodeId == nodeId)
                {
                    return room;
                }
            }

            return null;
        }

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            if (values == null || string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static Quaternion ToQuaternion(RoomWorldPose pose) => new(pose.RotationX, pose.RotationY, pose.RotationZ, pose.RotationW);

        private static float RoomMapSize(PlacedRoom room, int cells) => cells * (room.WorldPose.HasValue ? CellSizeMeters : LogicalCellSize);

        private IReadOnlyList<MinimapCellSnapshot> BuildCells(PlacedRoom room)
        {
            RoomFootprintShape shape = room.FootprintShape;
            IReadOnlyList<RoomFootprintCell> source = shape.Cells;
            float cellSize = room.WorldPose.HasValue ? CellSizeMeters : LogicalCellSize;
            Vector2 offset = _floorOffsetsByNodeId.TryGetValue(room.NodeId, out Vector2 authoredOffset)
                ? authoredOffset : Vector2.zero;
            List<MinimapCellSnapshot> result = new(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                result.Add(new MinimapCellSnapshot(offset.x + source[i].X * cellSize,
                    offset.y + source[i].Z * cellSize, cellSize, cellSize));
            }

            return result;
        }
    }

    public readonly struct MinimapCellSnapshot
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Width;
        public readonly float Depth;

        public MinimapCellSnapshot(float x, float y, float width, float depth)
        {
            X = x;
            Y = y;
            Width = width;
            Depth = depth;
        }
    }

    public readonly struct MinimapRoomSnapshot
    {
        public readonly string NodeId;
        public readonly string RoomTemplateId;
        public readonly float X;
        public readonly float Y;
        public readonly int RotationDegrees;
        public readonly bool IsStart;
        public readonly bool IsExit;
        public readonly float Width;
        public readonly float Depth;
        public readonly bool IsCurrent;
        public readonly bool UsesWorldPose;
        public readonly IReadOnlyList<MinimapCellSnapshot> Cells;

        public MinimapRoomSnapshot(
            string nodeId,
            string roomTemplateId,
            float x,
            float y,
            int rotationDegrees,
            bool isStart,
            bool isExit,
            float width = LevelMinimapSession.LogicalCellSize,
            float depth = LevelMinimapSession.LogicalCellSize,
            bool isCurrent = false,
            bool usesWorldPose = false,
            IReadOnlyList<MinimapCellSnapshot> cells = null)
        {
            NodeId = nodeId;
            RoomTemplateId = roomTemplateId;
            X = x;
            Y = y;
            RotationDegrees = rotationDegrees;
            IsStart = isStart;
            IsExit = isExit;
            Width = Math.Max(LevelMinimapSession.LogicalCellSize, width);
            Depth = Math.Max(LevelMinimapSession.LogicalCellSize, depth);
            IsCurrent = isCurrent;
            UsesWorldPose = usesWorldPose;
            Cells = cells ?? Array.Empty<MinimapCellSnapshot>();
        }
    }

    public readonly struct MinimapConnectionSnapshot
    {
        public readonly string FromNodeId;
        public readonly string ToNodeId;

        public MinimapConnectionSnapshot(string fromNodeId, string toNodeId)
        {
            FromNodeId = fromNodeId;
            ToNodeId = toNodeId;
        }
    }
}
