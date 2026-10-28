namespace RedoSchool.Features.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Placement preferences of one interactive room type in the mission graph. Every value is optional:
    /// an empty hint means «no preference», and the graph builders treat all types alike. The values are
    /// room template ids of the host tileset, so they belong to the room type descriptor, not to the
    /// pipeline.
    /// </summary>
    public sealed class InteractiveRoomGenerationHints
    {
        /// <summary>No placement preferences.</summary>
        public static readonly InteractiveRoomGenerationHints None = new();

        public InteractiveRoomGenerationHints(
            string preferredRoomTemplateId = null,
            string preferredSpacerRoomTemplateIdBefore = null,
            string preferredPassageRoomTemplateIdAfter = null,
            string branchPassageRoomTemplateIdAfter = null,
            bool placeLastAmongMainPathContent = false)
        {
            PreferredRoomTemplateId = Normalize(preferredRoomTemplateId);
            PreferredSpacerRoomTemplateIdBefore = Normalize(preferredSpacerRoomTemplateIdBefore);
            PreferredPassageRoomTemplateIdAfter = Normalize(preferredPassageRoomTemplateIdAfter);
            BranchPassageRoomTemplateIdAfter = Normalize(branchPassageRoomTemplateIdAfter);
            PlaceLastAmongMainPathContent = placeLastAmongMainPathContent;
        }

        /// <summary>Room template the legacy graph builder prefers for this content's node.</summary>
        public string PreferredRoomTemplateId { get; }

        /// <summary>Passage template placed right before this content on the main path.</summary>
        public string PreferredSpacerRoomTemplateIdBefore { get; }

        /// <summary>Passage template preferred right after this content on the main path.</summary>
        public string PreferredPassageRoomTemplateIdAfter { get; }

        /// <summary>Passage template appended after this content when it is placed on a side branch.</summary>
        public string BranchPassageRoomTemplateIdAfter { get; }

        /// <summary>Main-path content of this type is moved behind the other content kinds.</summary>
        public bool PlaceLastAmongMainPathContent { get; }

        private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
