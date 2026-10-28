namespace RedoSchool.Features.LevelGeneration.Generation.Application.DependencyHints
{
    /// <summary>
    /// Declares that SourceRoomTemplateId should be placed at a topology distance from TargetRoomTemplateId.
    /// </summary>
    public readonly struct RoomDependencyHint
    {
        public readonly string SourceRoomTemplateId;
        public readonly string TargetRoomTemplateId;
        public readonly int MinDistance;
        public readonly int MaxDistance;

        public RoomDependencyHint(string sourceRoomTemplateId, string targetRoomTemplateId, int minDistance,
            int maxDistance)
        {
            SourceRoomTemplateId = sourceRoomTemplateId;
            TargetRoomTemplateId = targetRoomTemplateId;
            MinDistance = minDistance;
            MaxDistance = maxDistance;
        }

        public bool IsValid =>
            !string.IsNullOrEmpty(SourceRoomTemplateId) &&
            !string.IsNullOrEmpty(TargetRoomTemplateId) &&
            MaxDistance >= 0;
    }
}
