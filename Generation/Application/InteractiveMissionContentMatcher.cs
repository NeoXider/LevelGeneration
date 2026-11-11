using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Matches mission interactive content with room templates. Every kind-dependent answer comes from
    /// <see cref="IInteractiveRoomKindRules"/> (the host game's room type registry); this class only
    /// resolves which rule applies and never compares against a concrete kind.
    /// </summary>
    public static class InteractiveMissionContentMatcher
    {
        public static bool TryFindContentSpec(
            IMissionConfigView config,
            string contentId,
            out IInteractiveMissionContentSpec spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(contentId))
            {
                return false;
            }

            IReadOnlyList<IInteractiveMissionContentSpec> content =
                config is IPlannedInteractiveMissionContentProvider planned
                    ? planned.PlannedInteractiveContent
                    : config is IInteractiveMissionContentProvider provider
                        ? provider.InteractiveContent
                        : null;
            if (content == null)
            {
                return false;
            }

            for (int i = 0; i < content.Count; i++)
            {
                IInteractiveMissionContentSpec candidate = content[i];
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(candidate.ContentId, contentId, StringComparison.Ordinal))
                {
                    spec = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The room type of a content item, or <see langword="false"/> when its kind is unknown.</summary>
        public static bool TryResolveContentRule(
            IInteractiveRoomKindRules rules,
            IInteractiveMissionContentSpec content,
            out IInteractiveRoomKindRule rule)
        {
            rule = null;
            return rules != null && content != null && rules.TryResolve(content.InteractiveKind, out rule);
        }

        /// <summary>
        /// The room type a template hosts: its declared interactive kind (canonical, then legacy spellings),
        /// then the type implied by its room module's type and asset name (rooms authored before kinds were
        /// declared). Resolved once per room template by <see cref="IInteractiveRoomKindRules.TryResolveRoom"/>.
        /// </summary>
        public static bool TryResolveRoomRule(
            IInteractiveRoomKindRules rules,
            IRoomTemplateDescriptor room,
            out IInteractiveRoomKindRule rule)
        {
            rule = null;
            return rules != null && room != null && rules.TryResolveRoom(room, out rule);
        }

        /// <summary>Canonical interactive kind of a room template, or <see langword="null"/>.</summary>
        public static string ResolveInteractiveKind(IInteractiveRoomKindRules rules, IRoomTemplateDescriptor room) =>
            TryResolveRoomRule(rules, room, out IInteractiveRoomKindRule rule) ? rule.Kind : null;

        public static bool IsInteractiveRoom(IInteractiveRoomKindRules rules, IRoomTemplateDescriptor room) =>
            TryResolveRoomRule(rules, room, out _);

        public static bool IsCompatible(
            IInteractiveRoomKindRules rules,
            IRoomTemplateDescriptor room,
            IInteractiveMissionContentSpec content)
        {
            if (room == null || content == null || string.IsNullOrWhiteSpace(content.ContentId))
            {
                return false;
            }

            if (!TryResolveContentRule(rules, content, out IInteractiveRoomKindRule contentRule)
                || !TryResolveRoomRule(rules, room, out IInteractiveRoomKindRule roomRule)
                || !string.Equals(contentRule.Kind, roomRule.Kind, StringComparison.Ordinal))
            {
                return false;
            }

            return contentRule.IsCompatible(content, ResolveInteractiveOptionCapacity(room));
        }

        /// <summary>Whether the room's answers are its locked doors (answer branches in the graph).</summary>
        public static bool HasDoorPerOptionAnswers(IInteractiveRoomKindRules rules, IRoomTemplateDescriptor room) =>
            TryResolveRoomRule(rules, room, out IInteractiveRoomKindRule rule)
            && rule.AnswerLayout == InteractiveRoomAnswerLayout.DoorPerOption;

        /// <summary>
        /// Option count of content whose answers become door branches (one correct door continues the path,
        /// every wrong door leads into a dead end); <see langword="false"/> for any other content.
        /// </summary>
        public static bool TryResolveDoorBranchOptionCount(
            IInteractiveRoomKindRules rules,
            IInteractiveMissionContentSpec content,
            out int optionCount)
        {
            optionCount = 0;
            if (!TryResolveContentRule(rules, content, out IInteractiveRoomKindRule rule)
                || rule.AnswerLayout != InteractiveRoomAnswerLayout.DoorPerOption
                || !rule.SupportsContent(content))
            {
                return false;
            }

            optionCount = content.OptionCount;
            return true;
        }

        /// <summary>Placement hints of the content's room type; <see cref="InteractiveRoomGenerationHints.None"/> when unknown.</summary>
        public static InteractiveRoomGenerationHints ResolveHints(
            IInteractiveRoomKindRules rules,
            IInteractiveMissionContentSpec content) =>
            TryResolveContentRule(rules, content, out IInteractiveRoomKindRule rule)
                ? rule.Hints
                : InteractiveRoomGenerationHints.None;

        public static int ResolveInteractiveOptionCapacity(IRoomTemplateDescriptor room)
        {
            int lockedDoorCount = CountLockedTargetDoors(room);
            if (lockedDoorCount > 0)
            {
                return lockedDoorCount;
            }

            if ((room?.Doors == null || room.Doors.Count == 0)
                && room is IInteractiveRoomTemplateDescriptor descriptor)
            {
                return Math.Max(0, descriptor.InteractiveOptionCapacity);
            }

            return 0;
        }

        public static string NormalizeKind(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            char[] buffer = new char[value.Length];
            int length = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c))
                {
                    buffer[length++] = char.ToLowerInvariant(c);
                }
            }

            return new string(buffer, 0, length);
        }

        private static int CountLockedTargetDoors(IRoomTemplateDescriptor room)
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
    }
}
