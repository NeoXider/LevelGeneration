namespace Neoxider.LevelGeneration.Generation.Infrastructure
{
    /// <summary>
    /// Где проект хранит контент генерации — для редакторного поиска комнат и инструментов авторинга. Пакет
    /// раскладку проекта не знает: проект задаёт её сам, обычно из <c>[InitializeOnLoad]</c> своей редакторной
    /// сборки. Значения по умолчанию подходят любому проекту: весь <c>Assets</c>, без пула комнат.
    /// В плеере не используется — там комнаты приходят из <c>TilesetSO.RoomPrefabs</c> и Resources.
    /// </summary>
    public static class LevelGenerationProjectPaths
    {
        /// <summary>Папки, где редакторный <see cref="RoomDatabase"/> ищет префабы с <c>RoomTemplate</c>.</summary>
        public static string[] RoomPrefabRoots { get; set; } = { "Assets" };

        /// <summary>Папка, которую Room Authoring Toolkit предлагает для префабов комнат.</summary>
        public static string RoomPrefabFolder { get; set; } = "Assets";

        /// <summary>
        /// Путь к <c>RoomPoolSO</c>, который инструменты показывают и пополняют; пусто — пул не задан.
        /// </summary>
        public static string DefaultRoomPoolPath { get; set; } = string.Empty;

        /// <summary>Папка Resources с комнатами, которую по умолчанию проверяет Room Compatibility Report.</summary>
        public static string RoomResourcesPath { get; set; } = string.Empty;
    }
}
