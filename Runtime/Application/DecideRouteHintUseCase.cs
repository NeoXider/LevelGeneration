using System;
using System.Collections.Generic;

namespace RedoSchool.LevelGeneration.Runtime.Application
{
    /// <summary>Исход решения о подсказке маршрута.</summary>
    public enum RouteHintOutcome
    {
        /// <summary>У уровня нет пути от старта к выходу — дефект layout, показывать нечего.</summary>
        NoCriticalPath = 0,

        /// <summary>Все комнаты критического пути уже посещены: дальше вести некуда.</summary>
        RouteComplete = 1,

        /// <summary>Цель есть, но ученик ещё не простоял положенное время без прогресса.</summary>
        Waiting = 2,

        /// <summary>Пора показать подсказку.</summary>
        ShowHint = 3
    }

    public readonly struct RouteHintDecision
    {
        public readonly RouteHintOutcome Outcome;
        public readonly CriticalPathDoor TargetDoor;
        public readonly int ProgressIndex;

        public RouteHintDecision(
            RouteHintOutcome outcome,
            CriticalPathDoor targetDoor,
            int progressIndex)
        {
            Outcome = outcome;
            TargetDoor = targetDoor;
            ProgressIndex = progressIndex;
        }

        public bool HasTarget => Outcome == RouteHintOutcome.Waiting || Outcome == RouteHintOutcome.ShowHint;
        public bool ShowHint => Outcome == RouteHintOutcome.ShowHint;

    }

    /// <summary>
    /// Решает, куда и когда вести ученика подсказкой маршрута. Цель — переход из самой дальней уже
    /// посещённой комнаты критического пути в следующую, ещё не посещённую. «Дальняя» считается по индексу
    /// на пути, а не по последнему действию: вернуться назад, побродить по пройденному или посетить тупик
    /// неправильного ответа — не прогресс, и цель от этого не меняется.
    /// Целью всегда остаётся дверь в следующую комнату критического пути, включая дверь с заданием.
    /// Показывать пора, когда простой без прогресса дошёл до порога либо показ включили читом.
    /// Без состояния: таймер простоя и набор открытых комнат держит <see cref="LevelRouteHintSession"/>.
    /// </summary>
    public static class DecideRouteHintUseCase
    {
        public static RouteHintDecision Execute(
            LevelCriticalPath path,
            ICollection<string> visitedRoomIds,
            float idleSeconds,
            float idleThresholdSeconds,
            bool forced)
        {
            if (path == null || path.IsEmpty)
            {
                return new RouteHintDecision(RouteHintOutcome.NoCriticalPath, default, -1);
            }

            int progressIndex = ResolveProgressIndex(path, visitedRoomIds);
            if (progressIndex >= path.Nodes.Count - 1)
            {
                return new RouteHintDecision(RouteHintOutcome.RouteComplete, default, progressIndex);
            }

            CriticalPathDoor target = path.Doors[progressIndex];
            bool due = forced || idleSeconds >= Math.Max(0f, idleThresholdSeconds);
            return new RouteHintDecision(due ? RouteHintOutcome.ShowHint : RouteHintOutcome.Waiting, target,
                progressIndex);
        }

        /// <summary>
        /// Индекс самой дальней посещённой комнаты пути. Старт посещён всегда: с него уровень начинается.
        /// </summary>
        public static int ResolveProgressIndex(LevelCriticalPath path, ICollection<string> visitedRoomIds)
        {
            if (path == null || path.Nodes.Count == 0)
            {
                return -1;
            }

            int progress = 0;
            if (visitedRoomIds == null)
            {
                return progress;
            }

            for (int i = path.Nodes.Count - 1; i > 0; i--)
            {
                if (visitedRoomIds.Contains(path.Nodes[i]))
                {
                    progress = i;
                    break;
                }
            }

            return progress;
        }
    }
}
