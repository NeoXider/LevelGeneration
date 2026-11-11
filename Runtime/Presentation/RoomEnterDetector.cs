using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;
using RedoSchool.LevelGeneration.Runtime.Application;
using RedoSchool.LevelGeneration.Runtime.Domain.Events;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Sits on the <c>RoomBounds</c> trigger. Publishes <see cref="RoomEnteredEvent"/> whenever
    /// the player enters this room after fully leaving it.
    /// </summary>
    [RequireComponent(typeof(RoomBounds))]
    public sealed class RoomEnterDetector : MonoBehaviour
    {
        [SerializeField] [Tooltip("Настройка параметра \"playerTag\".")]
        private string playerTag = "Player";

        private IRoomEventBus _bus;
        private RoomInstance _room;
        private readonly HashSet<Transform> _actorsInside = new();

        public void Bind(IRoomEventBus bus, RoomInstance room)
        {
            _bus = bus;
            _room = room;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_bus == null || _room == null || !PlayerTriggerDetection.IsPlayerCollider(other, playerTag))
            {
                return;
            }

            Transform actor = PlayerTriggerDetection.ResolveActorRoot(other);
            if (actor == null || !_actorsInside.Add(actor))
            {
                return;
            }

            _bus.Publish(new RoomEnteredEvent(_room.NodeId, _room.Kind));
        }

        private void OnTriggerExit(Collider other)
        {
            if (!PlayerTriggerDetection.IsPlayerCollider(other, playerTag))
            {
                return;
            }

            Transform actor = PlayerTriggerDetection.ResolveActorRoot(other);
            if (actor != null)
            {
                _actorsInside.Remove(actor);
            }
        }

        private void OnDisable() => _actorsInside.Clear();
    }
}
