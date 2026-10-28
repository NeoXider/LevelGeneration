using System;
using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Infrastructure
{
    /// <summary>
    /// Адаптер физики для ленты подсказки: луч вниз к коллайдерам пола из точки на
    /// <c>probeHeightMeters</c> выше предыдущей принятой высоты. Триггеры и коллайдеры игрока пропускаются.
    /// Решение, принять ли попадание, — <see cref="ResolveRouteHintFloorHeightUseCase"/>, не здесь.
    /// <para>
    /// Попадания берутся в буфер на <see cref="MaxHits"/> штук и сортируются по расстоянию: ближайший не
    /// пропущенный коллайдер и есть пол. Если буфер заполнен целиком (столько коллайдеров под одной точкой —
    /// редкость), часть попаданий могла не поместиться, и запрос повторяется без потолка через
    /// <see cref="Physics.RaycastAll(Vector3, Vector3, float, int, QueryTriggerInteraction)"/> — пол не
    /// теряется ценой одной аллокации.
    /// </para>
    /// </summary>
    public sealed class RouteHintFloorRaycastProbe
    {
        public const int MaxHits = 16;
        private const float ProbeDownMeters = 3f;

        private readonly RaycastHit[] _hits = new RaycastHit[MaxHits];
        private readonly LayerMask _layers;
        private readonly float _probeHeightMeters;
        private readonly Transform _ignoredRoot;

        public RouteHintFloorRaycastProbe(LayerMask layers, float probeHeightMeters, Transform ignoredRoot)
        {
            _layers = layers;
            _probeHeightMeters = Mathf.Max(0.05f, probeHeightMeters);
            _ignoredRoot = ignoredRoot;
        }

        /// <summary>Совместим с <see cref="RouteHintFloorProbe"/>.</summary>
        public bool Probe(Vector2 point, float fromFloorHeight, out float hitHeight)
        {
            Vector3 origin = new(point.x, fromFloorHeight + _probeHeightMeters, point.y);
            float distance = _probeHeightMeters + ProbeDownMeters;
            RaycastHit[] hits = _hits;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, hits, distance, _layers,
                QueryTriggerInteraction.Ignore);
            if (count >= MaxHits)
            {
                hits = Physics.RaycastAll(origin, Vector3.down, distance, _layers, QueryTriggerInteraction.Ignore);
                count = hits.Length;
            }

            Array.Sort(hits, 0, count, HitDistanceComparer.Instance);
            for (int i = 0; i < count; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || (_ignoredRoot != null && collider.transform.IsChildOf(_ignoredRoot)))
                {
                    continue;
                }

                hitHeight = hits[i].point.y;
                return true;
            }

            hitHeight = fromFloorHeight;
            return false;
        }

        private sealed class HitDistanceComparer : IComparer<RaycastHit>
        {
            public static readonly HitDistanceComparer Instance = new();

            public int Compare(RaycastHit x, RaycastHit y) => x.distance.CompareTo(y.distance);
        }
    }
}
