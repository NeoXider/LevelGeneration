using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Application;
using RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
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
