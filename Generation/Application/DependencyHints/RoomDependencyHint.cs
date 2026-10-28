using System;
using System.Collections.Generic;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints
{
    /// <summary>
    /// Declares that SourceRoomTemplateId should be placed at a topology distance from TargetRoomTemplateId.
    /// </summary>
    public readonly struct RoomDependencyHint
    {
        public readonly string SourceRoomTemplateId;
        public readonly string TargetRoomTemplateId;
        public readonly int MinDistance;
        public readonly int MaxDistance;

        public RoomDependencyHint(string sourceRoomTemplateId, string targetRoomTemplateId, int minDistance,
            int maxDistance)
        {
            SourceRoomTemplateId = sourceRoomTemplateId;
            TargetRoomTemplateId = targetRoomTemplateId;
            MinDistance = minDistance;
            MaxDistance = maxDistance;
        }

        public bool IsValid =>
            !string.IsNullOrEmpty(SourceRoomTemplateId) &&
            !string.IsNullOrEmpty(TargetRoomTemplateId) &&
            MaxDistance >= 0;
    }

    /// <summary>
    /// Достижимость ключа до его замка. Ключ годится только тогда, когда до его комнаты можно дойти от старта,
    /// ни разу не входя в комнату-замок: иначе ученик упирается в запертую дверь, а ключ лежит за ней.
    /// <para>
    /// Обход идёт от старта по дверям в обе стороны (двери проходимы в обе стороны, петля вокруг замка —
    /// законный путь к ключу); комнату-замок обход не посещает и не раскрывает. Один и тот же обход использует
    /// генератор (выбор узла под ключ в <c>MissionGenerator</c>) и валидатор (<c>LevelValidator</c>), поэтому
    /// раскладку, которую генератор считает годной, валидатор не отвергнет, и наоборот.
    /// </para>
    /// </summary>
    public static class KeyLockReachability
    {
        /// <summary>Неориентированная смежность по рёбрам/соединениям (пары узлов).</summary>
        public static Dictionary<string, List<string>> BuildAdjacency(IEnumerable<(string a, string b)> links)
        {
            Dictionary<string, List<string>> adjacency = new(StringComparer.Ordinal);
            if (links == null)
            {
                return adjacency;
            }

            foreach ((string a, string b) in links)
            {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                {
                    continue;
                }

                Add(adjacency, a, b);
                Add(adjacency, b, a);
            }

            return adjacency;
        }

        /// <summary>
        /// Узлы, достижимые от <paramref name="startNodeId"/> без входа в <paramref name="lockNodeId"/>.
        /// Если старт сам является замком, достижимых узлов нет: из комнаты-замка не выйти, не открыв её.
        /// </summary>
        public static HashSet<string> CollectReachableBeforeLock(
            string startNodeId,
            string lockNodeId,
            IReadOnlyDictionary<string, List<string>> adjacency)
        {
            HashSet<string> visited = new(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(startNodeId)
                || string.Equals(startNodeId, lockNodeId, StringComparison.Ordinal))
            {
                return visited;
            }

            Queue<string> queue = new();
            visited.Add(startNodeId);
            queue.Enqueue(startNodeId);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (adjacency == null || !adjacency.TryGetValue(current, out List<string> neighbors))
                {
                    continue;
                }

                for (int i = 0; i < neighbors.Count; i++)
                {
                    string next = neighbors[i];
                    if (string.Equals(next, lockNodeId, StringComparison.Ordinal) || !visited.Add(next))
                    {
                        continue;
                    }

                    queue.Enqueue(next);
                }
            }

            return visited;
        }

        /// <summary>Расстояния (в переходах через двери) от <paramref name="fromNodeId"/> до всех связанных узлов.</summary>
        public static Dictionary<string, int> Distances(
            string fromNodeId,
            IReadOnlyDictionary<string, List<string>> adjacency)
        {
            Dictionary<string, int> distances = new(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(fromNodeId))
            {
                return distances;
            }

            distances[fromNodeId] = 0;
            Queue<string> queue = new();
            queue.Enqueue(fromNodeId);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (adjacency == null || !adjacency.TryGetValue(current, out List<string> neighbors))
                {
                    continue;
                }

                int nextDistance = distances[current] + 1;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    string next = neighbors[i];
                    if (distances.ContainsKey(next))
                    {
                        continue;
                    }

                    distances[next] = nextDistance;
                    queue.Enqueue(next);
                }
            }

            return distances;
        }

        private static void Add(Dictionary<string, List<string>> adjacency, string from, string to)
        {
            if (!adjacency.TryGetValue(from, out List<string> list))
            {
                list = new List<string>(2);
                adjacency[from] = list;
            }

            list.Add(to);
        }
    }
}
