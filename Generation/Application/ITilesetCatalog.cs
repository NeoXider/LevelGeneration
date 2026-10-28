using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application
{
    /// <summary>
    /// Catalog of available tilesets (visual worlds). Returns Application-side views so the pipeline does not import UnityEngine.
    /// </summary>
    public interface ITilesetCatalog
    {
        public ITilesetView FindTileset(string tilesetId);
    }

    public interface ITilesetView
    {
        public string TilesetId { get; }
        public string DisplayName { get; }

        public IntRange TotalRoomsRange { get; }
        public IntRange MainPathRoomsRange { get; }
        public IntRange BranchingRange { get; }
        public IntRange SideBranchesRange { get; }
        public IntRange DeadEndsRange { get; }
        public IntRange DependencyDistanceRange { get; }

        public float BonusRoomChance { get; }
        public float SecretRoomChance { get; }
        public float LogicRoomShare { get; }

        public ExtractionMode DefaultExtractionMode { get; }
        public ExtractionAvailability DefaultExtractionAvailability { get; }
    }

    public readonly struct IntRange
    {
        public readonly int Min;
        public readonly int Max;

        public IntRange(int min, int max)
        {
            Min = min;
            Max = max;
        }
    }
}
