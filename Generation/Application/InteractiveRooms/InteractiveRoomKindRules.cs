using System;
using System.Collections.Generic;

namespace Neoxider.LevelGeneration.Generation.Application
{
    /// <summary>
    /// The interactive room types known to the generator: the generation-facing view of the host game's
    /// room type registry. Kind resolution, room ↔ content compatibility and graph placement are asked
    /// here instead of being switched on a concrete kind inside the pipeline.
    /// </summary>
    public interface IInteractiveRoomKindRules
    {
        /// <summary>All known types, ordered by <see cref="IInteractiveRoomKindRule.Kind"/> (ordinal).</summary>
        public IReadOnlyList<IInteractiveRoomKindRule> Rules { get; }

        /// <summary>
        /// Resolves a CONTENT kind: the exact canonical id, case-insensitive, surrounding spaces ignored (the
        /// rule of the content reader and the backend). No legacy spellings.
        /// </summary>
        public bool TryResolve(string kind, out IInteractiveRoomKindRule rule);

        /// <summary>
        /// The room type a ROOM TEMPLATE hosts: its declared kind (canonical id, then legacy spellings in
        /// <see cref="IInteractiveRoomKindRule.LegacyMatchPriority"/> order), then the type implied by its room
        /// module's type and asset name. Resolved once per room template (cached by room id).
        /// </summary>
        public bool TryResolveRoom(IRoomTemplateDescriptor room, out IInteractiveRoomKindRule rule);
    }

    /// <summary>Immutable <see cref="IInteractiveRoomKindRules"/> over a fixed set of rules.</summary>
    public sealed class InteractiveRoomKindRules : IInteractiveRoomKindRules
    {
        /// <summary>No interactive room types: every interactive content item is reported as unknown.</summary>
        public static readonly InteractiveRoomKindRules Empty = new(Array.Empty<IInteractiveRoomKindRule>());

        private readonly IInteractiveRoomKindRule[] _rules;
        private readonly IInteractiveRoomKindRule[] _legacyOrder;
        private readonly Dictionary<string, IInteractiveRoomKindRule> _byKind;
        private readonly Dictionary<string, RoomResolution> _roomCache = new(StringComparer.Ordinal);

        public InteractiveRoomKindRules(IEnumerable<IInteractiveRoomKindRule> rules)
        {
            List<IInteractiveRoomKindRule> ordered = new();
            _byKind = new Dictionary<string, IInteractiveRoomKindRule>(StringComparer.OrdinalIgnoreCase);
            if (rules != null)
            {
                foreach (IInteractiveRoomKindRule rule in rules)
                {
                    if (rule == null)
                    {
                        continue;
                    }

                    string kind = rule.Kind?.Trim();
                    if (string.IsNullOrEmpty(kind))
                    {
                        throw new ArgumentException("Interactive room rule has an empty kind.", nameof(rules));
                    }

                    if (_byKind.ContainsKey(kind))
                    {
                        throw new ArgumentException(
                            $"Interactive room kind '{rule.Kind}' is registered twice.", nameof(rules));
                    }

                    _byKind.Add(kind, rule);
                    ordered.Add(rule);
                }
            }

            ordered.Sort((left, right) => string.CompareOrdinal(left.Kind, right.Kind));
            _rules = ordered.ToArray();
            List<IInteractiveRoomKindRule> legacyOrder = new(ordered);
            legacyOrder.Sort((left, right) =>
            {
                int byPriority = left.LegacyMatchPriority.CompareTo(right.LegacyMatchPriority);
                return byPriority != 0 ? byPriority : string.CompareOrdinal(left.Kind, right.Kind);
            });
            _legacyOrder = legacyOrder.ToArray();
        }

        public IReadOnlyList<IInteractiveRoomKindRule> Rules => _rules;

        public bool TryResolve(string kind, out IInteractiveRoomKindRule rule)
        {
            rule = null;
            if (string.IsNullOrWhiteSpace(kind))
            {
                return false;
            }

            return _byKind.TryGetValue(kind, out rule) || _byKind.TryGetValue(kind.Trim(), out rule);
        }

        public bool TryResolveRoom(IRoomTemplateDescriptor room, out IInteractiveRoomKindRule rule)
        {
            rule = null;
            if (room == null || _rules.Length == 0)
            {
                return false;
            }

            IInteractiveRoomTemplateDescriptor interactive = room as IInteractiveRoomTemplateDescriptor;
            string declaredKind = interactive?.InteractiveKind;
            string moduleHint = interactive?.InteractiveModuleHint ?? room.ModuleAssetId;
            string roomId = room.RoomId;
            if (string.IsNullOrEmpty(roomId))
            {
                rule = ResolveRoom(declaredKind, moduleHint);
                return rule != null;
            }

            lock (_roomCache)
            {
                if (_roomCache.TryGetValue(roomId, out RoomResolution cached)
                    && string.Equals(cached.DeclaredKind, declaredKind, StringComparison.Ordinal)
                    && string.Equals(cached.ModuleHint, moduleHint, StringComparison.Ordinal))
                {
                    rule = cached.Rule;
                    return rule != null;
                }
            }

            rule = ResolveRoom(declaredKind, moduleHint);
            lock (_roomCache)
            {
                _roomCache[roomId] = new RoomResolution(declaredKind, moduleHint, rule);
            }

            return rule != null;
        }

        private IInteractiveRoomKindRule ResolveRoom(string declaredKind, string moduleHint)
        {
            if (TryResolve(declaredKind, out IInteractiveRoomKindRule declared))
            {
                return declared;
            }

            string normalizedKind = InteractiveMissionContentMatcher.NormalizeKind(declaredKind);
            if (!string.IsNullOrEmpty(normalizedKind))
            {
                for (int i = 0; i < _legacyOrder.Length; i++)
                {
                    if (_legacyOrder[i].MatchesLegacyKind(normalizedKind))
                    {
                        return _legacyOrder[i];
                    }
                }
            }

            string normalizedModule = InteractiveMissionContentMatcher.NormalizeKind(moduleHint);
            if (!string.IsNullOrEmpty(normalizedModule))
            {
                for (int i = 0; i < _legacyOrder.Length; i++)
                {
                    if (_legacyOrder[i].MatchesModuleName(normalizedModule))
                    {
                        return _legacyOrder[i];
                    }
                }
            }

            return null;
        }

        private readonly struct RoomResolution
        {
            public RoomResolution(string declaredKind, string moduleHint, IInteractiveRoomKindRule rule)
            {
                DeclaredKind = declaredKind;
                ModuleHint = moduleHint;
                Rule = rule;
            }

            public string DeclaredKind { get; }
            public string ModuleHint { get; }
            public IInteractiveRoomKindRule Rule { get; }
        }
    }
}
