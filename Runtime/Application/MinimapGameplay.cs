using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Application
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
    /// Что карте уровня нужно знать об игре, в которую она встроена: где ученик, нажата ли клавиша большой
    /// карты и как описать состояние управления в диагностике. Реализует игра; пакет об устройстве её ввода не
    /// знает и устройства сам не читает — ни напрямую, ни через reflection.
    /// </summary>
    public interface IMinimapGameplayContext
    {
        /// <summary>В мире ли ученик: таймер подсказки маршрута идёт и большая карта рисуется только в мире.</summary>
        RouteHintPresence Presence { get; }

        /// <summary>
        /// В этом кадре нажата геймплейная клавиша большой карты. Хозяин читает свой ввод сам и обязан молчать,
        /// пока клавиатурой владеет интерфейс (чат, поле ввода): иначе буква, напечатанная в чате, разворачивала
        /// бы карту поверх него.
        /// </summary>
        bool WasFullMapTogglePressed { get; }

        /// <summary>Фрагмент диагностической строки подсказки о состоянии управления, без ведущего пробела.</summary>
        string DescribeControl();
    }

    /// <summary>
    /// Точка подключения <see cref="IMinimapGameplayContext"/>. Игра назначает свою реализацию при запуске
    /// (после <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>); без неё карта работает
    /// автономно: ученик всегда в мире, а клавиша большой карты — <c>M</c> старого менеджера ввода, если он
    /// включён в проекте (без него — никогда).
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

            /// <summary>
            /// Без хозяина ввод читать некому, кроме старого менеджера: он доступен без сторонних сборок. Новый
            /// Input System пакет не знает — его клавишу подаёт хозяин.
            /// </summary>
            public bool WasFullMapTogglePressed
            {
                get
                {
#if ENABLE_LEGACY_INPUT_MANAGER
                    return Input.GetKeyDown(KeyCode.M);
#else
                    return false;
#endif
                }
            }

            public string DescribeControl() => "control=standalone";
        }
    }
}
