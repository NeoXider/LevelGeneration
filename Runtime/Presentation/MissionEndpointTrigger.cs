using System.Collections.Generic;
using Neoxider.LevelGeneration.Runtime.Application;
using Neoxider.LevelGeneration.Runtime.Domain.Events;
using UnityEngine;

namespace Neoxider.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Player trigger for generated mission endpoints.
    /// Start endpoints provide the player spawn transform; Exit endpoints mark mission extraction points.
    /// </summary>
    public sealed class MissionEndpointTrigger : MonoBehaviour
    {
        [SerializeField] [Tooltip("Endpoint role for this generated mission trigger.")]
        private MissionEndpointKind kind = MissionEndpointKind.Start;

        [SerializeField] [Tooltip("Only colliders with this tag can activate the endpoint.")]
        private string playerTag = "Player";

        [SerializeField] [Tooltip("Transform used for player teleport on Start endpoints. Defaults to this object.")]
        private Transform spawnTransform;

        [SerializeField] [Tooltip("Default trigger size when the component creates its own BoxCollider.")]
        private Vector3 triggerSize = new(2.5f, 2f, 2.5f);

        private IRoomEventBus _bus;
        private RoomInstance _room;
        private readonly HashSet<Transform> _actorsInside = new();

        public MissionEndpointKind Kind => kind;
        public Transform SpawnTransform => spawnTransform != null ? spawnTransform : transform;

        public void Configure(
            MissionEndpointKind endpointKind,
            RoomInstance room,
            IRoomEventBus bus,
            Transform explicitSpawnTransform = null)
        {
            kind = endpointKind;
            _room = room;
            _bus = bus;
            if (explicitSpawnTransform != null)
            {
                spawnTransform = explicitSpawnTransform;
            }

            EnsureTriggerCollider();
        }

        private void Awake() => EnsureTriggerCollider();

        private void Reset() => EnsureTriggerCollider();

        private void OnValidate()
        {
            if (triggerSize.x <= 0f)
            {
                triggerSize.x = 2.5f;
            }

            if (triggerSize.y <= 0f)
            {
                triggerSize.y = 2f;
            }

            if (triggerSize.z <= 0f)
            {
                triggerSize.z = 2.5f;
            }

            Collider trigger = GetComponent<Collider>();
            if (trigger == null)
            {
                return;
            }

            trigger.isTrigger = true;
            if (trigger is BoxCollider box)
            {
                box.size = triggerSize;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsPlayer(other))
            {
                return;
            }

            Transform actor = other is Collider collider
                ? PlayerTriggerDetection.ResolveActorRoot(collider)
                : other.transform;
            if (actor == null || !_actorsInside.Add(actor))
            {
                return;
            }

            if (_room != null)
            {
                _bus?.Publish(new MissionEndpointTriggeredEvent(_room.NodeId, _room.Kind, kind));
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsPlayer(other))
            {
                return;
            }

            Transform actor = other is Collider collider
                ? PlayerTriggerDetection.ResolveActorRoot(collider)
                : other.transform;
            if (actor != null)
            {
                _actorsInside.Remove(actor);
            }
        }

        private bool IsPlayer(Component other) => PlayerTriggerDetection.IsPlayerComponent(other, playerTag);

        private void EnsureTriggerCollider()
        {
            Collider trigger = GetComponent<Collider>();
            if (trigger == null)
            {
                trigger = gameObject.AddComponent<BoxCollider>();
            }

            trigger.isTrigger = true;
            if (trigger is BoxCollider box)
            {
                box.size = triggerSize;
            }
        }
    }
}
