using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Application.Placement
{
    /// <summary>
    /// MVP port: pick where the key items / collectibles go.
    /// In v1 only used by the DoorUnlock+Key demo (Room_KeyVault).
    /// </summary>
    public interface ICollectiblePlacer
    {
        public void Place(LevelLayout layout);
    }
}
