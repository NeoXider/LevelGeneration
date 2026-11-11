using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.DependencyHints;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    /// <summary>
    /// Covers the LevelValidator hardening fixes:
    ///   H4 — reachability is now enforced for ALL extraction modes, not just exit modes.
    ///   C2 (interim) — a genuine key-behind-lock dependency violation must be a hard Error.
    /// Reuses the shared fakes declared alongside <see cref="MissionGraphBuilder_KeyLockDistanceTests"/>.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelValidatorConnectivityAndDependencyTests
    {
        // (a) H4: an interior room that is not connected to Start must be an Error,
        //     even in a non-exit extraction mode (SameAsInsertion here).
        [Test]
        public void Validate_ReportsUnreachableInteriorRoom_InNonExitMode()
        {
            // n_start - n_mid connected; n_interior is placed but has NO connection -> disconnected.
            LevelLayout layout = new(
                new[] { Room("n_start", "room_start", 0, 0), Room("n_mid", "room_mid", 1, 0), Room("n_interior", "room_interior", 5, 5) },
                new[] { new DoorConnection("n_start", "east", "n_mid", "west") },
                "n_start",
                new string[0]);

            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_mid", RoomKind.Passage), new FakeRoomTemplate("room_interior", RoomKind.Interactive) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new SameAsInsertionMissionConfig(), new FakeTileset());

            Assert.IsTrue(
                result.Issues.Any(i =>
                    i.Code == "RoomUnreachable" && i.Severity == LevelValidationSeverity.Error),
                "Expected an Error 'RoomUnreachable' for the disconnected interior room.");
            Assert.IsFalse(result.IsValid);
        }

        [Test]
        public void Validate_DoesNotReportUnreachable_WhenAllRoomsConnected()
        {
            LevelLayout layout = new(
                new[] { Room("n_start", "room_start", 0, 0), Room("n_mid", "room_mid", 1, 0), Room("n_interior", "room_interior", 2, 0) },
                new[] { new DoorConnection("n_start", "east", "n_mid", "west"), new DoorConnection("n_mid", "east", "n_interior", "west") },
                "n_start",
                new string[0]);

            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_mid", RoomKind.Passage), new FakeRoomTemplate("room_interior", RoomKind.Interactive) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new SameAsInsertionMissionConfig(), new FakeTileset());

            Assert.IsFalse(result.Issues.Any(i => i.Code == "RoomUnreachable"));
        }

        // (b) C2 (interim): a key placed behind its own lock (key not reachable before the lock)
        //     surfaces as a hard Error, not a Warning, so the layout is rejected and retried.
        //     The hint requires room_key -> room_lock to be connected within distance 1..2, but the
        //     key room is disconnected from the lock, so the dependency is genuinely unsatisfied.
        [Test]
        public void Validate_ReportsKeyBehindLock_AsError()
        {
            // Start -> lock is reachable, but the key sits on an isolated island (unreachable),
            // i.e. the player can reach the lock before ever reaching the key.
            LevelLayout layout = new(
                new[] { Room("n_start", "room_start", 0, 0), Room("n_lock", "room_lock", 1, 0), Room("n_key", "room_key", 9, 9) },
                new[] { new DoorConnection("n_start", "east", "n_lock", "west") },
                "n_start",
                new string[0]);

            LevelValidator validator = new(
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_lock", RoomKind.Interactive), new FakeRoomTemplate("room_key", RoomKind.Interactive) }),
                new[]
                {
                    new StaticHintExtractor(
                        new RoomDependencyHint("room_key", "room_lock", 1, 2))
                });

            LevelValidationResult result =
                validator.Validate(layout, new SameAsInsertionMissionConfig(), new FakeTileset());

            Assert.IsTrue(
                result.Issues.Any(i =>
                    i.Code == "KeyDependencyUnsatisfied" && i.Severity == LevelValidationSeverity.Error),
                "Expected an Error 'KeyDependencyUnsatisfied' for the key-behind-lock dependency.");
            Assert.IsFalse(result.IsValid);
        }

        [Test]
        public void Validate_RejectsKeyPlacedBehindItsOwnLock()
        {
            // Start -> lock -> key: ключ в одном переходе от замка (дистанция в пределах 1..3), но дойти до него
            // можно только через сам замок. Раньше расстояние считалось без учёта старта, и раскладка проходила.
            LevelLayout layout = new(
                new[] { Room("n_start", "room_start", 0, 0), Room("n_lock", "room_lock", 1, 0), Room("n_key", "room_key", 2, 0) },
                new[] { new DoorConnection("n_start", "east", "n_lock", "west"), new DoorConnection("n_lock", "east", "n_key", "west") },
                "n_start",
                new string[0]);

            LevelValidationResult result = KeyLockValidator().Validate(
                layout,
                new SameAsInsertionMissionConfig(),
                new FakeTileset());

            Assert.IsTrue(
                result.Issues.Any(i =>
                    i.Code == "KeyDependencyUnsatisfied"
                    && i.Severity == LevelValidationSeverity.Error
                    && i.Message.Contains("n_lock")),
                "A key reachable only through its own lock must be rejected.");
            Assert.IsFalse(result.IsValid);
        }

        [Test]
        public void Validate_AcceptsKeyBeforeItsLock()
        {
            LevelLayout layout = new(
                new[] { Room("n_start", "room_start", 0, 0), Room("n_key", "room_key", 1, 0), Room("n_lock", "room_lock", 2, 0) },
                new[] { new DoorConnection("n_start", "east", "n_key", "west"), new DoorConnection("n_key", "east", "n_lock", "west") },
                "n_start",
                new string[0]);

            LevelValidationResult result = KeyLockValidator().Validate(
                layout,
                new SameAsInsertionMissionConfig(),
                new FakeTileset());

            Assert.IsFalse(result.Issues.Any(i => i.Code == "KeyDependencyUnsatisfied"));
        }

        [Test]
        public void Validate_RejectsLockWithoutAnyKeyRoom()
        {
            // Замок поставлен, комнаты с ключом в раскладке нет вовсе. Раньше подсказка молча пропускалась
            // (нечего сравнивать), и замок без ключа уходил в игру; теперь это ошибка.
            LevelLayout layout = new(
                new[] { Room("n_start", "room_start", 0, 0), Room("n_lock", "room_lock", 1, 0), Room("n_after", "room_start", 2, 0) },
                new[] { new DoorConnection("n_start", "east", "n_lock", "west"), new DoorConnection("n_lock", "east", "n_after", "west") },
                "n_start",
                new string[0]);

            LevelValidationResult result = KeyLockValidator().Validate(
                layout,
                new SameAsInsertionMissionConfig(),
                new FakeTileset());

            Assert.IsTrue(
                result.Issues.Any(i =>
                    i.Code == "KeyDependencyUnsatisfied"
                    && i.Severity == LevelValidationSeverity.Error
                    && i.Message.Contains("'n_lock'")),
                "A placed lock without any key room must be rejected.");
            Assert.IsFalse(result.IsValid);
        }

        private static LevelValidator KeyLockValidator()
        {
            return new LevelValidator(
                new FakeRoomDatabase(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_lock", RoomKind.Interactive), new FakeRoomTemplate("room_key", RoomKind.Interactive) }),
                new[] { new StaticHintExtractor(new RoomDependencyHint("room_key", "room_lock", 1, 3)) });
        }

        private static PlacedRoom Room(string nodeId, string templateId, int x, int y)
        {
            return new PlacedRoom(nodeId, templateId, new GridPose(x, y, 0),
                new Dictionary<string, DoorState>());
        }

        private sealed class SameAsInsertionMissionConfig : FakeMissionConfig
        {
            public override ExtractionMode ExtractionMode => ExtractionMode.SameAsInsertion;
        }
    }
}
