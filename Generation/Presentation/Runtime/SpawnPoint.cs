using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime
{
    public enum SpawnPointKind
    {
        Player,
        Npc,
        Enemy,
        Generic
    }

    /// <summary>Scene marker for an actor spawn position inside a room prefab.</summary>
    public sealed class SpawnPoint : MonoBehaviour
    {
        [SerializeField] [Tooltip("Настройка параметра \"kind\".")]
        private SpawnPointKind kind = SpawnPointKind.Player;

        public SpawnPointKind Kind => kind;
    }
}
