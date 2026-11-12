using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using Neoxider.LevelGeneration.Runtime.Application;
using Neoxider.LevelGeneration.Runtime.Domain.Events;
using UnityEngine;

namespace Neoxider.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Door-plane based room transition detector.
    /// RoomBounds can overlap for angled/wall-shaped rooms, so runtime room discovery must be
    /// driven by actual connected doors instead of broad room bounds.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class DoorRoomTransitionDetector : MonoBehaviour
    {
        private const float SideEpsilonMeters = 0.05f;

        [SerializeField] [Tooltip("Player tag accepted by this transition trigger.")]
        private string playerTag = "Player";

        private readonly HashSet<Transform> _trackedActors = new(2);
        private readonly Dictionary<Transform, Vector3> _enterPoints = new(2);
        private RoomDoorTransitionTracker _tracker;
        private RoomInstance _ownerRoom;
        private RoomInstance _connectedRoom;
        private Vector3 _planePoint;
        private Vector3 _outwardNormal;

        public string OwnerNodeId => _ownerRoom != null ? _ownerRoom.NodeId : null;
        public string ConnectedNodeId => _connectedRoom != null ? _connectedRoom.NodeId : null;

        public void Bind(
            RoomDoorTransitionTracker tracker,
            RoomInstance ownerRoom,
            RoomInstance connectedRoom,
            Vector3 planePoint,
            Vector3 outwardNormalWorld)
        {
            _tracker = tracker;
            _ownerRoom = ownerRoom;
            _connectedRoom = connectedRoom;
            _planePoint = planePoint;
            _outwardNormal = NormalizeHorizontal(outwardNormalWorld);
            EnsureTriggerCollider();
        }

        public bool TryPublishCrossing(Vector3 enterPoint, Vector3 exitPoint)
        {
            if (!TryResolveCrossing(enterPoint, exitPoint, out RoomInstance targetRoom))
            {
                return false;
            }

            _tracker?.PublishEntered(targetRoom);
            return true;
        }

        public bool TryResolveCrossing(Vector3 enterPoint, Vector3 exitPoint, out RoomInstance targetRoom)
        {
            targetRoom = null;
            if (_ownerRoom == null || _connectedRoom == null)
            {
                return false;
            }

            float enterSide = SignedSide(enterPoint);
            float exitSide = SignedSide(exitPoint);
            if (enterSide < -SideEpsilonMeters && exitSide > SideEpsilonMeters)
            {
                targetRoom = _connectedRoom;
                return true;
            }

            if (enterSide > SideEpsilonMeters && exitSide < -SideEpsilonMeters)
            {
                targetRoom = _ownerRoom;
                return true;
            }

            return false;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsPlayer(other))
            {
                return;
            }

            Transform actor = PlayerTriggerDetection.ResolveActorRoot(other);
            if (actor == null)
            {
                return;
            }

            Track(actor, SamplePoint(other));
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsPlayer(other))
            {
                return;
            }

            Transform actor = PlayerTriggerDetection.ResolveActorRoot(other);
            if (actor == null || !TryUntrack(actor, out Vector3 enterPoint))
            {
                return;
            }

            TryPublishCrossing(enterPoint, SamplePoint(other));
        }

        private void OnDisable()
        {
            _trackedActors.Clear();
            _enterPoints.Clear();
        }

        private bool IsPlayer(Collider other) => PlayerTriggerDetection.IsPlayerCollider(other, playerTag);

        private float SignedSide(Vector3 point) => Vector3.Dot(point - _planePoint, _outwardNormal);

        private void Track(Transform actor, Vector3 point)
        {
            _trackedActors.Add(actor);
            _enterPoints[actor] = point;
        }

        private bool TryUntrack(Transform actor, out Vector3 point)
        {
            if (!_trackedActors.Remove(actor) || !_enterPoints.TryGetValue(actor, out point))
            {
                point = default;
                return false;
            }

            _enterPoints.Remove(actor);
            return true;
        }

        private static Vector3 SamplePoint(Collider collider) => collider != null ? collider.bounds.center : Vector3.zero;

        private static Vector3 NormalizeHorizontal(Vector3 value)
        {
            value.y = 0f;
            return value.sqrMagnitude > 0.0001f ? value.normalized : Vector3.forward;
        }

        private void EnsureTriggerCollider()
        {
            Collider[] colliders = GetComponents<Collider>();
            if (colliders == null || colliders.Length == 0)
            {
                BoxCollider box = gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(2.4f, 3f, 2.4f);
                box.center = new Vector3(0f, 1.5f, 0f);
                box.isTrigger = true;
                return;
            }

            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].isTrigger = true;
                }
            }
        }
    }

    public sealed class RoomDoorTransitionTracker
    {
        private readonly IRoomEventBus _bus;
        private string _currentNodeId;

        public RoomDoorTransitionTracker(IRoomEventBus bus, string initialNodeId)
        {
            _bus = bus;
            _currentNodeId = initialNodeId;
        }

        public void PublishEntered(RoomInstance room)
        {
            if (_bus == null || room == null || string.IsNullOrEmpty(room.NodeId))
            {
                return;
            }

            if (string.Equals(_currentNodeId, room.NodeId, System.StringComparison.Ordinal))
            {
                return;
            }

            _currentNodeId = room.NodeId;
            _bus.Publish(new RoomEnteredEvent(room.NodeId, room.Kind));
        }
    }
}
