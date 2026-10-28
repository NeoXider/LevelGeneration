using System.Collections.Generic;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Generation.Infrastructure.Data
{
    /// <summary>
    /// Reusable room prefab list for a planet/tileset. Consumer mission configs can reference one
    /// or more pools and still keep mission-local room overrides.
    /// </summary>
    [CreateAssetMenu(menuName = "Neoxider/LevelGeneration/Room Pool", fileName = "RoomPoolSO")]
    public sealed class RoomPoolSO : ScriptableObject
    {
        [SerializeField] private string poolId;
        [SerializeField] private List<MissionRoomRule> rooms = new();

        public string PoolId => !string.IsNullOrWhiteSpace(poolId) ? poolId : name;
        public IReadOnlyList<MissionRoomRule> Rooms => rooms;
    }
}
