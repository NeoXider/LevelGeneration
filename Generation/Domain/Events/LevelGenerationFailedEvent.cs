namespace RedoSchool.Features.LevelGeneration.Generation.Domain.Events
{
    /// <summary>
    /// Published when MissionGenerator exhausted retries and could not produce a valid layout.
    /// </summary>
    public readonly struct LevelGenerationFailedEvent
    {
        public readonly string MissionId;
        public readonly int Seed;
        public readonly string Reason;

        public LevelGenerationFailedEvent(string missionId, int seed, string reason)
        {
            MissionId = missionId;
            Seed = seed;
            Reason = reason ?? string.Empty;
        }
    }
}
