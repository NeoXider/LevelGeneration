using Neoxider.LevelGeneration.Generation.Application;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    /// <summary>
    /// Interactive room types for the generator tests. The level-generation package knows no concrete kind:
    /// the host game registers them (see the game's interactive room type descriptors). These rules mirror
    /// the registered room types of the game so the pipeline tests keep exercising door-per-option
    /// branches and locked-exit rooms with the same placement hints. The game's EditMode suite compares them
    /// with the production descriptors (<c>InteractiveRoomKindRulesParityTests</c>), so a drift fails there.
    /// </summary>
    public static class TestInteractiveRoomKinds
    {
        public const string MazeBranch = "MazeBranch";
        public const string LeverPanel = "LeverPanel";
        public const string PasswordDoor = "PasswordDoor";
        public const string ShootingGallery = "ShootingGallery";

        /// <summary>Neutral templates of the test tileset for the rooms the generator inserts itself.</summary>
        public static readonly Neoxider.LevelGeneration.Generation.Application.Pipeline.RoomTemplateRoles Roles =
            new("passage_2doors", "hub_3doors", "dead_end_1door");

        public static readonly IInteractiveRoomKindRules Standard = new InteractiveRoomKindRules(
            new IInteractiveRoomKindRule[]
            {
                new InteractiveRoomKindRule(
                    MazeBranch,
                    InteractiveRoomAnswerLayout.DoorPerOption,
                    legacyKindTokens: new[] { "quizbranch", "mazebranch" },
                    moduleNameTokens: new[] { "quizbranch" },
                    legacyMatchPriority: 0,
                    hints: new InteractiveRoomGenerationHints(
                        preferredRoomTemplateId: "maze_room_4doors",
                        preferredSpacerRoomTemplateIdBefore: "hub_3doors",
                        branchPassageRoomTemplateIdAfter: "passage_2doors",
                        placeLastAmongMainPathContent: true)),
                new InteractiveRoomKindRule(
                    LeverPanel,
                    InteractiveRoomAnswerLayout.LockedExit,
                    minCorrectAnswers: 2,
                    legacyKindTokens: new[] { "lever" },
                    moduleNameTokens: new[] { "lever" },
                    legacyMatchPriority: 1),
                new InteractiveRoomKindRule(
                    PasswordDoor,
                    InteractiveRoomAnswerLayout.LockedExit,
                    minCorrectAnswers: 1,
                    legacyKindTokens: new[] { "password" },
                    moduleNameTokens: new[] { "password" },
                    legacyMatchPriority: 2,
                    hints: new InteractiveRoomGenerationHints(
                        preferredPassageRoomTemplateIdAfter: "hub_3doors")),
                new InteractiveRoomKindRule(
                    ShootingGallery,
                    InteractiveRoomAnswerLayout.LockedExit,
                    minCorrectAnswers: 0,
                    requiresTemplate: true)
            });
    }
}
