namespace RedoSchool.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Generation-facing part of one interactive room type: which content kind it is, which room
    /// templates can host that content and how the graph places it. The host game owns the concrete
    /// rules (one per room type descriptor); the pipeline never compares against a concrete kind and
    /// asks the rule instead.
    /// </summary>
    public interface IInteractiveRoomKindRule
    {
        /// <summary>Canonical content kind: the same string the mission JSON and the backend use.</summary>
        public string Kind { get; }

        /// <summary>How the answers of this room type map onto the room's locked doors.</summary>
        public InteractiveRoomAnswerLayout AnswerLayout { get; }

        /// <summary>
        /// Missing a compatible room template is a generation defect even for optional content.
        /// Otherwise optional content may be omitted with a warning; mandatory content always requires a template.
        /// </summary>
        public bool RequiresTemplate { get; }

        /// <summary>Placement preferences of this room type in the mission graph.</summary>
        public InteractiveRoomGenerationHints Hints { get; }

        /// <summary>
        /// Order in which legacy spellings are tried when a room template's declared kind is not a canonical id
        /// (lower first): a legacy string or module name may contain tokens of several types.
        /// </summary>
        public int LegacyMatchPriority { get; }

        /// <summary>
        /// Whether a normalized legacy kind string of a ROOM TEMPLATE names this type (see
        /// <see cref="InteractiveMissionContentMatcher.NormalizeKind"/>): spellings still found in rooms authored
        /// before kinds were canonical. Content kinds never go through this — they resolve exactly.
        /// </summary>
        public bool MatchesLegacyKind(string normalizedKind);

        /// <summary>
        /// Whether a normalized room-module type/asset name implies this type for a room template that does
        /// not declare its interactive kind.
        /// </summary>
        public bool MatchesModuleName(string normalizedModuleName);

        /// <summary>Whether the content item can be planned into a level at all.</summary>
        public bool SupportsContent(IInteractiveMissionContentSpec content);

        /// <summary>
        /// Whether a room of this type that has <paramref name="roomOptionCapacity"/> locked answer doors can
        /// host <paramref name="content"/>.
        /// </summary>
        public bool IsCompatible(IInteractiveMissionContentSpec content, int roomOptionCapacity);
    }

    /// <summary>How the answers of an interactive room map onto its locked doors.</summary>
    public enum InteractiveRoomAnswerLayout
    {
        /// <summary>
        /// Answers are given inside the room; solving it opens the locked exit door(s).
        /// </summary>
        LockedExit = 0,

        /// <summary>
        /// Every answer option is its own locked door: the correct one continues the path, every wrong one
        /// leads into a dead end. The graph expands such content into answer branches.
        /// </summary>
        DoorPerOption = 1
    }
}
