using System;
using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// Дверь критического пути, ориентированная по ходу к выходу: из <see cref="FromNodeId"/>
    /// (уже пройденная сторона) в <see cref="ToNodeId"/> (следующая комната).
    /// </summary>
    public readonly struct CriticalPathDoor : IEquatable<CriticalPathDoor>
    {
        public readonly string FromNodeId;
        public readonly string FromSocketId;
        public readonly string ToNodeId;
        public readonly string ToSocketId;

        public CriticalPathDoor(string fromNodeId, string fromSocketId, string toNodeId, string toSocketId)
        {
            FromNodeId = fromNodeId;
            FromSocketId = fromSocketId;
            ToNodeId = toNodeId;
            ToSocketId = toSocketId;
        }

        public bool IsValid => !string.IsNullOrEmpty(FromNodeId) && !string.IsNullOrEmpty(ToNodeId);

        public bool Equals(CriticalPathDoor other) =>
            string.Equals(FromNodeId, other.FromNodeId, StringComparison.Ordinal)
            && string.Equals(FromSocketId, other.FromSocketId, StringComparison.Ordinal)
            && string.Equals(ToNodeId, other.ToNodeId, StringComparison.Ordinal)
            && string.Equals(ToSocketId, other.ToSocketId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is CriticalPathDoor other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(FromNodeId, FromSocketId, ToNodeId, ToSocketId);
    }

    /// <summary>
    /// Критический путь уровня: комнаты от старта до ближайшего выхода. Строится по связям
    /// <see cref="DoorConnection.IsMainPath"/> — генератор помечает ими ровно обязательный маршрут, и
    /// у развилки-вопроса главной остаётся только дверь правильного ответа, поэтому тупики неправильных
    /// ответов в путь не попадают. Если главных связей не хватает до выхода (старые или ручные layout),
    /// путь ищется по всем связям.
    /// </summary>
    public sealed class LevelCriticalPath
    {
        public static readonly LevelCriticalPath Empty = new(Array.Empty<string>(), Array.Empty<CriticalPathDoor>());

        private LevelCriticalPath(IReadOnlyList<string> nodes, IReadOnlyList<CriticalPathDoor> doors)
        {
            Nodes = nodes;
            Doors = doors;
        }

        /// <summary>Комнаты пути по порядку: первая — старт, последняя — выход.</summary>
        public IReadOnlyList<string> Nodes { get; }

        /// <summary>Двери пути: <c>Doors[i]</c> ведёт из <c>Nodes[i]</c> в <c>Nodes[i + 1]</c>.</summary>
        public IReadOnlyList<CriticalPathDoor> Doors { get; }

        public bool IsEmpty => Nodes.Count < 2;

        public int IndexOf(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return -1;
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                if (string.Equals(Nodes[i], nodeId, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        public static LevelCriticalPath Build(LevelLayout layout)
        {
            if (layout == null || string.IsNullOrEmpty(layout.StartNodeId) || layout.ExitNodeIds.Count == 0)
            {
                return Empty;
            }

            LevelCriticalPath mainPath = Search(layout, true);
            return mainPath.IsEmpty ? Search(layout, false) : mainPath;
        }

        private static LevelCriticalPath Search(LevelLayout layout, bool mainPathOnly)
        {
            HashSet<string> exits = new(StringComparer.Ordinal);
            for (int i = 0; i < layout.ExitNodeIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(layout.ExitNodeIds[i]))
                {
                    exits.Add(layout.ExitNodeIds[i]);
                }
            }

            Dictionary<string, CriticalPathDoor> arrivedThrough = new(StringComparer.Ordinal);
            HashSet<string> visited = new(StringComparer.Ordinal) { layout.StartNodeId };
            Queue<string> frontier = new();
            frontier.Enqueue(layout.StartNodeId);
            string reachedExit = exits.Contains(layout.StartNodeId) ? layout.StartNodeId : null;

            while (reachedExit == null && frontier.Count > 0)
            {
                string nodeId = frontier.Dequeue();
                for (int i = 0; i < layout.Connections.Count; i++)
                {
                    DoorConnection connection = layout.Connections[i];
                    if (mainPathOnly && !connection.IsMainPath)
                    {
                        continue;
                    }

                    if (!TryOrient(connection, nodeId, out CriticalPathDoor door) || !visited.Add(door.ToNodeId))
                    {
                        continue;
                    }

                    arrivedThrough[door.ToNodeId] = door;
                    if (exits.Contains(door.ToNodeId))
                    {
                        reachedExit = door.ToNodeId;
                        break;
                    }

                    frontier.Enqueue(door.ToNodeId);
                }
            }

            if (reachedExit == null || reachedExit == layout.StartNodeId)
            {
                return Empty;
            }

            List<CriticalPathDoor> doors = new();
            string cursor = reachedExit;
            while (arrivedThrough.TryGetValue(cursor, out CriticalPathDoor door))
            {
                doors.Add(door);
                cursor = door.FromNodeId;
            }

            doors.Reverse();
            List<string> nodes = new(doors.Count + 1) { layout.StartNodeId };
            for (int i = 0; i < doors.Count; i++)
            {
                nodes.Add(doors[i].ToNodeId);
            }

            return new LevelCriticalPath(nodes, doors);
        }

        private static bool TryOrient(DoorConnection connection, string fromNodeId, out CriticalPathDoor door)
        {
            if (string.Equals(connection.FromNodeId, fromNodeId, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(connection.ToNodeId))
            {
                door = new CriticalPathDoor(connection.FromNodeId, connection.FromSocketId, connection.ToNodeId,
                    connection.ToSocketId);
                return true;
            }

            if (string.Equals(connection.ToNodeId, fromNodeId, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(connection.FromNodeId))
            {
                door = new CriticalPathDoor(connection.ToNodeId, connection.ToSocketId, connection.FromNodeId,
                    connection.FromSocketId);
                return true;
            }

            door = default;
            return false;
        }
    }
}
