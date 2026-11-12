using Neoxider.LevelGeneration.Generation.Domain.Enums;

namespace Neoxider.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Runtime door-state mutator. Application/Rooms modules call this to open/lock a door without
    /// touching <c>UnityEngine</c>. Infrastructure adapter forwards to <c>DoorVisualSwitcher</c>.
    /// </summary>
    public interface IDoorStateController
    {
        public bool SetDoorState(string nodeId, string socketId, DoorState newState);

        public DoorState GetDoorState(string nodeId, string socketId);
    }
}
