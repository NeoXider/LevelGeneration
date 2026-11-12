using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Default room selector. Filters templates by tileset, RoomKind, role flags and required connectable doors.
    /// </summary>
    public sealed class RoomSelector : IRoomSelector
    {
        private readonly IInteractiveRoomKindRules _interactiveRoomKinds;

        /// <param name="interactiveRoomKinds">
        /// Interactive room types of the host game: which rooms host which mission content.
        /// </param>
        public RoomSelector(IInteractiveRoomKindRules interactiveRoomKinds)
        {
            _interactiveRoomKinds = interactiveRoomKinds
                                    ?? throw new ArgumentNullException(nameof(interactiveRoomKinds));
        }

        public IRoomTemplateDescriptor Select(
            RoomNode node,
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase database,
            IDeterministicRng rng)
        {
            IReadOnlyList<IRoomTemplateDescriptor> tilesetPool = database.GetRoomsForTileset(tileset.TilesetId);
            IReadOnlyList<IRoomTemplateDescriptor> pool = ResolveMissionPool(config, tilesetPool, database.AllRooms);
            pool = IncludePreferredRoom(pool, tilesetPool, node.PreferredRoomTemplateId);
            if (pool == null || pool.Count == 0)
            {
                return null;
            }

            EffectiveRoomWeightResolver resolver = BuildWeightResolver(config);
            bool requiresContent = !string.IsNullOrWhiteSpace(node.ContentId);
            IInteractiveMissionContentSpec contentSpec = null;
            if (requiresContent
                && !InteractiveMissionContentMatcher.TryFindContentSpec(config, node.ContentId, out contentSpec))
            {
                return null;
            }

            bool enforcePreferredRoom = ShouldEnforcePreferredRoom(node, pool, contentSpec);
            List<IRoomTemplateDescriptor> candidates = new(pool.Count);
            for (int i = 0; i < pool.Count; i++)
            {
                IRoomTemplateDescriptor room = pool[i];
                if (room == null || !MatchesKind(node, room, config))
                {
                    continue;
                }

                if (enforcePreferredRoom
                    && !string.Equals(room.RoomId, node.PreferredRoomTemplateId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!MatchesRoleFlags(node, room))
                {
                    continue;
                }

                if (!HasEnoughConnectableDoors(room, node.RequiredDoorCount))
                {
                    continue;
                }

                int weight = ResolveSelectionWeight(node, room, resolver);
                if (weight <= 0)
                {
                    continue;
                }

                candidates.Add(room);
            }

            if (candidates.Count == 0)
            {
                return requiresContent ? null : FallbackSelect(node, pool, config, resolver);
            }

            if (contentSpec != null)
            {
                List<IRoomTemplateDescriptor> contentCandidates = FilterByInteractiveContent(candidates, contentSpec);
                if (contentCandidates.Count == 0)
                {
                    return null;
                }

                candidates = contentCandidates;

                List<IRoomTemplateDescriptor> exactContentDoorCandidates =
                    FilterByExactDoorCount(candidates, node.RequiredDoorCount);
                if (exactContentDoorCandidates.Count > 0)
                {
                    candidates = exactContentDoorCandidates;
                }
            }
            else
            {
                if (ShouldPreferExactDoorCount(node))
                {
                    List<IRoomTemplateDescriptor> exactDoorCandidates =
                        FilterByExactDoorCount(candidates, node.RequiredDoorCount);
                    if (exactDoorCandidates.Count > 0)
                    {
                        candidates = exactDoorCandidates;
                    }
                }

                List<IRoomTemplateDescriptor> quizBranchCandidates =
                    FilterByQuizBranchOptionCount(candidates, node, config);
                if (quizBranchCandidates.Count > 0)
                {
                    candidates = quizBranchCandidates;
                }
            }

            candidates.Sort(static (a, b) => string.CompareOrdinal(a.RoomId, b.RoomId));

            int totalWeight = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                totalWeight += ResolveSelectionWeight(node, candidates[i], resolver);
            }

            if (totalWeight <= 0)
            {
                return candidates[0];
            }

            int roll = rng.NextInt(0, totalWeight);
            int accumulator = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                accumulator += ResolveSelectionWeight(node, candidates[i], resolver);
                if (roll < accumulator)
                {
                    return candidates[i];
                }
            }

            return candidates[candidates.Count - 1];
        }

        private static IReadOnlyList<IRoomTemplateDescriptor> ResolveMissionPool(
            IMissionConfigView config,
            IReadOnlyList<IRoomTemplateDescriptor> tilesetPool,
            IReadOnlyList<IRoomTemplateDescriptor> allRooms)
        {
            IReadOnlyList<IMissionRoomRule> rules = config?.MissionRoomRules;
            if (rules == null || rules.Count == 0)
            {
                return tilesetPool;
            }

            HashSet<string> allowed = new(StringComparer.Ordinal);
            for (int i = 0; i < rules.Count; i++)
            {
                if (!string.IsNullOrEmpty(rules[i]?.RoomId))
                {
                    allowed.Add(rules[i].RoomId);
                }
            }

            if (allowed.Count == 0)
            {
                return tilesetPool;
            }

            List<IRoomTemplateDescriptor> result = new(allowed.Count);
            AddAllowedRooms(result, tilesetPool, allowed);
            return result.Count > 0 ? result : tilesetPool ?? Array.Empty<IRoomTemplateDescriptor>();
        }

        private static bool ShouldPreferExactDoorCount(RoomNode node)
        {
            return node == null
                   || node.Kind != RoomKind.Passage
                   || node.IsDeadEnd;
        }

        private bool ShouldEnforcePreferredRoom(
            RoomNode node,
            IReadOnlyList<IRoomTemplateDescriptor> pool,
            IInteractiveMissionContentSpec contentSpec)
        {
            if (node == null || string.IsNullOrEmpty(node.PreferredRoomTemplateId))
            {
                return false;
            }

            if (contentSpec == null)
            {
                return true;
            }

            IRoomTemplateDescriptor preferred = FindRoom(pool, node.PreferredRoomTemplateId);
            return preferred != null
                   && InteractiveMissionContentMatcher.IsCompatible(_interactiveRoomKinds, preferred, contentSpec);
        }

        private int ResolveSelectionWeight(
            RoomNode node,
            IRoomTemplateDescriptor room,
            EffectiveRoomWeightResolver resolver)
        {
            int weight = resolver.Resolve(room);
            if (weight <= 0)
            {
                return 0;
            }

            int connectableDoors = ConnectableDoorCount(room);
            if (node != null
                && node.Kind == RoomKind.Interactive
                && InteractiveMissionContentMatcher.IsInteractiveRoom(_interactiveRoomKinds, room))
            {
                weight = SafeMultiply(weight, 4);
            }

            if (node != null
                && node.Kind == RoomKind.Passage
                && !node.IsDeadEnd
                && connectableDoors >= 3)
            {
                weight = SafeMultiply(weight, 2);
            }

            return weight;
        }

        private static int SafeMultiply(int value, int multiplier)
        {
            if (value <= 0 || multiplier <= 0)
            {
                return 0;
            }

            return value > int.MaxValue / multiplier ? int.MaxValue : value * multiplier;
        }

        private static void AddAllowedRooms(
            List<IRoomTemplateDescriptor> target,
            IReadOnlyList<IRoomTemplateDescriptor> source,
            ISet<string> allowed)
        {
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                IRoomTemplateDescriptor room = source[i];
                if (room != null && allowed.Contains(room.RoomId) && !ContainsRoom(target, room.RoomId))
                {
                    target.Add(room);
                }
            }
        }

        private static IReadOnlyList<IRoomTemplateDescriptor> IncludePreferredRoom(
            IReadOnlyList<IRoomTemplateDescriptor> pool,
            IReadOnlyList<IRoomTemplateDescriptor> tilesetPool,
            string preferredRoomTemplateId)
        {
            if (string.IsNullOrEmpty(preferredRoomTemplateId) || tilesetPool == null)
            {
                return pool;
            }

            if (ContainsRoom(pool, preferredRoomTemplateId))
            {
                return pool;
            }

            IRoomTemplateDescriptor preferred = FindRoom(tilesetPool, preferredRoomTemplateId);
            if (preferred == null)
            {
                return pool;
            }

            List<IRoomTemplateDescriptor> result = pool != null
                ? new List<IRoomTemplateDescriptor>(pool)
                : new List<IRoomTemplateDescriptor>(1);
            result.Add(preferred);
            return result;
        }

        private static bool ContainsRoom(IReadOnlyList<IRoomTemplateDescriptor> rooms, string roomId) => FindRoom(rooms, roomId) != null;

        private static IRoomTemplateDescriptor FindRoom(IReadOnlyList<IRoomTemplateDescriptor> rooms, string roomId)
        {
            if (rooms == null)
            {
                return null;
            }

            for (int i = 0; i < rooms.Count; i++)
            {
                IRoomTemplateDescriptor room = rooms[i];
                if (room != null && string.Equals(room.RoomId, roomId, StringComparison.Ordinal))
                {
                    return room;
                }
            }

            return null;
        }

        private static List<IRoomTemplateDescriptor> FilterByExactDoorCount(
            IReadOnlyList<IRoomTemplateDescriptor> candidates,
            int requiredDoorCount)
        {
            List<IRoomTemplateDescriptor> result = new();
            if (requiredDoorCount <= 0)
            {
                return result;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                IRoomTemplateDescriptor room = candidates[i];
                if (ConnectableDoorCount(room) == requiredDoorCount)
                {
                    result.Add(room);
                }
            }

            return result;
        }

        private static List<IRoomTemplateDescriptor> FilterByQuizBranchOptionCount(
            IReadOnlyList<IRoomTemplateDescriptor> candidates,
            RoomNode node,
            IMissionConfigView config)
        {
            List<IRoomTemplateDescriptor> result = new();
            if (node == null
                || node.Kind != RoomKind.Interactive
                || config is not IQuizBranchMissionConfigView quizConfig
                || quizConfig.QuizBranchAnswerCount <= 0)
            {
                return result;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                IRoomTemplateDescriptor room = candidates[i];
                if (AnswerDoorCount(room) == quizConfig.QuizBranchAnswerCount)
                {
                    result.Add(room);
                }
            }

            return result;
        }

        private List<IRoomTemplateDescriptor> FilterByInteractiveContent(
            IReadOnlyList<IRoomTemplateDescriptor> candidates,
            IInteractiveMissionContentSpec content)
        {
            List<IRoomTemplateDescriptor> result = new();
            if (candidates == null || content == null)
            {
                return result;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                IRoomTemplateDescriptor room = candidates[i];
                if (InteractiveMissionContentMatcher.IsCompatible(_interactiveRoomKinds, room, content))
                {
                    result.Add(room);
                }
            }

            return result;
        }

        private static bool HasEnoughConnectableDoors(
            IRoomTemplateDescriptor room,
            int requiredDoorCount,
            bool allowLegacyWithoutDoorMetadata = false)
        {
            if (requiredDoorCount <= 0)
            {
                return true;
            }

            if (room?.Doors == null || room.Doors.Count == 0)
            {
                return allowLegacyWithoutDoorMetadata;
            }

            return ConnectableDoorCount(room) >= requiredDoorCount;
        }

        private static int ConnectableDoorCount(IRoomTemplateDescriptor room)
        {
            if (room?.Doors == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < room.Doors.Count; i++)
            {
                if (room.Doors[i] != null && room.Doors[i].IsConnectable())
                {
                    count++;
                }
            }

            return count;
        }

        private static int AnswerDoorCount(IRoomTemplateDescriptor room)
        {
            if (room?.Doors == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < room.Doors.Count; i++)
            {
                if (room.Doors[i] != null
                    && room.Doors[i].IsConnectable()
                    && room.Doors[i] is IDoorSocketInitialStateDescriptor state
                    && state.InitialState == DoorState.Locked)
                {
                    count++;
                }
            }

            return count;
        }

        private static List<IRoomTemplateDescriptor> SortByRoomId(IReadOnlyList<IRoomTemplateDescriptor> pool)
        {
            List<IRoomTemplateDescriptor> ordered = new(pool.Count);
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i] != null)
                {
                    ordered.Add(pool[i]);
                }
            }

            ordered.Sort(static (a, b) => string.CompareOrdinal(a.RoomId, b.RoomId));
            return ordered;
        }

        private IRoomTemplateDescriptor FallbackSelect(
            RoomNode node,
            IReadOnlyList<IRoomTemplateDescriptor> pool,
            IMissionConfigView config,
            EffectiveRoomWeightResolver resolver)
        {
            if (pool == null || pool.Count == 0)
            {
                return null;
            }

            // Fallback selection must be deterministic across machines/platforms for a fixed seed.
            // The pool order depends on RoomDatabase build order (Resources/AssetDatabase enumeration),
            // so sort by RoomId with the same comparer the main path uses before choosing.
            List<IRoomTemplateDescriptor> orderedPool = SortByRoomId(pool);

            if (!string.IsNullOrEmpty(node.PreferredRoomTemplateId))
            {
                IRoomTemplateDescriptor preferred = FindRoom(orderedPool, node.PreferredRoomTemplateId);
                if (preferred != null && IsFallbackCandidate(node, preferred, config, resolver))
                {
                    return preferred;
                }
            }

            for (int i = 0; i < orderedPool.Count; i++)
            {
                IRoomTemplateDescriptor room = orderedPool[i];
                if (IsFallbackCandidate(node, room, config, resolver))
                {
                    return room;
                }
            }

            return SelectBestEffortFallback(node, orderedPool, config, resolver);
        }

        private IRoomTemplateDescriptor SelectBestEffortFallback(
            RoomNode node,
            IReadOnlyList<IRoomTemplateDescriptor> pool,
            IMissionConfigView config,
            EffectiveRoomWeightResolver resolver)
        {
            IRoomTemplateDescriptor best = null;
            int bestConnectableDoors = -1;
            for (int i = 0; i < pool.Count; i++)
            {
                IRoomTemplateDescriptor room = pool[i];
                if (room == null
                    || !MatchesKind(node, room, config)
                    || !MatchesRoleFlags(node, room)
                    || ResolveSelectionWeight(node, room, resolver) <= 0)
                {
                    continue;
                }

                int connectableDoors = ConnectableDoorCount(room);
                if (connectableDoors <= bestConnectableDoors)
                {
                    continue;
                }

                bestConnectableDoors = connectableDoors;
                best = room;
            }

            return best;
        }

        private bool IsFallbackCandidate(
            RoomNode node,
            IRoomTemplateDescriptor room,
            IMissionConfigView config,
            EffectiveRoomWeightResolver resolver)
        {
            return room != null
                   && MatchesKind(node, room, config)
                   && MatchesRoleFlags(node, room)
                   && HasEnoughConnectableDoors(
                       room,
                       node.RequiredDoorCount,
                       true)
                   && ResolveSelectionWeight(node, room, resolver) > 0;
        }

        private bool MatchesKind(RoomNode node, IRoomTemplateDescriptor room, IMissionConfigView config)
        {
            if (room.Kind == node.Kind)
            {
                return true;
            }

            if (node.Kind == RoomKind.Start && room.CanBeStart)
            {
                return true;
            }

            if (node.Kind == RoomKind.Exit && room.CanBeExit)
            {
                return true;
            }

            if (node.Kind == RoomKind.Interactive
                && InteractiveMissionContentMatcher.IsInteractiveRoom(_interactiveRoomKinds, room))
            {
                return true;
            }

            // Dead-end graph nodes are authored as Passage+IsDeadEnd (the graph never uses the DeadEnd kind),
            // so a dedicated DeadEnd room template must be accepted for any dead-end slot — that is exactly
            // what caps MazeBranch wrong-answer doors and bonus branches.
            if (node.IsDeadEnd && room.Kind == RoomKind.DeadEnd)
            {
                return true;
            }

            return false;
        }

        private static bool MatchesRoleFlags(RoomNode node, IRoomTemplateDescriptor room)
        {
            if (node.Kind == RoomKind.Start && !room.CanBeStart)
            {
                return false;
            }

            if (node.Kind == RoomKind.Exit && !room.CanBeExit)
            {
                return false;
            }

            if (node.IsDeadEnd && !room.CanBeDeadEnd && ConnectableDoorCount(room) != 1)
            {
                return false;
            }

            if (node.IsOnMainPath && !room.CanBeMainPath)
            {
                return false;
            }

            if (!node.IsOnMainPath && !room.CanBeSideBranch)
            {
                return false;
            }

            return true;
        }

        private static EffectiveRoomWeightResolver BuildWeightResolver(IMissionConfigView config)
        {
            if (config == null)
            {
                return new EffectiveRoomWeightResolver();
            }

            Dictionary<string, int> missionRoomOverrides = null;
            if (config.MissionRoomRules != null && config.MissionRoomRules.Count > 0)
            {
                missionRoomOverrides = new Dictionary<string, int>(config.MissionRoomRules.Count);
                for (int i = 0; i < config.MissionRoomRules.Count; i++)
                {
                    IMissionRoomRule rule = config.MissionRoomRules[i];
                    if (rule == null || string.IsNullOrEmpty(rule.RoomId))
                    {
                        continue;
                    }

                    missionRoomOverrides[rule.RoomId] = rule.OverrideWeight ? rule.Weight : 100;
                }
            }

            if (config.RoomWeightOverrides != null && config.RoomWeightOverrides.Count > 0)
            {
                missionRoomOverrides ??= new Dictionary<string, int>(config.RoomWeightOverrides.Count);
                for (int i = 0; i < config.RoomWeightOverrides.Count; i++)
                {
                    IRoomWeightOverride o = config.RoomWeightOverrides[i];
                    if (o == null || string.IsNullOrEmpty(o.RoomId))
                    {
                        continue;
                    }

                    missionRoomOverrides[o.RoomId] = o.Weight;
                }
            }

            Dictionary<RoomKind, int> missionKindOverrides = null;
            if (config.RoomKindWeightOverrides != null && config.RoomKindWeightOverrides.Count > 0)
            {
                missionKindOverrides = new Dictionary<RoomKind, int>(config.RoomKindWeightOverrides.Count);
                for (int i = 0; i < config.RoomKindWeightOverrides.Count; i++)
                {
                    IRoomKindWeightOverride o = config.RoomKindWeightOverrides[i];
                    if (o == null)
                    {
                        continue;
                    }

                    missionKindOverrides[o.Kind] = o.Weight;
                }
            }

            return new EffectiveRoomWeightResolver(missionRoomOverrides, missionKindOverrides);
        }
    }
}
