using UnityEngine;

namespace RedoSchool.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// AABB bounds of a room prefab. Used by validators, editor tooling and broad runtime metadata.
    /// Precise runtime room entry is driven by door transition detectors because generated room
    /// bounds can overlap for arbitrary-angle and wall-shaped layouts.
    /// Must be attached to a Collider set as Trigger.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class RoomBounds : MonoBehaviour
    {
        [SerializeField] [Tooltip("Настройка параметра \"boxCollider\".")]
        private BoxCollider boxCollider;

        public BoxCollider Box => boxCollider != null ? boxCollider : boxCollider = GetComponent<BoxCollider>();

        private void Reset()
        {
            boxCollider = GetComponent<BoxCollider>();
            if (boxCollider != null)
            {
                boxCollider.isTrigger = true;
            }
        }
    }
}
