namespace Neoxider.LevelGeneration.Generation.Application.Pipeline
{
    public static class RoomWallCollisionSettings
    {
        public const float DefaultPlacementWallHalfWidthShrinkMeters = 0.4f;
        public const float DefaultPlacementWallEndShrinkMeters = 0.3f;

        public static float PlacementWallHalfWidthShrinkMeters { get; set; } =
            DefaultPlacementWallHalfWidthShrinkMeters;

        public static float PlacementWallEndShrinkMeters { get; set; } =
            DefaultPlacementWallEndShrinkMeters;
    }
}
