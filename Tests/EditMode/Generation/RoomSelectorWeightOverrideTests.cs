using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    [Category("LevelGeneration")]
    public sealed class RoomSelectorWeightOverrideTests
    {
        [Test]
        public void MissionRoomWeightOverride_CanForceSpecificTemplate()
        {
            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            FakeRoomDatabase database = new(new IRoomTemplateDescriptor[] { new FakeRoomTemplate("room_exploration_a", RoomKind.Passage, weight: 1), new FakeRoomTemplate("room_exploration_b", RoomKind.Passage, weight: 1) });
            OverrideConfig config = new(new[] { new RoomWeight("room_exploration_a", 0), new RoomWeight("room_exploration_b", 100) });
            RoomNode node = new("n_explore", RoomKind.Passage, 2);

            IRoomTemplateDescriptor selected =
                selector.Select(node, config, new FakeTileset(), database, new FirstRollRng());

            Assert.AreEqual("room_exploration_b", selected.RoomId);
        }

        private sealed class OverrideConfig : FakeMissionConfig
        {
            private readonly IReadOnlyList<IRoomWeightOverride> _overrides;

            public OverrideConfig(IReadOnlyList<IRoomWeightOverride> overrides)
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
    }
}
