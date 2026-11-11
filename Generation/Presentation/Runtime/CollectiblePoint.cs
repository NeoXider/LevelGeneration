using UnityEngine;

namespace RedoSchool.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// Scene marker for a position where collectibles / key items can appear.
    /// Rarity is a hint for the placer (Common / Rare / KeyItem / Secret).
    /// </summary>
    public sealed class CollectiblePoint : MonoBehaviour
    {
        [SerializeField] [Tooltip("Настройка параметра \"pointId\".")]
        private string pointId = "default";

        [SerializeField] [Tooltip("Настройка параметра \"rarity\".")]
        private string rarity = "Common";

        public string PointId => pointId;
        public string Rarity => rarity;
    }
}
