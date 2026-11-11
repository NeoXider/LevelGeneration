using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.Placement
{
    /// <summary>
    /// MVP port: pick where the main objective trigger goes inside Objective rooms.
    /// </summary>
    public interface IObjectivePlacer
    {
        public void Place(LevelLayout layout);
    }
}
