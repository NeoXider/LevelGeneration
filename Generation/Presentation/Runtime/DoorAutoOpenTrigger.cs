using System.Collections.Generic;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// Child trigger volume for a door. Opens visual/animator only when DoorSocket.AutoOpen is enabled
    /// and the generated door state is usable. Locked, secret, wall and disabled doors ignore the trigger.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class DoorAutoOpenTrigger : MonoBehaviour
    {
        private readonly HashSet<EntityId> _activePlayerColliders = new();

        [SerializeField] [Tooltip("Reference to the target door socket.")]
        private DoorSocket targetDoor;

        [SerializeField] [Tooltip("Player tag used by this trigger.")]
        private string playerTag = "Player";

        private BoxCollider _box;

        private void Reset()
        {
            targetDoor = GetComponentInParent<DoorSocket>(true);
            EnsureTriggerCollider();
        }

        private void OnValidate()
        {
            if (targetDoor == null)
            {
                targetDoor = GetComponentInParent<DoorSocket>(true);
            }

            EnsureTriggerCollider();
        }

        private void OnEnable() => ResolveTargetDoor();

        private void OnDisable()
        {
            if (_activePlayerColliders.Count > 0)
            {
                _activePlayerColliders.Clear();
                TrySetDoorOpen(false);
            }
        }

        /// <summary>
        /// Re-applies the open state when a player is already inside the trigger. Called when the door's
        /// state changes externally (e.g. a password terminal unlocks the door) so the visual catches up
        /// without forcing the player to step out of and back into the trigger volume.
        /// </summary>
        public void RefreshTriggeredOpen()
        {
            if (_activePlayerColliders.Count > 0)
            {
                TrySetDoorOpen(true);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsPlayer(other))
            {
                return;
            }

            if (_activePlayerColliders.Add(other.GetEntityId()))
            {
                TrySetDoorOpen(true);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsPlayer(other))
            {
                return;
            }

            _activePlayerColliders.Remove(other.GetEntityId());
            if (_activePlayerColliders.Count == 0)
            {
                TrySetDoorOpen(false);
            }
        }

        private void TrySetDoorOpen(bool isOpen)
        {
            ResolveTargetDoor();
            if (targetDoor == null)
            {
                return;
            }

            // Dead-end / non-connectable doors are switched to Disabled at runtime and must never open
            // or report blocked attempts, even if a trigger volume still surrounds them.
            if (targetDoor.State == Domain.Enums.DoorState.Disabled)
            {
                targetDoor.TrySetTriggeredOpen(false);
                return;
            }

            bool applied = targetDoor.TrySetTriggeredOpen(isOpen);
            if (applied && isOpen)
            {
                NotifyAutoOpenReceivers(targetDoor, true);
                return;
            }

            if (isOpen && targetDoor.State == Domain.Enums.DoorState.Locked)
            {
                NotifyAutoOpenReceivers(targetDoor, false);
                if (targetDoor.IsAvailableForAutoOpen && targetDoor.TrySetTriggeredOpen(true))
                {
                    NotifyAutoOpenReceivers(targetDoor, true);
                }
            }
        }

        private void NotifyAutoOpenReceivers(DoorSocket door, bool isOpen)
        {
            MonoBehaviour[] behaviours = GetComponentsInParent<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is IDoorAutoOpenReceiver receiver)
                {
                    receiver.HandleDoorAutoOpen(door, isOpen);
                }
            }
        }

        private void ResolveTargetDoor()
        {
            if (targetDoor == null)
            {
                targetDoor = GetComponentInParent<DoorSocket>(true);
            }
        }

        private bool IsPlayer(Collider other) => other != null && other.CompareTag(playerTag);

        private void EnsureTriggerCollider()
        {
            _box = _box != null ? _box : GetComponent<BoxCollider>();
            if (_box == null)
            {
                return;
            }

            _box.isTrigger = true;
        }
    }
}
