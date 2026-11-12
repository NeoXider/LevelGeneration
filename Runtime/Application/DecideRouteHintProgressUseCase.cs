using System.Collections.Generic;

namespace Neoxider.LevelGeneration.Runtime.Application
{
    /// <summary>Исход решения о прогрессе подсказки маршрута.</summary>
    public enum RouteHintProgress
    {
        /// <summary>
        /// Прогресса нет: боковая комната, тупик, ветка неверного ответа, повторный визит
        /// или открытие двери. Таймер простоя не трогаем, показанную подсказку не гасим.
        /// </summary>
        NoProgress = 0,

        /// <summary>
        /// Игрок продвинулся: вошёл в комнату критического пути дальше всех посещённых
        /// или выполнил задание текущей цели. Отсчёт простоя начинается заново.
        /// </summary>
        ProgressAdvanced = 1,

        /// <summary>Все комнаты критического пути посещены: вести некуда.</summary>
        RouteComplete = 2
    }

    /// <summary>
    /// Решает, было ли событие прогрессом к цели подсказки. Прогресс — вход в комнату
    /// критического пути дальше самой дальней уже посещённой, выполнение задания цели
    /// или завершение всего маршрута. Открытие двери и вход в комнату вне пути —
    /// не прогресс: таймер простоя и показанная подсказка их переживают, а путь
    /// перепланируется с новой позиции. Без состояния: входные данные держит
    /// <see cref="LevelRouteHintSession"/>, решает этот use case.
    /// </summary>
    public static class DecideRouteHintProgressUseCase
    {
        public static RouteHintProgress Execute(
            LevelCriticalPath path,
            int previousProgressIndex,
            ICollection<string> visitedRoomIds)
        {
            if (path == null || path.IsEmpty)
            {
                return RouteHintProgress.NoProgress;
            }

            int progress = DecideRouteHintUseCase.ResolveProgressIndex(path, visitedRoomIds);
            if (progress >= path.Nodes.Count - 1)
            {
                return RouteHintProgress.RouteComplete;
            }

            return progress > previousProgressIndex
                ? RouteHintProgress.ProgressAdvanced
                : RouteHintProgress.NoProgress;
        }

        public static RouteHintProgress ExecuteTaskCompleted(bool wasTargetTask)
        {
            return wasTargetTask ? RouteHintProgress.ProgressAdvanced : RouteHintProgress.NoProgress;
        }
    }
}
