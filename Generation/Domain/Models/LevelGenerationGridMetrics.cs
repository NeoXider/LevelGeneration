namespace Neoxider.LevelGeneration.Generation.Domain.Models
{
    public static class LevelGenerationGridMetrics
    {
        public const float DoorSnapMeters = 5f;
        public const float CellSizeMeters = DoorSnapMeters;
        public const float FootprintCellDoorSteps = CellSizeMeters / DoorSnapMeters;
    }
}
