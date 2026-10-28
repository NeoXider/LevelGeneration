using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Infrastructure;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    [Category("LevelGeneration")]
    public sealed class RoomDatabaseTests
    {
        [Test]
        public void GetRoomsForTileset_UsesLatestRoomForDuplicateIds()
        {
            TestRoomTemplate stale = new("room", "tileset", RoomKind.Passage, true);
            TestRoomTemplate latest = new("room", "tileset", RoomKind.Passage, false);
            TestRoomTemplate other = new("room2", "tileset", RoomKind.Passage, true);
            RoomDatabase database = new(new IRoomTemplateDescriptor[] { stale, latest, other });

            Assert.AreEqual(2, database.AllRooms.Count);
            Assert.AreSame(latest, database.GetRoomsForTileset("tileset").Single(room => room.RoomId == "room"));
        }

        [Test]
        public void GetRoomsForTileset_FiltersByTilesetAndReturnsEmptyForUnknownOrMissing()
        {
            RoomDatabase database = new(new IRoomTemplateDescriptor[] { new TestRoomTemplate("a_start", "tileset_a", RoomKind.Start), new TestRoomTemplate("a_corridor", "tileset_a", RoomKind.Passage), new TestRoomTemplate("b_exit", "tileset_b", RoomKind.Exit), new TestRoomTemplate("no_tileset", null, RoomKind.Interactive) });

            IReadOnlyList<IRoomTemplateDescriptor> tilesetABuff = database.GetRoomsForTileset("tileset_a");
            Assert.AreEqual(2, tilesetABuff.Count);
            Assert.IsTrue(tilesetABuff.Any(room => room.RoomId == "a_start"));
            Assert.IsTrue(tilesetABuff.Any(room => room.RoomId == "a_corridor"));

            Assert.AreEqual(0, database.GetRoomsForTileset(string.Empty).Count);
            Assert.AreEqual(0, database.GetRoomsForTileset("missing").Count);
        }

        [Test]
        public void Constructor_SkipsDestroyedUnityRoomTemplates()
        {
            GameObject destroyedObject = new("DestroyedRoomTemplate");
            RoomTemplate destroyedTemplate = destroyedObject.AddComponent<RoomTemplate>();
            UnityEngine.Object.DestroyImmediate(destroyedObject);

            TestRoomTemplate live = new("live", "tileset", RoomKind.Passage);

            RoomDatabase database = new(new IRoomTemplateDescriptor[] { destroyedTemplate, live });

            Assert.AreEqual(1, database.AllRooms.Count);
            Assert.AreSame(live, database.AllRooms[0]);
        }

        private sealed class TestRoomTemplate : IRoomTemplateDescriptor
        {
            private readonly string _roomId;
            private readonly string _tilesetId;
            private readonly RoomKind _kind;
            private readonly bool _canBeStart;

            public TestRoomTemplate(
                string roomId,
                string tilesetId,
                RoomKind kind,
                bool canBeStart = false,
                bool canBeSideBranch = true,
                bool canBeMainPath = true,
                bool canBeExit = false,
                bool canBeDeadEnd = true)
            {
                _roomId = roomId;
                _tilesetId = tilesetId;
                _kind = kind;
                _canBeStart = canBeStart;
                CanBeSideBranch = canBeSideBranch;
                CanBeMainPath = canBeMainPath;
                CanBeExit = canBeExit;
                CanBeDeadEnd = canBeDeadEnd;
            }

            public string RoomId => _roomId;
            public RoomKind Kind => _kind;
            public string TilesetId => _tilesetId;
            public int Difficulty => 1;
            public int Weight => 1;
            public bool CanBeMainPath { get; }
            public bool CanBeSideBranch { get; }
            public bool CanBeStart => _canBeStart;
            public bool CanBeExit { get; }
            public bool CanBeDeadEnd { get; }
            public IReadOnlyList<string> Tags => Array.Empty<string>();
            public int EstimatedDurationSeconds => 60;
            public IReadOnlyList<IDoorSocketDescriptor> Doors => Array.Empty<IDoorSocketDescriptor>();
            public RedoSchool.Features.LevelGeneration.Generation.Domain.Models.RoomFootprint Footprint => new(1, 1);
            public string ModuleAssetId => null;
        }
    }
}
