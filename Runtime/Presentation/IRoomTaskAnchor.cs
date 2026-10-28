using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Место интерактивного задания комнаты. Пока задание не завершено, подсказка ведёт к нему;
    /// после завершения — к двери следующей комнаты пути.
    /// </summary>
    public interface IRoomTaskAnchor
    {
        public bool IsTaskCompleted { get; }
        public bool TryGetTaskWorldPosition(out Vector3 position);
    }
}
