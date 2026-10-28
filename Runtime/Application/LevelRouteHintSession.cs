using System;
using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// Состояние подсказки маршрута одной миссии: какие комнаты ученик уже посетил, сколько он простоял
    /// без прогресса и включена ли подсказка принудительно. Решение «куда и когда» принимает
    /// <see cref="DecideRouteHintUseCase"/>, был ли визит прогрессом —
    /// <see cref="DecideRouteHintProgressUseCase"/>; сессия лишь копит входные данные и держит последнее решение.
    /// Время приходит снаружи, поэтому сессия тестируется без сцены.
    /// </summary>
    public sealed class LevelRouteHintSession
    {
        public const float DefaultIdleThresholdSeconds = 60f;

        private readonly HashSet<string> _visitedRoomIds = new(StringComparer.Ordinal);
        private LevelCriticalPath _path = LevelCriticalPath.Empty;
        private LevelLayout _layout;
        private float _idleThresholdSeconds = DefaultIdleThresholdSeconds;

        public LevelCriticalPath CriticalPath => _path;
        public RouteHintDecision Decision { get; private set; }

        /// <summary>Сколько секунд прошло без прогресса (копится только пока управление у игрока).</summary>
        public float IdleSeconds { get; private set; }

        public bool IsForced { get; private set; }
        public bool HasActiveLevel => _layout != null;

        /// <summary>Растёт при каждой смене цели или её вида: вьюшкам пора перестроить путь.</summary>
        public int TargetVersion { get; private set; }

        public float IdleThresholdSeconds
        {
            get => _idleThresholdSeconds;
            set
            {
                float clamped = Math.Max(0f, value);
                if (Math.Abs(clamped - _idleThresholdSeconds) < 0.0001f)
                {
                    return;
                }

                _idleThresholdSeconds = clamped;
                Recalculate();
            }
        }

        /// <summary>
        /// Новый уровень начинается с нуля: посещён только старт, принудительный показ снят (чит
        /// применяет его к новому уровню заново). Перепривязка того же уровня сохраняет посещённое.
        /// </summary>
        public void Bind(LevelLayout layout, bool preserveVisitedRooms = false)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            if (!preserveVisitedRooms)
            {
                _visitedRoomIds.Clear();
                IsForced = false;
            }

            _path = LevelCriticalPath.Build(layout);
            _visitedRoomIds.Add(layout.StartNodeId);
            IdleSeconds = 0f;
            Recalculate();
        }

        public void Reset()
        {
            _layout = null;
            _path = LevelCriticalPath.Empty;
            _visitedRoomIds.Clear();
            IdleSeconds = 0f;
            IsForced = false;
            Recalculate();
        }

        public bool IsRoomVisited(string nodeId) => !string.IsNullOrEmpty(nodeId) && _visitedRoomIds.Contains(nodeId);

        /// <summary>
        /// Ученик вошёл в комнату. Отсчёт простоя начинает заново только прогресс: вход в комнату
        /// критического пути дальше всех посещённых или завершение маршрута. Боковая комната, тупик,
        /// ветка неверного ответа и повторный визит таймер не трогают и показанную подсказку не гасят.
        /// Возвращает true только при продвижении дальше по пути; завершение маршрута обнуляет таймер,
        /// но признаком продвижения не считается — вести уже некуда.
        /// </summary>
        public bool MarkRoomVisited(string nodeId)
        {
            if (!HasActiveLevel || string.IsNullOrEmpty(nodeId) || !_visitedRoomIds.Add(nodeId))
            {
                return false;
            }

            int previousProgress = Decision.ProgressIndex;
            RouteHintProgress verdict =
                DecideRouteHintProgressUseCase.Execute(_path, previousProgress, _visitedRoomIds);
            if (verdict != RouteHintProgress.NoProgress)
            {
                IdleSeconds = 0f;
            }

            Recalculate();
            return verdict == RouteHintProgress.ProgressAdvanced;
        }

        /// <summary>
        /// Задание текущей цели выполнено: это прогресс, отсчёт простоя начинается заново, а подсказка
        /// прячется до нового простоя — вести теперь к следующей цели, путь перепланируется при показе.
        /// </summary>
        public RouteHintProgress NoteTargetTaskCompleted(bool wasTargetTask)
        {
            if (!HasActiveLevel)
            {
                return RouteHintProgress.NoProgress;
            }

            RouteHintProgress verdict = DecideRouteHintProgressUseCase.ExecuteTaskCompleted(wasTargetTask);
            if (verdict != RouteHintProgress.NoProgress)
            {
                IdleSeconds = 0f;
                Recalculate();
            }

            return verdict;
        }

        /// <summary>
        /// Возврат из чекпоинта: набор посещённых комнат ЗАМЕНЯЕТСЯ сохранённым (плюс старт), а простой
        /// отсчитывается заново. Замена, а не дописывание: иначе прогресс, сделанный после сохранения и
        /// потерянный откатом, продолжал бы считаться пройденным.
        /// </summary>
        public void RestoreVisitedRooms(IEnumerable<string> nodeIds)
        {
            if (!HasActiveLevel)
            {
                return;
            }

            _visitedRoomIds.Clear();
            _visitedRoomIds.Add(_layout.StartNodeId);
            if (nodeIds != null)
            {
                foreach (string nodeId in nodeIds)
                {
                    if (!string.IsNullOrEmpty(nodeId))
                    {
                        _visitedRoomIds.Add(nodeId);
                    }
                }
            }

            IdleSeconds = 0f;
            Recalculate();
        }

        /// <summary>
        /// Шаг времени. Простой копится, только пока у ученика есть цель и управление у него: пока открыт
        /// брифинг, чат, код-станция или любая UI-панель, он занят, а не заблудился.
        /// </summary>
        public void Tick(float deltaSeconds, bool playerInControl)
        {
            if (!HasActiveLevel)
            {
                return;
            }

            if (playerInControl && deltaSeconds > 0f && Decision.HasTarget)
            {
                IdleSeconds = Math.Min(IdleSeconds + deltaSeconds, 1_000_000f);
            }

            Recalculate();
        }

        public void SetForced(bool forced)
        {
            if (IsForced == forced)
            {
                return;
            }

            IsForced = forced;
            Recalculate();
        }

        public void ResetIdleTimer()
        {
            IdleSeconds = 0f;
            Recalculate();
        }

        private void Recalculate()
        {
            RouteHintDecision previous = Decision;
            Decision = _layout == null
                ? new RouteHintDecision(RouteHintOutcome.NoCriticalPath, default, -1)
                : DecideRouteHintUseCase.Execute(_path, _visitedRoomIds, IdleSeconds, _idleThresholdSeconds, IsForced);
            if (!previous.TargetDoor.Equals(Decision.TargetDoor)
                || previous.HasTarget != Decision.HasTarget)
            {
                unchecked
                {
                    TargetVersion++;
                }
            }
        }
    }
}
