using RedoSchool.LevelGeneration.Generation.Application.Placement;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Infrastructure.Placement
{
    /// <summary>MVP placer stubs — placement happens via room-prefab anchors, no extra logic for v1.</summary>
    public sealed class StubObjectivePlacer : IObjectivePlacer
    {
        public void Place(LevelLayout layout)
        {
        }
    }

    public sealed class StubCollectiblePlacer : ICollectiblePlacer
    {
        public void Place(LevelLayout layout)
        {
        }
    }

    public sealed class StubRewardPlacer : IRewardPlacer
    {
        public void Place(LevelLayout layout)
        {
        }
    }

    public sealed class StubSecretPlacer : ISecretPlacer
    {
        public void Place(LevelLayout layout)
        {
        }
    }
}
