using Neoxider.LevelGeneration.Generation.Domain.Enums;
using UnityEngine;

namespace Neoxider.LevelGeneration.Generation.Infrastructure.Data
{
    /// <summary>
    /// Designer-facing archetype: which built-in shape to use when MissionGraphBuilder builds the spine.
    /// Holds a thin discriminator now; richer template data (room sequence, branch points) can be added later.
    /// </summary>
    [CreateAssetMenu(menuName = "Neoxider/LevelGeneration/Mission Archetype", fileName = "MissionArchetypeSO")]
    public sealed class MissionArchetypeSO : ScriptableObject
    {
        [SerializeField] [Tooltip("Настройка параметра \"archetypeId\".")]
        private string archetypeId;

        [SerializeField] [Tooltip("Настройка параметра \"kind\".")]
        private MissionArchetypeKind kind = MissionArchetypeKind.Simple;

        [SerializeField] [Tooltip("Настройка параметра \"designNote\".")] [TextArea(2, 4)]
        private string designNote;

        public string ArchetypeId => archetypeId;
        public MissionArchetypeKind Kind => kind;
        public string DesignNote => designNote;
    }
}
