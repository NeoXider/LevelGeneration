using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Domain.Events
{
    /// <summary>
    /// Published when MissionGenerator successfully produced a <see cref="LevelLayout"/>.
    /// LevelSpawner listens and instantiates room prefabs.
    /// <para>
    /// <b>Два идентификатора, и они РАЗНЫЕ.</b> <see cref="LevelConfigId"/> — это имя конфига
    /// генерации (<c>MissionConfig_SciFiAcademy_Procedural_Tunnels_01</c>), а <see cref="LessonId"/> —
    /// урок бэкенда (<c>m1u1_python_intro</c>), у которого этот конфиг лежит полем
    /// <c>planet_practice.level_generation_mission_id</c>. Раньше поле было одно и звалось
    /// <c>MissionId</c>; потребители читали его как урок, спрашивали бэкенд про несуществующий урок и
    /// получали 404. Имена разведены именно поэтому — перепутать их больше нельзя молча.
    /// </para>
    /// <para>
    /// <b>Два сида, и они тоже РАЗНЫЕ.</b> <see cref="Seed"/> — ЗАПРОШЕННЫЙ сид, а
    /// <see cref="SuccessfulSeed"/> — тот, на котором генерация сошлась: при неудачной попытке
    /// <c>MissionGenerator</c> берёт следующий (<c>seed + attempt</c>). Всё, что раскладывается по
    /// готовому уровню детерминированно (колбы-фрагменты), обязано сидироваться УСПЕШНЫМ: он же
    /// лежит в <c>MissionRuntimeCheckpoint.SuccessfulSeed</c> и с него же миссия возобновляется.
    /// Пока раскладка бралась от запрошенного, после перезагрузки страницы она перемешивалась —
    /// несобранные колбы «переезжали» в другие точки, а идентификаторы <c>code-fragment:i</c>
    /// оказывались привязаны к чужим местам.
    /// </para>
    /// </summary>
    public readonly struct LevelGenerationCompletedEvent
    {
        /// <summary>Идентификатор конфига генерации уровня (<c>MissionConfigSO.missionId</c>).</summary>
        public readonly string LevelConfigId;

        /// <summary>
        /// Урок бэкенда, ради которого миссия запущена. Пусто — миссию запустили мимо флоу урока
        /// (читы, превью): это законно, но у потребителей, которым нужен контент урока, повода
        /// гадать нет.
        /// </summary>
        public readonly string LessonId;

        /// <summary>Сид, который запросил запуск миссии (диагностика, отчёты о прогоне).</summary>
        public readonly int Seed;

        /// <summary>
        /// Сид попытки, на которой уровень действительно построен (<c>MissionGenerator.LastSuccessfulSeed</c>).
        /// Единственный сид, пригодный для детерминированной раскладки поверх готового уровня.
        /// </summary>
        public readonly int SuccessfulSeed;

        public readonly LevelLayout Layout;
        public readonly long DurationMs;

        /// <summary>
        /// Принудительный тестовый уровень (читы): в раскладке только старт, запрошенная комната и выход.
        /// Раскладка поверх уровня (колбы урока) на нём не планируется — мест для неё нет по построению.
        /// </summary>
        public readonly bool IsForcedTestLevel;

        public LevelGenerationCompletedEvent(
            string levelConfigId,
            string lessonId,
            int seed,
            LevelLayout layout,
            long durationMs)
            : this(levelConfigId, lessonId, seed, seed, layout, durationMs)
        {
        }

        public LevelGenerationCompletedEvent(
            string levelConfigId,
            string lessonId,
            int seed,
            int successfulSeed,
            LevelLayout layout,
            long durationMs,
            bool isForcedTestLevel = false)
        {
            LevelConfigId = levelConfigId;
            LessonId = lessonId;
            Seed = seed;
            SuccessfulSeed = successfulSeed;
            Layout = layout;
            DurationMs = durationMs;
            IsForcedTestLevel = isForcedTestLevel;
        }
    }
}
