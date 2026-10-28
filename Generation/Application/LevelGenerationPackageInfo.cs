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
        public const string Version = "4.1.1";
        public const string Author = "Neoxider";

        /// <summary>
        /// Version of the layout a seed and a config produce. It changes only when the same seed and config can
        /// give a different layout (4.1.0: seed-only determinism, sealed incidental doors, key before its lock,
        /// real maze dead ends). A host that saves progress against a seed stores this value and starts the run
        /// over when it differs, instead of mapping saved room ids onto other rooms.
        /// </summary>
        public const int LayoutContract = 2;

        public const string GeneratorBoundary = "Generation";
        public const string MinimapBoundary = "Runtime/Presentation";
        public const string RuntimeBoundary = "Runtime";

        public const bool MinimapIsPartOfLevelGeneration = true;
        public const bool RoomsAreOptionalGameplayIntegration = false;
    }
}
