using System;
using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class ShootingGalleryGenerationTests
    {
        [Test]
        public void Preparation_KeepsShootingContentWhenItsTemplateExists()
        {
            GalleryConfig config = new();
            List<string> warnings = new();

            IMissionConfigView prepared = MissionGenerator.CreateInteractiveContentConfig(config, new FakeTileset(),
                new FakeRoomDatabase(new[] { GalleryRoom() }), TestInteractiveRoomKinds.Standard, warnings);

            Assert.That(((IInteractiveMissionContentProvider)prepared).InteractiveContent,
                Has.Count.EqualTo(config.InteractiveContent.Count));
            Assert.That(((IInteractiveMissionContentProvider)prepared).InteractiveContent[0].ContentId,
                Is.EqualTo(config.InteractiveContent[0].ContentId));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void Selection_ChoosesTheShootingTemplateForShootingContent()
        {
            GalleryConfig config = new();
            FakeRoomTemplate gallery = GalleryRoom();
            FakeRoomTemplate other = new("other", RoomKind.Interactive, interactiveKind: TestInteractiveRoomKinds.PasswordDoor,
                doors: new[] { new FakeDoorSocket("exit", initialState: DoorState.Locked) });
            RoomNode node = new("gallery_node", RoomKind.Interactive, 1, contentId: config.InteractiveContent[0].ContentId);

            IRoomTemplateDescriptor selected = new RoomSelector(TestInteractiveRoomKinds.Standard).Select(
                node, config, new FakeTileset(), new FakeRoomDatabase(new[] { other, gallery }), new FirstRollRng());

            Assert.That(selected, Is.SameAs(gallery), "Контент тира нельзя повесить на комнату другого вида.");
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ShootingContent_WithoutUsableTemplateFailsVisibly(bool mandatory, bool hasUnlockedTemplate)
        {
            GalleryConfig config = new(mandatory);
            IRoomTemplateDescriptor[] rooms = hasUnlockedTemplate
                ? new IRoomTemplateDescriptor[] { new FakeRoomTemplate("gallery_unlocked", RoomKind.Interactive,
                    interactiveKind: TestInteractiveRoomKinds.ShootingGallery) }
                : Array.Empty<IRoomTemplateDescriptor>();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                MissionGenerator.CreateInteractiveContentConfig(config, new FakeTileset(), new FakeRoomDatabase(rooms),
                    TestInteractiveRoomKinds.Standard));

            Assert.That(error.Message, Does.Contain(config.InteractiveContent[0].ContentId));
            Assert.That(error.Message, Does.Contain(config.InteractiveContent[0].InteractiveKind));
            Assert.That(error.Message, Does.Contain(config.TilesetId));
        }

        private static FakeRoomTemplate GalleryRoom() => new("gallery", RoomKind.Interactive,
            interactiveKind: TestInteractiveRoomKinds.ShootingGallery,
            doors: new[] { new FakeDoorSocket("exit", initialState: DoorState.Locked) });

        private sealed class GalleryConfig : FakeMissionConfig, IInteractiveMissionContentProvider
        {
            public GalleryConfig(bool mandatory = true) => InteractiveContent = new[] { new GalleryContent(mandatory) };
            public IReadOnlyList<IInteractiveMissionContentSpec> InteractiveContent { get; }
        }

        private sealed class GalleryContent : IInteractiveMissionContentSpec
        {
            public GalleryContent(bool mandatory) => IsMandatory = mandatory;
            public string ContentId => "shoot_1";
            public string InteractiveKind => TestInteractiveRoomKinds.ShootingGallery;
            public int OptionCount => 0;
            public int CorrectAnswerCount => 0;
            public bool IsMandatory { get; }
        }
    }
}
