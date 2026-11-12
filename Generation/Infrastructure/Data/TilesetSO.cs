using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using UnityEngine;

namespace Neoxider.LevelGeneration.Generation.Infrastructure.Data
{
    /// <summary>
    /// Visual world definition: list of rooms + default generation parameters.
    /// Implements <see cref="ITilesetView"/> directly so the pipeline does not import <c>UnityEngine</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Neoxider/LevelGeneration/Tileset", fileName = "TilesetSO")]
    public sealed class TilesetSO : ScriptableObject, ITilesetView
    {
        [Header("Identity")] [SerializeField] [Tooltip("Настройка параметра \"tilesetId\".")]
        private string tilesetId;

        [SerializeField] [Tooltip("Настройка параметра \"displayName\".")]
        private string displayName;

        [SerializeField] [Tooltip("Настройка параметра \"icon\".")]
        private Sprite icon;

        [Header("Rooms")] [SerializeField] [Tooltip("Настройка параметра \"roomPrefabs\".")]
        private List<GameObject> roomPrefabs = new();

        [Header("Default room counts (inclusive ranges)")] [SerializeField] [Tooltip("Настройка параметра \"totalRoomsRange\".")]
        private IntRangeSerialized totalRoomsRange = new(5, 9);

        [SerializeField] [Tooltip("Настройка параметра \"mainPathRoomsRange\".")]
        private IntRangeSerialized mainPathRoomsRange = new(4, 6);

        [SerializeField] [Tooltip("Настройка параметра \"branchingRange\".")]
        private IntRangeSerialized branchingRange = new(1, 3);

        [SerializeField] [Tooltip("Настройка параметра \"sideBranchesRange\".")]
        private IntRangeSerialized sideBranchesRange = new(0, 3);

        [SerializeField] [Tooltip("Настройка параметра \"deadEndsRange\".")]
        private IntRangeSerialized deadEndsRange = new(0, 2);

        [Header("Key-Lock dependency distance default")] [SerializeField] [Tooltip("Настройка параметра \"dependencyDistanceRange\".")]
        private IntRangeSerialized dependencyDistanceRange = new(3, 6);

        [Header("Default chances")] [SerializeField] [Tooltip("Настройка параметра \"bonusRoomChance\".")] [Range(0f, 1f)]
        private float bonusRoomChance = 0.5f;

        [SerializeField] [Tooltip("Настройка параметра \"secretRoomChance\".")] [Range(0f, 1f)]
        private float secretRoomChance = 0.2f;

        [SerializeField] [Tooltip("Настройка параметра \"logicRoomShare\".")] [Range(0f, 1f)]
        private float logicRoomShare = 0.4f;

        [Header("Extraction defaults")] [SerializeField] [Tooltip("Настройка параметра \"defaultExtractionMode\".")]
        private ExtractionMode defaultExtractionMode = ExtractionMode.SeparateExitRoom;

        [SerializeField] [Tooltip("Настройка параметра \"defaultExtractionAvailability\".")]
        private ExtractionAvailability defaultExtractionAvailability = ExtractionAvailability.AlwaysAvailable;

        [Header("Visual / audio (optional)")] [SerializeField] [Tooltip("Настройка параметра \"defaultSkybox\".")]
        private Material defaultSkybox;

        [SerializeField] [Tooltip("Настройка параметра \"ambientLoop\".")]
        private AudioClip ambientLoop;

        public string TilesetId => tilesetId;
        public string DisplayName => displayName;
        public Sprite Icon => icon;
        public IReadOnlyList<GameObject> RoomPrefabs => roomPrefabs;

        public IEnumerable<RoomTemplate> Rooms
        {
            get
            {
                for (int i = 0; i < roomPrefabs.Count; i++)
                {
                    if (roomPrefabs[i] != null && roomPrefabs[i].TryGetComponent<RoomTemplate>(out RoomTemplate room))
                    {
                        yield return room;
                    }
                }
            }
        }

        public Material DefaultSkybox => defaultSkybox;
        public AudioClip AmbientLoop => ambientLoop;

        // ---- ITilesetView ----
        IntRange ITilesetView.TotalRoomsRange => new(totalRoomsRange.min, totalRoomsRange.max);
        IntRange ITilesetView.MainPathRoomsRange => new(mainPathRoomsRange.min, mainPathRoomsRange.max);
        IntRange ITilesetView.BranchingRange => new(branchingRange.min, branchingRange.max);
        IntRange ITilesetView.SideBranchesRange => new(sideBranchesRange.min, sideBranchesRange.max);
        IntRange ITilesetView.DeadEndsRange => new(deadEndsRange.min, deadEndsRange.max);
        IntRange ITilesetView.DependencyDistanceRange => new(dependencyDistanceRange.min, dependencyDistanceRange.max);
        float ITilesetView.BonusRoomChance => bonusRoomChance;
        float ITilesetView.SecretRoomChance => secretRoomChance;
        float ITilesetView.LogicRoomShare => logicRoomShare;
        ExtractionMode ITilesetView.DefaultExtractionMode => defaultExtractionMode;
        ExtractionAvailability ITilesetView.DefaultExtractionAvailability => defaultExtractionAvailability;
    }
}
