using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.Placement
{
    /// <summary>
    /// MVP port: pick where the main objective trigger goes inside Objective rooms.
    /// </summary>
    public interface IObjectivePlacer
    {
        public void Place(LevelLayout layout);
    }
}
