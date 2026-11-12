using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Domain.Events
{
    /// <summary>
    /// Published when MissionGenerator successfully produced a <see cref="LevelLayout"/>; the host's spawner listens and
    /// instantiates room prefabs.
    /// <para>
    /// <b>Два идентификатора, и они РАЗНЫЕ.</b> <see cref="LevelConfigId"/> — имя конфига генерации уровня, а
    /// <see cref="ContextId"/> — контекст хозяина, ради которого уровень построен (у учебной игры — урок, у
    /// которого этот конфиг лежит полем). Пока поле было одно, потребители читали имя конфига как идентификатор
    /// контекста и спрашивали о несуществующем; имена разведены, чтобы перепутать их молча было нельзя.
    /// </para>
    /// <para>
    /// <b>Два сида, и они тоже РАЗНЫЕ.</b> <see cref="Seed"/> — ЗАПРОШЕННЫЙ сид, а
    /// <see cref="SuccessfulSeed"/> — тот, на котором генерация сошлась: при неудачной попытке
    /// <c>MissionGenerator</c> берёт следующий (<c>seed + attempt</c>). Всё, что раскладывается по
    /// готовому уровню детерминированно (предметы контента), обязано сидироваться УСПЕШНЫМ: он же
    /// хозяин сохраняет в своём чекпоинте, и с него же миссия возобновляется.
    /// Пока раскладка бралась от запрошенного, после перезагрузки страницы она перемешивалась —
    /// несобранные предметы «переезжали» в другие точки, а их идентификаторы оказывались привязаны к
    /// чужим местам.
    /// </para>
    /// </summary>
    public readonly struct LevelGenerationCompletedEvent
    {
        /// <summary>Идентификатор конфига генерации уровня (id конфига миссии хозяина).</summary>
        public readonly string LevelConfigId;

        /// <summary>
        /// Контекст хозяина, ради которого миссия запущена (например, урок). Пусто — миссию запустили мимо
        /// этого потока (читы, превью): это законно, и потребителю, которому нужен контекст, гадать не о чем.
        /// </summary>
        public readonly string ContextId;

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
        /// Раскладка поверх уровня (предметы контента) на нём не планируется — мест для неё нет по построению.
        /// </summary>
        public readonly bool IsForcedTestLevel;

        public LevelGenerationCompletedEvent(
            string levelConfigId,
            string contextId,
            int seed,
            LevelLayout layout,
            long durationMs)
            : this(levelConfigId, contextId, seed, seed, layout, durationMs)
        {
        }

        public LevelGenerationCompletedEvent(
            string levelConfigId,
            string contextId,
            int seed,
            int successfulSeed,
            LevelLayout layout,
            long durationMs,
            bool isForcedTestLevel = false)
        {
            LevelConfigId = levelConfigId;
            ContextId = contextId;
            Seed = seed;
            SuccessfulSeed = successfulSeed;
            Layout = layout;
            DurationMs = durationMs;
            IsForcedTestLevel = isForcedTestLevel;
        }
    }
}
