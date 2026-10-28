using System;
using System.Collections.Generic;

namespace RedoSchool.Features.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Data-driven <see cref="IInteractiveRoomKindRule"/> for question-shaped room types (a question with
    /// answer options and correct answers). A room type with other generation semantics implements
    /// <see cref="IInteractiveRoomKindRule"/> directly.
    /// <list type="bullet">
    /// <item><see cref="InteractiveRoomAnswerLayout.DoorPerOption"/>: exactly one correct answer; the room hosts
    /// the content only when its locked answer doors equal the option count; planning needs at least two
    /// options.</item>
    /// <item><see cref="InteractiveRoomAnswerLayout.LockedExit"/>: at least <c>minCorrectAnswers</c> correct
    /// answers and at least one locked exit door.</item>
    /// </list>
    /// </summary>
    public sealed class InteractiveRoomKindRule : IInteractiveRoomKindRule
    {
        private readonly string[] _legacyKindTokens;
        private readonly string[] _moduleNameTokens;
        private readonly int _minCorrectAnswers;

        /// <param name="kind">Canonical content kind.</param>
        /// <param name="answerLayout">How answers map onto the room's locked doors.</param>
        /// <param name="minCorrectAnswers">Minimum correct answers for <see cref="InteractiveRoomAnswerLayout.LockedExit"/>.</param>
        /// <param name="legacyKindTokens">
        /// Tokens of legacy room-template spellings: a declared room kind that is not a canonical id and
        /// CONTAINS one of them (normalized) names this type. Content kinds never match by token.
        /// </param>
        /// <param name="moduleNameTokens">
        /// Tokens of room-module type/asset names that imply this type for rooms without a declared kind.
        /// </param>
        /// <param name="legacyMatchPriority">Order among types when tokens are tried (lower first).</param>
        /// <param name="hints">Placement preferences in the mission graph.</param>
        /// <param name="requiresTemplate">Fail on a missing template even when the authored content is optional.</param>
        public InteractiveRoomKindRule(
            string kind,
            InteractiveRoomAnswerLayout answerLayout,
            int minCorrectAnswers = 1,
            IEnumerable<string> legacyKindTokens = null,
            IEnumerable<string> moduleNameTokens = null,
            int legacyMatchPriority = int.MaxValue,
            InteractiveRoomGenerationHints hints = null,
            bool requiresTemplate = false)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new ArgumentException("Interactive room kind must not be empty.", nameof(kind));
            }

            Kind = kind.Trim();
            AnswerLayout = answerLayout;
            _minCorrectAnswers = Math.Max(0, minCorrectAnswers);
            _legacyKindTokens = NormalizeTokens(legacyKindTokens);
            LegacyMatchPriority = legacyMatchPriority;
            _moduleNameTokens = NormalizeTokens(moduleNameTokens);
            Hints = hints ?? InteractiveRoomGenerationHints.None;
            RequiresTemplate = requiresTemplate;
        }

        public string Kind { get; }
        public InteractiveRoomAnswerLayout AnswerLayout { get; }
        public bool RequiresTemplate { get; }
        public InteractiveRoomGenerationHints Hints { get; }
        public int LegacyMatchPriority { get; }

        public bool MatchesLegacyKind(string normalizedKind) =>
            !string.IsNullOrEmpty(normalizedKind) && ContainsAnyToken(normalizedKind, _legacyKindTokens);

        public bool MatchesModuleName(string normalizedModuleName) =>
            !string.IsNullOrEmpty(normalizedModuleName) && ContainsAnyToken(normalizedModuleName, _moduleNameTokens);

        public bool SupportsContent(IInteractiveMissionContentSpec content)
        {
            if (content == null)
            {
                return false;
            }

            return AnswerLayout != InteractiveRoomAnswerLayout.DoorPerOption
                   || (content.CorrectAnswerCount == 1 && content.OptionCount > 1);
        }

        public bool IsCompatible(IInteractiveMissionContentSpec content, int roomOptionCapacity)
        {
            if (content == null)
            {
                return false;
            }

            if (AnswerLayout == InteractiveRoomAnswerLayout.DoorPerOption)
            {
                return content.CorrectAnswerCount == 1
                       && content.OptionCount > 0
                       && roomOptionCapacity == content.OptionCount;
            }

            return content.CorrectAnswerCount >= _minCorrectAnswers && roomOptionCapacity >= 1;
        }

        private static bool ContainsAnyToken(string normalizedValue, string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (normalizedValue.Contains(tokens[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] NormalizeTokens(IEnumerable<string> tokens)
        {
            if (tokens == null)
            {
                return Array.Empty<string>();
            }

            List<string> result = new();
            foreach (string token in tokens)
            {
                string normalized = InteractiveMissionContentMatcher.NormalizeKind(token);
                if (!string.IsNullOrEmpty(normalized) && !result.Contains(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result.ToArray();
        }
    }
}
