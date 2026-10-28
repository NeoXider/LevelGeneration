namespace RedoSchool.Features.LevelGeneration
{
    /// <summary>
    /// Public identity for the Neoxider level-generation library boundary.
    /// Keep this in sync with package.json and the LevelGeneration changelog.
    /// </summary>
    public static class LevelGenerationPackageInfo
    {
        public const string PackageName = "com.neoxider.level-generation";
        public const string DisplayName = "Neoxider LevelGeneration";
        public const string Version = "4.0.0";
        public const string Author = "Neoxider";

        public const string GeneratorBoundary = "Generation";
        public const string MinimapBoundary = "Runtime/Presentation";
        public const string RuntimeBoundary = "Runtime";

        public const bool MinimapIsPartOfLevelGeneration = true;
        public const bool RoomsAreOptionalGameplayIntegration = false;
    }
}
