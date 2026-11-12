using Neoxider.LevelGeneration.Generation.Application.Placement;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Infrastructure.Placement
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
