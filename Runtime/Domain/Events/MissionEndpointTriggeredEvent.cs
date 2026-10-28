using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.Features.LevelGeneration.Runtime.Domain.Events
{
    public enum MissionEndpointKind
    {
        Start = 0,
        Exit = 1
    }

    public readonly struct MissionEndpointTriggeredEvent
    {
        public readonly string NodeId;
        public readonly RoomKind RoomKind;
        public readonly MissionEndpointKind EndpointKind;

        public MissionEndpointTriggeredEvent(string nodeId, RoomKind roomKind, MissionEndpointKind endpointKind)
        {
            NodeId = nodeId;
            RoomKind = roomKind;
            EndpointKind = endpointKind;
        }
    }
}
