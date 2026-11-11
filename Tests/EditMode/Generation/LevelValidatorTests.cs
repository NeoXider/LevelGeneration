using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class LevelValidatorTests
    {
        [Test]
        public void Validate_ReportsMissingStart()
        {
            LevelLayout layout = new(
                new[] { Room("n_other", 0, 0) },
                new DoorConnection[0],
                "n_start",
                new[] { "n_other" });

            LevelValidationResult result =
                new LevelValidator().Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "MissingStart");
        }

        [Test]
        public void Validate_ReportsOverlap()
        {
            LevelLayout layout = new(
                new[] { Room("n_start", 0, 0), Room("n_exit", 0, 0) },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });

            LevelValidationResult result =
                new LevelValidator().Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "RoomsOverlap");
        }

        [Test]
        public void Validate_ReportsOverlap_ForMultiCellFootprints()
        {
            LevelLayout layout = new(
                new[] { new PlacedRoom("n_start", "big", new GridPose(0, 0, 0), new Dictionary<string, DoorState>()), new PlacedRoom("n_exit", "small", new GridPose(1, 1, 0), new Dictionary<string, DoorState>()) },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("big", RoomKind.Start, footprint: new RoomFootprint(2, 2)), new FakeRoomTemplate("small", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "RoomsOverlap");
        }

        [Test]
        public void Validate_WorldPoseUsesUnityYawConvention_ForRotatedWorldPoseRooms()
        {
            Quaternion rotation = Quaternion.Euler(0f, 90f, 0f);
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(-4f, -24f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(-4f, 0f, -24f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1)),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0f, -24f, 90),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0f, 0f, -24f, rotation.x, rotation.y, rotation.z, rotation.w),
                        new RoomFootprint(1, 1))
                },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsFalse(result.Issues.Any(issue => issue.Code == "RoomsOverlap"));
        }

        [Test]
        public void Validate_WorldPoseUsesWallSegments_WhenFootprintsOverlapButWallsDoNot()
        {
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1)),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(1f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1))
                },
                Array.Empty<DoorConnection>(),
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    wallSegments: new[] { new RoomWallSegment2D(0f, 0f, 4f, 0f, 0.2f) }),
                new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    wallSegments: new[] { new RoomWallSegment2D(0f, 4f, 4f, 4f, 0.2f) })
            });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsFalse(result.Issues.Any(issue => issue.Code == "RoomsOverlap"));
        }

        [Test]
        public void Validate_GridPoseFallsBackToFootprint_WhenWallSegmentsMissing()
        {
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>()),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>())
                },
                Array.Empty<DoorConnection>(),
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "RoomsOverlap");
        }

        [Test]
        public void Validate_ConnectedDoorSeamIgnoresOnlyNearbyWallIntersection()
        {
            float seam = LevelGenerationGridMetrics.DoorSnapMeters;
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>()),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>())
                },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new[] { new FakeDoorSocket("east", direction: DoorDirection.East) },
                    wallSegments: new[] { new RoomWallSegment2D(seam, seam - 1f, seam, seam + 1f, 0.2f) }),
                new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    doors: new[] { new FakeDoorSocket("west", direction: DoorDirection.West) },
                    wallSegments: new[] { new RoomWallSegment2D(seam - 1f, seam, seam + 1f, seam, 0.2f) })
            });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsFalse(result.Issues.Any(issue => issue.Code == "RoomsOverlap"));
        }

        [Test]
        public void Validate_ConnectedDoorSeamIgnoresLongWallsCrossingDoorSeam()
        {
            float seam = LevelGenerationGridMetrics.DoorSnapMeters;
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>()),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>())
                },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new[] { new FakeDoorSocket("east", direction: DoorDirection.East) },
                    wallSegments: new[] { new RoomWallSegment2D(seam, seam - 6f, seam, seam + 6f, 0.2f) }),
                new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    doors: new[] { new FakeDoorSocket("west", direction: DoorDirection.West) },
                    wallSegments: new[] { new RoomWallSegment2D(seam - 6f, seam, seam + 6f, seam, 0.2f) })
            });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsFalse(result.Issues.Any(issue => issue.Code == "RoomsOverlap"));
        }

        [Test]
        public void Validate_ConnectedRoomsReportWallIntersectionAwayFromDoorSeam()
        {
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>()),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>())
                },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[]
            {
                new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new[] { new FakeDoorSocket("east", direction: DoorDirection.East) },
                    wallSegments: new[] { new RoomWallSegment2D(0f, 0f, 4f, 0f, 0.2f) }),
                new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    doors: new[] { new FakeDoorSocket("west", direction: DoorDirection.West) },
                    wallSegments: new[] { new RoomWallSegment2D(0f, 0f, 4f, 0f, 0.2f) })
            });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "RoomsOverlap");
        }

        [Test]
        public void Validate_WorldPoseAllowsTinyConnectedDoorSeamOverlap_ForWorldPoseRooms()
        {
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1)),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0.99f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(4.95f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1))
                },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsFalse(result.Issues.Any(issue => issue.Code == "RoomsOverlap"));
        }

        [Test]
        public void Validate_WorldPoseReportsBlockingOverlap_ForConnectedWorldPoseRooms()
        {
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1)),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0.1f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0.5f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1))
                },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "RoomsOverlap");
        }

        [Test]
        public void Validate_WorldPoseReportsOverlap_ForUnconnectedWorldPoseRooms()
        {
            LevelLayout layout = new(
                new[]
                {
                    new PlacedRoom(
                        "n_start",
                        "room_start",
                        new GridPose(0f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1)),
                    new PlacedRoom(
                        "n_exit",
                        "room_exit",
                        new GridPose(0.1f, 0f, 0),
                        new Dictionary<string, DoorState>(),
                        new RoomWorldPose(0.5f, 0f, 0f, 0f, 0f, 0f, 1f),
                        new RoomFootprint(1, 1))
                },
                Array.Empty<DoorConnection>(),
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "RoomsOverlap");
        }

        [Test]
        public void Validate_ReportsRoomKindCountOutOfRange()
        {
            LevelLayout layout = new(
                new[] { new PlacedRoom("n_start", "room_start", new GridPose(0, 0, 0), new Dictionary<string, DoorState>()), new PlacedRoom("n_exit", "room_exit", new GridPose(4, 0, 0), new Dictionary<string, DoorState>()) },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result = new LevelValidator(db).Validate(
                layout,
                new RoomKindRuleMissionConfig(new RoomKindRule(RoomKind.Interactive, new OptionalIntRange(2, 3))),
                new FakeTileset());

            AssertIssue(result, "RoomKindCountOutOfRange");
        }

        [Test]
        public void Validate_ReportsBrokenMainPath()
        {
            LevelLayout layout = new(
                new[] { Room("n_start", 0, 0), Room("n_mid", 1, 0), Room("n_exit", 2, 0) },
                new[] { new DoorConnection("n_start", "east", "n_mid", "west") },
                "n_start",
                new[] { "n_exit" });

            LevelValidationResult result =
                new LevelValidator().Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "MainPathBroken");
        }

        [Test]
        public void Validate_ReportsError_WhenPlacedRoomsBelowMinimum()
        {
            LevelLayout layout = new(
                new[] { Room("n_start", 0, 0), Room("n_exit", 2, 0) },
                new[] { new DoorConnection("n_start", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });

            LevelValidationResult result =
                new LevelValidator().Validate(layout, new MinRoomsMissionConfig(3), new FakeTileset());

            AssertIssue(result, "LengthOutOfRange", LevelValidationSeverity.Error);
            Assert.IsFalse(result.IsValid);
        }

        [Test]
        public void Validate_ReportsExitRoomsBelowCount_ForDoubleExit()
        {
            LevelLayout layout = new(
                new[] { Room("n_start", 0, 0), Room("n_mid", 1, 0), Room("n_exit", 2, 0) },
                new[] { new DoorConnection("n_start", "east", "n_mid", "west"), new DoorConnection("n_mid", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_mid", RoomKind.Passage), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new DoubleExitMissionConfig(), new FakeTileset());

            AssertIssue(result, "ExitRoomsBelowCount");
        }

        [Test]
        public void Validate_ReportsKeyDependencyUnsatisfied_WhenDistanceTooLong()
        {
            LevelLayout layout = KeyLockLayoutFactory.Create(7);
            LevelValidator validator = KeyLockLayoutFactory.CreateValidator(3, 6);

            LevelValidationResult result = validator.Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "KeyDependencyUnsatisfied");
        }

        [Test]
        public void Validate_DeadEndCapOverlappingNonDeadEndRoom_IsError()
        {
            // Раньше любое пересечение с шаблоном DeadEnd понижалось до предупреждения, и заглушка поверх
            // комнаты основного пути уходила в игру. Теперь это ошибка: сид отклоняется.
            LevelLayout layout = new(
                new[] { new PlacedRoom("n_start", "room_start", new GridPose(0, 0, 0), new Dictionary<string, DoorState>()), new PlacedRoom("n_cap", "room_cap", new GridPose(0, 0, 0), new Dictionary<string, DoorState>()) },
                new[] { new DoorConnection("n_start", "east", "n_cap", "west") },
                "n_start",
                new string[0]);
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_cap", RoomKind.DeadEnd) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            AssertIssue(result, "RoomsOverlap", LevelValidationSeverity.Error);
            Assert.IsFalse(result.Issues.Any(issue => issue.Code == "RoomsOverlap"
                                                      && issue.Severity == LevelValidationSeverity.Warning));
            Assert.IsFalse(result.IsValid);
        }

        [Test]
        public void Validate_ReportsEveryOverlap_SoADeadEndWarningCannotHideAnError()
        {
            // n_cap_b (широкая заглушка) накрывает и заглушку n_cap_a (предупреждение: обе DeadEnd), и проход n_passage
            // (ошибка). Раньше проверка комнаты останавливалась на первом пересечении, и ошибка терялась.
            Dictionary<string, DoorState> noStates = new();
            LevelLayout layout = new(
                new[] { new PlacedRoom("n_start", "room_start", new GridPose(10, 10, 0), noStates), new PlacedRoom("n_cap_a", "room_cap", new GridPose(0, 0, 0), noStates), new PlacedRoom("n_passage", "room_passage", new GridPose(2, 0, 0), noStates), new PlacedRoom("n_cap_b", "room_cap_wide", new GridPose(0, 0, 0), noStates) },
                new DoorConnection[0],
                "n_start",
                new string[0]);
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_start", RoomKind.Start), new FakeRoomTemplate("room_cap", RoomKind.DeadEnd), new FakeRoomTemplate("room_passage", RoomKind.Passage), new FakeRoomTemplate("room_cap_wide", RoomKind.DeadEnd, footprint: new RoomFootprint(3, 1)) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsTrue(
                result.Issues.Any(issue => issue.Code == "RoomsOverlap"
                                           && issue.Severity == LevelValidationSeverity.Error
                                           && issue.Message.Contains("n_cap_b")
                                           && issue.Message.Contains("n_passage")),
                "The cap-over-passage overlap must be reported as an Error even after a cap-over-cap warning.");
            Assert.IsTrue(
                result.Issues.Any(issue => issue.Code == "RoomsOverlap"
                                           && issue.Severity == LevelValidationSeverity.Warning
                                           && issue.Message.Contains("n_cap_a")));
        }

        [Test]
        public void Validate_RejectsRoomTemplateWithDuplicateConnectableSocketIds()
        {
            // Состояние двери адресуется по SocketId: две подключаемые двери «door» молча делили бы одно состояние.
            LevelLayout layout = new(
                new[] { new PlacedRoom("n_start", "dup_room", new GridPose(0, 0, 0), new Dictionary<string, DoorState>()), new PlacedRoom("n_exit", "room_exit", new GridPose(4, 0, 0), new Dictionary<string, DoorState>()) },
                new DoorConnection[0],
                "n_start",
                new[] { "n_exit" });
            FakeRoomDatabase db = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("dup_room", RoomKind.Start, doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("door", direction: DoorDirection.East), new FakeDoorSocket("door", direction: DoorDirection.West) }), new FakeRoomTemplate("room_exit", RoomKind.Exit) });

            LevelValidationResult result =
                new LevelValidator(db).Validate(layout, new FakeMissionConfig(), new FakeTileset());

            Assert.IsTrue(
                result.Issues.Any(issue => issue.Code == DoorSocketIdRules.DuplicateIssueCode
                                           && issue.Severity == LevelValidationSeverity.Error
                                           && issue.Message.Contains("'dup_room'")
                                           && issue.Message.Contains("'door'")),
                "Duplicate connectable socket ids must be an Error naming the room and the socket.");
        }

        [Test]
        public void DoorSocketIdRules_IgnoresDuplicatesAmongNonConnectableSockets()
        {
            FakeRoomTemplate decorative = new(
                "decorative_room",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("deco", initialState: DoorState.Disabled), new FakeDoorSocket("deco", initialState: DoorState.Disabled), new FakeDoorSocket("east", direction: DoorDirection.East) });
            FakeRoomTemplate mixed = new(
                "mixed_room",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new FakeDoorSocket("east", direction: DoorDirection.East), new FakeDoorSocket("east", initialState: DoorState.Disabled) });

            Assert.IsFalse(DoorSocketIdRules.TryFindDuplicateConnectableSocketId(decorative, out _));
            Assert.IsTrue(DoorSocketIdRules.TryFindDuplicateConnectableSocketId(mixed, out string socketId));
            Assert.AreEqual("east", socketId);
        }

        private static PlacedRoom Room(string nodeId, int x, int y)
        {
            return new PlacedRoom(nodeId, $"template_{nodeId}", new GridPose(x, y, 0),
                new Dictionary<string, DoorState>());
        }

        private static void AssertIssue(LevelValidationResult result, string code) => Assert.IsTrue(result.Issues.Any(issue => issue.Code == code), $"Expected issue '{code}'.");

        private static void AssertIssue(LevelValidationResult result, string code, LevelValidationSeverity severity)
        {
            Assert.IsTrue(
                result.Issues.Any(issue => issue.Code == code && issue.Severity == severity),
                $"Expected issue '{code}' with severity '{severity}'.");
        }

        private sealed class MinRoomsMissionConfig : FakeMissionConfig
        {
            private readonly int _minRooms;

            public MinRoomsMissionConfig(int minRooms)
            {
                _minRooms = minRooms;
            }

            public override OptionalIntRange TotalRoomsRange => new(_minRooms, 10);
        }

        private sealed class RoomKindRuleMissionConfig : FakeMissionConfig
        {
            private readonly IReadOnlyList<IRoomKindSpawnRule> _rules;

            public RoomKindRuleMissionConfig(params IRoomKindSpawnRule[] rules)
            {
                _rules = rules;
            }

            public override IReadOnlyList<IRoomKindSpawnRule> RoomKindSpawnRules => _rules;
        }

        private sealed class RoomKindRule : IRoomKindSpawnRule
        {
            public RoomKindRule(RoomKind kind, OptionalIntRange countRange)
            {
                Kind = kind;
                CountRange = countRange;
            }

            public RoomKind Kind { get; }
            public RoomTypeSpawnMode Mode => RoomTypeSpawnMode.CountRange;
            public bool OverrideChance => false;
            public float Chance => 0f;
            public OptionalIntRange CountRange { get; }
        }

        private sealed class DoubleExitMissionConfig : FakeMissionConfig
        {
            public override ExtractionMode ExtractionMode => ExtractionMode.DoubleExit;
            public override int ExtractionRoomsCount => 2;
        }
    }
}
