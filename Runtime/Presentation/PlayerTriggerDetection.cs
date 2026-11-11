using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Shared player collider checks for mission triggers (tag on root, multi-collider actors).
    /// </summary>
    public static class PlayerTriggerDetection
    {
        public static bool IsPlayerCollider(Collider other, string playerTag)
        {
            if (other == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(playerTag))
            {
                return true;
            }

            if (other.CompareTag(playerTag))
            {
                return true;
            }

            Transform root = ResolveActorRoot(other);
            return root != null && root.CompareTag(playerTag);
        }

        public static bool IsPlayerComponent(Component other, string playerTag)
        {
            if (other == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(playerTag))
            {
                return true;
            }

            if (other.CompareTag(playerTag))
            {
                return true;
            }

            Transform root = other is Collider collider
                ? ResolveActorRoot(collider)
                : other.transform.root;
            return root != null && root.CompareTag(playerTag);
        }

        public static Transform ResolveActorRoot(Collider other)
        {
            if (other == null)
            {
                return null;
            }

            if (other.attachedRigidbody != null)
            {
                return other.attachedRigidbody.transform;
            }

            return other.transform.root;
        }
    }
}
