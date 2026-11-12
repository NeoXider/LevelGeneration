using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    [Category("LevelGeneration")]
    public sealed class RoomSelectorTests
    {
        [Test]
        public void Select_FiltersByRoomTypeAndMainPathFlag()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("corridor_side_only", RoomKind.Passage, canBeMainPath: false), new FakeRoomTemplate("corridor_main", RoomKind.Passage), new FakeRoomTemplate("objective", RoomKind.Interactive) });
            RoomNode node = new("n_corridor", RoomKind.Passage, 2);

            IRoomTemplateDescriptor selected = selector.Select(node, new FakeMissionConfig(), new FakeTileset(),
                database, new FirstRollRng());

            Assert.AreEqual("corridor_main", selected.RoomId);
        }

        [Test]
        public void Select_HonorsMissionRoomWeightOverrideZero()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("corridor_a", RoomKind.Passage), new FakeRoomTemplate("corridor_b", RoomKind.Passage) });
            WeightOverrideMissionConfig config = new(new[] { new RoomWeight("corridor_a", 0) });
            RoomNode node = new("n_corridor", RoomKind.Passage, 2);

            IRoomTemplateDescriptor selected =
                selector.Select(node, config, new FakeTileset(), database, new FirstRollRng());

            Assert.AreEqual("corridor_b", selected.RoomId);
        }

        [Test]
        public void Select_PrefersExactRequiredDoorCountWhenAvailable()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("junction_tunnel", RoomKind.Passage, 2), new FakeRoomTemplate("junction_splitter", RoomKind.Passage, 3), new FakeRoomTemplate("junction_hub", RoomKind.Passage, 3) });
            RoomNode node = new("n_junction", RoomKind.Passage, 3);

            IRoomTemplateDescriptor selected = selector.Select(node, new FakeMissionConfig(), new FakeTileset(),
                database, new FirstRollRng());

            Assert.AreEqual("junction_hub", selected.RoomId);
        }

        [Test]
        public void Select_UsesMissionRoomPoolWhenProvided()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("corridor_not_in_mission", RoomKind.Passage), new FakeRoomTemplate("corridor_in_mission", RoomKind.Passage) });
            MissionRoomPoolConfig config = new(new[] { new MissionRoom("corridor_in_mission", RoomKind.Passage) });
            RoomNode node = new("n_corridor", RoomKind.Passage, 2);

            IRoomTemplateDescriptor selected =
                selector.Select(node, config, new FakeTileset(), database, new FirstRollRng());

            Assert.AreEqual("corridor_in_mission", selected.RoomId);
        }

        [Test]
        public void Select_PreferredTemplateNodeForcesConcreteRoom()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("corridor_a", RoomKind.Passage), new FakeRoomTemplate("corridor_b", RoomKind.Passage) });
            RoomNode node = new(
                "n_corridor",
                RoomKind.Passage,
                2,
                preferredRoomTemplateId: "corridor_b");

            IRoomTemplateDescriptor selected = selector.Select(node, new FakeMissionConfig(), new FakeTileset(),
                database, new FirstRollRng());

            Assert.AreEqual("corridor_b", selected.RoomId);
        }

        [Test]
        public void Select_PreferredTemplateCanBypassMissionPoolForHybridAnchor()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("corridor_in_pool", RoomKind.Passage), new FakeRoomTemplate("corridor_sequence_anchor", RoomKind.Passage) });
            MissionRoomPoolConfig config = new(new[] { new MissionRoom("corridor_in_pool", RoomKind.Passage) });
            RoomNode node = new(
                "n_corridor",
                RoomKind.Passage,
                2,
                preferredRoomTemplateId: "corridor_sequence_anchor");

            IRoomTemplateDescriptor selected =
                selector.Select(node, config, new FakeTileset(), database, new FirstRollRng());

            Assert.AreEqual("corridor_sequence_anchor", selected.RoomId);
        }

        [Test]
        public void Select_UsesCompatibleTopology_WhenExactMatchMissing()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("junction_hub", RoomKind.Passage, 3), new FakeRoomTemplate("junction_lshape", RoomKind.Passage, 2), new FakeRoomTemplate("junction_tunnel", RoomKind.Passage, 2) });
            RoomNode node = new("n_junction", RoomKind.Passage, 3);

            IRoomTemplateDescriptor selected = selector.Select(node, new FakeMissionConfig(), new FakeTileset(),
                database, new FirstRollRng());

            Assert.AreEqual("junction_hub", selected.RoomId);
        }

        [Test]
        public void Select_BiasesMultiDoorTopologyForNonDeadEndPassage()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("corridor_dead_end", RoomKind.Passage, 1), new FakeRoomTemplate("corridor_lshape", RoomKind.Passage, 2), new FakeRoomTemplate("corridor_hub", RoomKind.Passage, 3) });
            RoomNode node = new("n_corridor", RoomKind.Passage, 2);

            IRoomTemplateDescriptor selected = selector.Select(node, new FakeMissionConfig(), new FakeTileset(),
                database, new FirstRollRng());

            Assert.AreEqual("corridor_hub", selected.RoomId);
        }

        [Test]
        public void Select_NonDeadEndPassage_DoesNotForceMinimalConnectableDoorCount()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("z_tunnel", RoomKind.Passage, 1), new FakeRoomTemplate("a_terminal", RoomKind.Passage, 3) });
            RoomNode node = new("n_corridor", RoomKind.Passage, 1);

            IRoomTemplateDescriptor selected =
                selector.Select(node, new FakeMissionConfig(), new FakeTileset(), database, new FirstRollRng());

            Assert.AreEqual("a_terminal", selected.RoomId);
        }

        [Test]
        public void Select_PrefersExactDoorCountForDeadEndPassage()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("corridor_split", RoomKind.Passage, 2), new FakeRoomTemplate("corridor_hub", RoomKind.Passage, 3) });
            RoomNode node = new("n_dead_end", RoomKind.Passage, 2, isDeadEnd: true);

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new FakeMissionConfig(),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("corridor_split", selected.RoomId);
        }

        [Test]
        public void Select_DeadEndPassage_AllowsOneDoorTemplateWithoutDeadEndFlag()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate("corridor_two_doors", RoomKind.Passage, 2), new FakeRoomTemplate(
                    "corridor_one_door",
                    RoomKind.Passage,
                    1,
                    canBeDeadEnd: false)
            });
            RoomNode node = new("n_dead_end", RoomKind.Passage, 1, isDeadEnd: true);

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new FakeMissionConfig(),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("corridor_one_door", selected.RoomId);
        }

        [Test]
        public void Select_AllowsTemplatesWithoutDoorMetadata_AsCompatibilityFallback()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "legacy_passage_without_doors",
                    RoomKind.Passage,
                    doors: System.Array.Empty<IDoorSocketDescriptor>())
            });
            RoomNode node = new("n_junction", RoomKind.Passage, 3);

            IRoomTemplateDescriptor selected = selector.Select(node, new FakeMissionConfig(), new FakeTileset(),
                database, new FirstRollRng());

            Assert.AreEqual("legacy_passage_without_doors", selected.RoomId);
        }

        [Test]
        public void Select_ForQuizBranchPrefersDoorCountMatchingAnswerCount()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "junction_two_doors",
                    RoomKind.Passage,
                    3,
                    doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("east"), new FakeDoorSocket("north") }),
                new FakeRoomTemplate(
                    "junction_three_doors",
                    RoomKind.Passage,
                    3,
                    doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("east"), new FakeDoorSocket("north"), new FakeDoorSocket("west") })
            });
            RoomNode node = new("n_junction", RoomKind.Passage, 3);

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new QuizBranchMissionConfig(3),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("junction_three_doors", selected.RoomId);
        }

        [Test]
        public void Select_InteractiveContentIdFiltersConcreteInteractiveKind()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_password",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.PasswordDoor,
                    doors: LockedDoors(1)),
                new FakeRoomTemplate(
                    "room_lever",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.LeverPanel,
                    doors: LockedDoors(1))
            });
            RoomNode node = new(
                "n_interactive",
                RoomKind.Interactive,
                contentId: "Q_LEVER");

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new InteractiveContentMissionConfig(new InteractiveContent("Q_LEVER",
                    TestInteractiveRoomKinds.LeverPanel, 4, 2)),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("room_lever", selected.RoomId);
        }

        [Test]
        public void Select_MazeBranchContentRequiresMatchingAnswerDoorCapacity()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_maze_two_answers",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    doors: LockedDoors(2)),
                new FakeRoomTemplate(
                    "room_maze_four_answers",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    doors: LockedDoors(4))
            });
            RoomNode node = new(
                "n_interactive",
                RoomKind.Interactive,
                contentId: "Q_MAZE_4");

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new InteractiveContentMissionConfig(new InteractiveContent("Q_MAZE_4",
                    TestInteractiveRoomKinds.MazeBranch, 4, 1)),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("room_maze_four_answers", selected.RoomId);
        }

        [Test]
        public void Select_InteractiveContentIgnoresIncompatiblePreferredTemplate()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_maze_two_answers",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    doors: LockedDoors(2)),
                new FakeRoomTemplate(
                    "room_maze_four_answers",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    doors: LockedDoors(4))
            });
            RoomNode node = new(
                "n_interactive",
                RoomKind.Interactive,
                1,
                contentId: "Q_MAZE_2",
                preferredRoomTemplateId: "room_maze_four_answers");

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new InteractiveContentMissionConfig(new InteractiveContent("Q_MAZE_2",
                    TestInteractiveRoomKinds.MazeBranch, 2, 1)),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("room_maze_two_answers", selected.RoomId);
        }

        [Test]
        public void Select_PasswordContentUsesModuleNamedRoomWithLockedTargetDoor()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_password_module",
                    RoomKind.Interactive,
                    moduleAssetId: "PasswordDoorModuleSO",
                    doors: LockedDoors(1)),
                new FakeRoomTemplate(
                    "room_maze",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    doors: LockedDoors(2))
            });
            RoomNode node = new(
                "n_interactive",
                RoomKind.Interactive,
                contentId: "Q_PASSWORD");

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new InteractiveContentMissionConfig(new InteractiveContent("Q_PASSWORD",
                    TestInteractiveRoomKinds.PasswordDoor, 1, 1)),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.AreEqual("room_password_module", selected.RoomId);
        }

        [Test]
        public void Select_InteractiveContentIdWithoutSpec_ReturnsNullInsteadOfGenericInteractive()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_generic_interactive",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    doors: LockedDoors(2))
            });
            RoomNode node = new(
                "n_interactive",
                RoomKind.Interactive,
                contentId: "Q_MISSING");

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new InteractiveContentMissionConfig(),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.IsNull(selected);
        }

        [Test]
        public void Select_InteractiveContentIdWithoutCompatibleRoom_ReturnsNullInsteadOfGenericInteractive()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_maze",
                    RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    doors: LockedDoors(2))
            });
            RoomNode node = new(
                "n_interactive",
                RoomKind.Interactive,
                contentId: "Q_PASSWORD");

            IRoomTemplateDescriptor selected = selector.Select(
                node,
                new InteractiveContentMissionConfig(new InteractiveContent("Q_PASSWORD",
                    TestInteractiveRoomKinds.PasswordDoor, 1, 1)),
                new FakeTileset(),
                database,
                new FirstRollRng());

            Assert.IsNull(selected);
        }

        private static IReadOnlyList<IDoorSocketDescriptor> LockedDoors(int count)
        {
            IDoorSocketDescriptor[] doors = new IDoorSocketDescriptor[count];
            for (int i = 0; i < doors.Length; i++)
            {
                doors[i] = new FakeDoorSocket($"locked_{i}", initialState: DoorState.Locked);
            }

            return doors;
        }

        private sealed class WeightOverrideMissionConfig : FakeMissionConfig
        {
            private readonly IReadOnlyList<IRoomWeightOverride> _overrides;

            public WeightOverrideMissionConfig(IReadOnlyList<IRoomWeightOverride> overrides)
            {
                _overrides = overrides;
            }

            public override IReadOnlyList<IRoomWeightOverride> RoomWeightOverrides => _overrides;
        }

        private sealed class RoomWeight : IRoomWeightOverride
        {
            public RoomWeight(string roomId, int weight)
            {
                RoomId = roomId;
                Weight = weight;
            }

            public string RoomId { get; }
            public int Weight { get; }
        }

        private sealed class MissionRoomPoolConfig : FakeMissionConfig
        {
            private readonly IReadOnlyList<IMissionRoomRule> _rules;

            public MissionRoomPoolConfig(IReadOnlyList<IMissionRoomRule> rules)
            {
                _rules = rules;
            }

            public override IReadOnlyList<IMissionRoomRule> MissionRoomRules => _rules;
        }

        private sealed class MissionRoom : IMissionRoomRule
        {
            public MissionRoom(string roomId, RoomKind kind)
            {
                RoomId = roomId;
                Kind = kind;
            }

            public string RoomId { get; }
            public RoomKind Kind { get; }
            public bool HasSpawnRule => false;
            public RoomTypeSpawnMode Mode => RoomTypeSpawnMode.Chance;
            public bool OverrideChance => false;
            public float Chance => 0f;
            public OptionalIntRange CountRange => default;
            public bool OverrideWeight => false;
            public int Weight => 100;
        }

        private sealed class QuizBranchMissionConfig : FakeMissionConfig, IQuizBranchMissionConfigView
        {
            public QuizBranchMissionConfig(int answerCount)
            {
                QuizBranchAnswerCount = answerCount;
            }

            public int QuizBranchAnswerCount { get; }
        }

        private sealed class InteractiveContentMissionConfig : FakeMissionConfig, IInteractiveMissionContentProvider
        {
            public InteractiveContentMissionConfig(params IInteractiveMissionContentSpec[] content)
            {
                InteractiveContent = content;
            }

            public IReadOnlyList<IInteractiveMissionContentSpec> InteractiveContent { get; }
        }

        private sealed class InteractiveContent : IInteractiveMissionContentSpec
        {
            public InteractiveContent(string contentId, string interactiveKind, int optionCount, int correctAnswerCount)
            {
                ContentId = contentId;
                InteractiveKind = interactiveKind;
                OptionCount = optionCount;
                CorrectAnswerCount = correctAnswerCount;
            }

            public string ContentId { get; }
            public string InteractiveKind { get; }
            public int OptionCount { get; }
            public int CorrectAnswerCount { get; }
            public bool IsMandatory => true;
        }
    }
}
