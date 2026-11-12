namespace Neoxider.LevelGeneration.Generation.Domain.Events
{
    /// <summary>
    /// Published when MissionGenerator starts building a layout. Loading screen listens.
    /// </summary>
    public readonly struct LevelGenerationStartedEvent
    {
        public readonly string MissionId;
        public readonly int Seed;

        public LevelGenerationStartedEvent(string missionId, int seed)
        {
            MissionId = missionId;
            Seed = seed;
        }
    }
}
