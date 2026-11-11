using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Pure helper that computes the final pick weight for a room template given mission + tileset overrides.
    /// </summary>
    public sealed class EffectiveRoomWeightResolver
    {
        private readonly IReadOnlyDictionary<string, int> _missionRoomOverrides;
        private readonly IReadOnlyDictionary<RoomKind, int> _missionKindOverrides;
        private readonly IReadOnlyDictionary<string, int> _tilesetRoomOverrides;
        private readonly IReadOnlyDictionary<RoomKind, int> _tilesetKindOverrides;

        public EffectiveRoomWeightResolver(
            IReadOnlyDictionary<string, int> missionRoomOverrides = null,
            IReadOnlyDictionary<RoomKind, int> missionKindOverrides = null,
            IReadOnlyDictionary<string, int> tilesetRoomOverrides = null,
            IReadOnlyDictionary<RoomKind, int> tilesetKindOverrides = null)
        {
            _missionRoomOverrides = missionRoomOverrides;
            _missionKindOverrides = missionKindOverrides;
            _tilesetRoomOverrides = tilesetRoomOverrides;
            _tilesetKindOverrides = tilesetKindOverrides;
        }

        public int Resolve(IRoomTemplateDescriptor room)
        {
            if (room == null)
            {
                return 0;
            }

            int baseWeight = room.Weight;

            if (_missionRoomOverrides != null && _missionRoomOverrides.TryGetValue(room.RoomId, out int missionRoom))
            {
                baseWeight = missionRoom;
            }
            else if (_tilesetRoomOverrides != null &&
                     _tilesetRoomOverrides.TryGetValue(room.RoomId, out int tilesetRoom))
            {
                baseWeight = tilesetRoom;
            }

            if (baseWeight <= 0)
            {
                return 0;
            }

            int kindMultiplier = 100;
            if (_missionKindOverrides != null && _missionKindOverrides.TryGetValue(room.Kind, out int missionKind))
            {
                kindMultiplier = missionKind;
            }
            else if (_tilesetKindOverrides != null && _tilesetKindOverrides.TryGetValue(room.Kind, out int tilesetKind))
            {
                kindMultiplier = tilesetKind;
            }

            if (kindMultiplier <= 0)
            {
                return 0;
            }

            long resolved = (long)baseWeight * kindMultiplier / 100L;
            if (resolved <= 0L)
            {
                return 0;
            }

            return resolved > int.MaxValue ? int.MaxValue : (int)resolved;
        }
    }
}
