using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Application
{
    /// <summary>Где ученик с точки зрения подсказки маршрута и большой карты.</summary>
    public enum RouteHintPresence
    {
        /// <summary>
        /// Ученик в мире: ни панель, ни поле ввода, ни свободная мышь его не держат, контроллер ходит. Сюда же
        /// относится WebGL-ожидание захвата курсора браузером: курсор виден и взгляд выключен, пока игрок не
        /// сделает жест.
        /// </summary>
        Exploring = 0,

        /// <summary>Ученика держит UI: лок, захват клавиатуры, поле ввода, выключенный gaze-режим
        /// или замороженный контроллер. Видимость курсора сама по себе занятостью не считается.</summary>
        Occupied = 1
    }

    /// <summary>
    /// Что карте уровня нужно знать об игре, в которую она встроена: где ученик, свободна ли клавиатура и как
    /// описать состояние управления в диагностике. Реализует игра; пакет об устройстве её ввода не знает.
    /// </summary>
    public interface IMinimapGameplayContext
    {
        /// <summary>В мире ли ученик: таймер подсказки маршрута идёт и большая карта рисуется только в мире.</summary>
        RouteHintPresence Presence { get; }

        /// <summary>
        /// Клавиатурой владеет интерфейс (чат, поле ввода): геймплейная клавиша большой карты молчит.
        /// </summary>
        bool IsGameplayKeyboardBlocked { get; }

        /// <summary>Фрагмент диагностической строки подсказки о состоянии управления, без ведущего пробела.</summary>
        string DescribeControl();
    }

    /// <summary>
    /// Точка подключения <see cref="IMinimapGameplayContext"/>. Игра назначает свою реализацию при запуске
    /// (после <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>); без неё карта работает
    /// автономно: ученик всегда в мире, клавиатура свободна.
    /// </summary>
    public static class MinimapGameplay
    {
        private static IMinimapGameplayContext _context;

        public static IMinimapGameplayContext Context
        {
            get => _context ?? StandaloneContext.Instance;
            set => _context = value;
        }

        /// <summary>
        /// Domain reload в редакторе может быть выключен: без сброса контекст прошлой игровой сессии остался бы
        /// и в следующей. Сброс — при входе в Play Mode, игра назначает контекст заново до первой сцены.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnSubsystemRegistration() => _context = null;

        private sealed class StandaloneContext : IMinimapGameplayContext
        {
            public static readonly StandaloneContext Instance = new();

            public RouteHintPresence Presence => RouteHintPresence.Exploring;

            public bool IsGameplayKeyboardBlocked => false;

            public string DescribeControl() => "control=standalone";
        }
    }
}
