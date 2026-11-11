using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Runtime view of one spawned room.
    /// </summary>
    public sealed class RoomInstance : MonoBehaviour
    {
        [SerializeField] [Tooltip("Node id in generated graph.")]
        private string nodeId;

        [SerializeField] [Tooltip("Generator-facing room kind.")]
        private RoomKind roomKind;

        [SerializeField] [Tooltip("Source room template.")]
        private RoomTemplate template;

        [SerializeField] [Tooltip("Door sockets in this room instance.")]
        private List<DoorSocket> doors = new();

        public string NodeId => nodeId;
        public RoomKind Kind => roomKind;
        public RoomTemplate Template => template;
        public IReadOnlyList<DoorSocket> Doors => doors;

        public void Configure(string nodeIdValue, RoomKind kindValue, RoomTemplate templateValue)
        {
            nodeId = nodeIdValue;
            roomKind = kindValue;
            template = templateValue;
            doors.Clear();
            if (template != null)
            {
                doors.AddRange(template.Doors);
            }
        }

        public DoorSocket FindSocket(string socketId)
        {
            DoorSocket fallback = null;
            for (int i = 0; i < doors.Count; i++)
            {
                if (doors[i] != null && doors[i].SocketId == socketId)
                {
                    fallback ??= doors[i];
                    if (doors[i].State != DoorState.Disabled
                        && doors[i].enabled
                        && doors[i].gameObject.activeSelf)
                    {
                        return doors[i];
                    }
                }
            }

            return fallback;
        }
    }
}
