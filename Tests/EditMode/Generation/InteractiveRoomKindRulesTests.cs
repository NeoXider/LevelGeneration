using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    /// <summary>
    /// How the generator resolves interactive room types: content kinds exactly (the content reader and the
    /// backend accept only canonical ids), room templates also by legacy spellings and by their module's type
    /// and asset name — in the old priority order — and once per room template.
    /// </summary>
    public sealed class InteractiveRoomKindRulesTests
    {
        [TestCase("MazeBranch", TestInteractiveRoomKinds.MazeBranch)]
        [TestCase(" leverpanel ", TestInteractiveRoomKinds.LeverPanel)]
        [TestCase("PASSWORDDOOR", TestInteractiveRoomKinds.PasswordDoor)]
        public void ContentKind_ResolvesByCanonicalIdIgnoringCaseAndSpaces(string kind, string expected)
        {
            Assert.That(TestInteractiveRoomKinds.Standard.TryResolve(kind, out IInteractiveRoomKindRule rule), Is.True);
            Assert.That(rule.Kind, Is.EqualTo(expected));
        }

        [TestCase("QuizBranch")]
        [TestCase("maze_branch")]
        [TestCase("Lever")]
        [TestCase("password-door")]
        public void ContentKind_LegacySpelling_IsUnknown(string kind)
        {
            Assert.That(TestInteractiveRoomKinds.Standard.TryResolve(kind, out _), Is.False,
                "Контент разбирается точно: вид, которого не знают бэкенд и разбор контента, генератор не угадывает.");
        }

        [TestCase("quiz_branch", TestInteractiveRoomKinds.MazeBranch)]
        [TestCase("Maze-Branch", TestInteractiveRoomKinds.MazeBranch)]
        [TestCase("LeverRoom", TestInteractiveRoomKinds.LeverPanel)]
        [TestCase("password_terminal", TestInteractiveRoomKinds.PasswordDoor)]
        [TestCase("lever_quizbranch", TestInteractiveRoomKinds.MazeBranch)]
        [TestCase("password_lever", TestInteractiveRoomKinds.LeverPanel)]
        public void RoomTemplate_LegacyDeclaredKind_ResolvesInTheOldPriorityOrder(string declared, string expected)
        {
            FakeRoom room = new("room_legacy_" + declared, declared, moduleHint: null);

            Assert.That(TestInteractiveRoomKinds.Standard.TryResolveRoom(room, out IInteractiveRoomKindRule rule), Is.True);
            Assert.That(rule.Kind, Is.EqualTo(expected),
                "Старые комнаты разбирались в порядке quizbranch → lever → password.");
        }

        [TestCase("QuizBranchModuleSO Module_Room_07", TestInteractiveRoomKinds.MazeBranch)]
        [TestCase("LeverQuestionModuleSO Module_A", TestInteractiveRoomKinds.LeverPanel)]
        [TestCase("PasswordDoorModuleSO RoomModule", TestInteractiveRoomKinds.PasswordDoor)]
        public void RoomTemplate_WithoutDeclaredKind_IsInferredFromModuleTypeName(string moduleHint, string expected)
        {
            FakeRoom room = new("room_module_" + expected, string.Empty, moduleHint);

            Assert.That(TestInteractiveRoomKinds.Standard.TryResolveRoom(room, out IInteractiveRoomKindRule rule), Is.True,
                "Имя типа модуля участвует в выводе, как до реестра: ассет модуля может называться как угодно.");
            Assert.That(rule.Kind, Is.EqualTo(expected));
        }

        [Test]
        public void RoomTemplate_WithoutKindOrKnownModule_IsNotInteractive()
        {
            FakeRoom room = new("room_plain", string.Empty, "CorridorModuleSO Corridor");

            Assert.That(TestInteractiveRoomKinds.Standard.TryResolveRoom(room, out _), Is.False);
        }

        [Test]
        public void RoomTemplate_IsResolvedOncePerRoomId_AndAChangedDescriptorIsResolvedAgain()
        {
            IInteractiveRoomKindRules rules = new InteractiveRoomKindRules(TestInteractiveRoomKinds.Standard.Rules);
            FakeRoom room = new("room_cached", TestInteractiveRoomKinds.LeverPanel, moduleHint: null);

            Assert.That(rules.TryResolveRoom(room, out IInteractiveRoomKindRule first), Is.True);
            int readsAfterFirst = room.KindReads;
            Assert.That(rules.TryResolveRoom(room, out IInteractiveRoomKindRule second), Is.True);
            Assert.That(second, Is.SameAs(first));
            Assert.That(room.KindReads, Is.EqualTo(readsAfterFirst + 1),
                "Повторный запрос читает только объявленный вид для сверки с кэшем — без повторного разбора.");

            FakeRoom sameIdOtherKind = new("room_cached", TestInteractiveRoomKinds.PasswordDoor, moduleHint: null);
            Assert.That(rules.TryResolveRoom(sameIdOtherKind, out IInteractiveRoomKindRule third), Is.True);
            Assert.That(third.Kind, Is.EqualTo(TestInteractiveRoomKinds.PasswordDoor),
                "Кэш по id комнаты не должен отдать вид другой комнаты с тем же id.");
        }

        private sealed class FakeRoom : IRoomTemplateDescriptor, IInteractiveRoomTemplateDescriptor
        {
            private readonly string _declaredKind;

            public FakeRoom(string roomId, string declaredKind, string moduleHint)
            {
                RoomId = roomId;
                _declaredKind = declaredKind;
                InteractiveModuleHint = moduleHint;
            }

            public int KindReads { get; private set; }

            public string RoomId { get; }
            public RoomKind Kind => RoomKind.Interactive;
            public string TilesetId => "test";
            public int Difficulty => 1;
            public int Weight => 1;
            public bool CanBeMainPath => true;
            public bool CanBeSideBranch => true;
            public bool CanBeStart => false;
            public bool CanBeExit => false;
            public bool CanBeDeadEnd => false;
            public IReadOnlyList<string> Tags => new string[0];
            public int EstimatedDurationSeconds => 1;
            public RoomFootprint Footprint => new(1, 1);
            public IReadOnlyList<IDoorSocketDescriptor> Doors => new IDoorSocketDescriptor[0];
            public string ModuleAssetId => "module_asset";

            public string InteractiveKind
            {
                get
                {
                    KindReads++;
                    return _declaredKind;
                }
            }

            public string InteractiveModuleHint { get; }
            public int InteractiveOptionCapacity => 1;
        }
    }
}
