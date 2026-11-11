using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;
using RedoSchool.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Infrastructure
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

    /// <summary>
    /// Адаптер физики для планировщика подсказки: снимает с комнаты сплошные предметы на полу (мебель, реквизит,
    /// пульты) их контурами в плане. До него планировщик знал только стены миникарты, и путь шёл сквозь стулья и
    /// столы, а лента на полу поднималась на их крышку. Какой коллайдер — препятствие, решает
    /// <see cref="ClassifyRouteHintColliderUseCase"/>; здесь только факты: сплошной ли это предмет (не триггер,
    /// включён, не часть двери, не стена и не объём комнаты, примитив или выпуклый меш, не подвижное тело), его
    /// высоты и контур. Размеры берутся из формы коллайдера и его трансформа, а не из <c>Collider.bounds</c>:
    /// дальние комнаты при адаптивной загрузке выключены, а у выключенного коллайдера границы пустые.
    /// </summary>
    public static class RouteHintObstacleCollector
    {
        /// <summary>Ось коробки ближе к вертикали, чем этот косинус, — коробка стоит прямо, её контур — прямоугольник.</summary>
        private const float UprightCosine = 0.99f;

        public static void Collect(Transform roomRoot, float floorHeight, float roomFloorAreaSquareMeters,
            float stepMeters, IReadOnlyList<Transform> excludedRoots, List<RouteHintObstacle> output)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            output.Clear();
            if (roomRoot == null)
            {
                return;
            }

            Collider[] colliders = roomRoot.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (!IsSolidProp(collider, roomRoot, excludedRoots)
                    || !TryMeasure(collider, out RouteHintObstacle footprint, out float bottom, out float top))
                {
                    continue;
                }

                RouteHintColliderRole role = ClassifyRouteHintColliderUseCase.Execute(true, bottom - floorHeight,
                    top - floorHeight, footprint.Area, roomFloorAreaSquareMeters, stepMeters);
                if (role == RouteHintColliderRole.Obstacle)
                {
                    output.Add(footprint);
                }
            }
        }

        private static bool IsSolidProp(Collider collider, Transform roomRoot, IReadOnlyList<Transform> excludedRoots)
        {
            if (collider == null || !collider.enabled || collider.isTrigger || !IsPropShape(collider)
                || !IsActiveWithinRoom(collider.transform, roomRoot)
                || IsUnderAny(collider.transform, excludedRoots)
                || collider.GetComponentInParent<DoorSocket>(true) != null
                || collider.GetComponent<RoomBounds>() != null)
            {
                return false;
            }

            Rigidbody body = collider.GetComponentInParent<Rigidbody>(true);
            return body == null || body.isKinematic;
        }

        /// <summary>
        /// Предметы — примитивы и выпуклые меши. Невыпуклый меш-коллайдер — это архитектура (стены, пол, своды):
        /// его границы накрыли бы проходы.
        /// </summary>
        private static bool IsPropShape(Collider collider) => collider switch
        {
            BoxCollider => true,
            SphereCollider => true,
            CapsuleCollider => true,
            MeshCollider mesh => mesh.convex && mesh.sharedMesh != null,
            _ => false
        };

        /// <summary>Включён ли предмет внутри комнаты; включена ли сама комната — неважно (адаптивная загрузка).</summary>
        private static bool IsActiveWithinRoom(Transform transform, Transform roomRoot)
        {
            for (Transform current = transform; current != null && current != roomRoot; current = current.parent)
            {
                if (!current.gameObject.activeSelf)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsUnderAny(Transform transform, IReadOnlyList<Transform> roots)
        {
            if (roots == null)
            {
                return false;
            }

            for (int i = 0; i < roots.Count; i++)
            {
                if (roots[i] != null && transform.IsChildOf(roots[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryMeasure(Collider collider, out RouteHintObstacle footprint, out float bottom,
            out float top)
        {
            Transform transform = collider.transform;
            switch (collider)
            {
                case BoxCollider box:
                    return TryMeasureBox(transform, box.center, box.size, out footprint, out bottom, out top);
                case MeshCollider mesh:
                {
                    Bounds local = mesh.sharedMesh.bounds;
                    return TryMeasureBox(transform, local.center, local.size, out footprint, out bottom, out top);
                }
                case CapsuleCollider capsule:
                {
                    float diameter = capsule.radius * 2f;
                    Vector3 size = new(diameter, diameter, diameter);
                    size[Mathf.Clamp(capsule.direction, 0, 2)] = Mathf.Max(capsule.height, diameter);
                    return TryMeasureBox(transform, capsule.center, size, out footprint, out bottom, out top);
                }
                case SphereCollider sphere:
                {
                    Vector3 scale = transform.lossyScale;
                    float radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y),
                        Mathf.Abs(scale.z));
                    Vector3 center = transform.TransformPoint(sphere.center);
                    footprint = RouteHintObstacle.FromRect(new Rect(center.x - radius, center.z - radius,
                        radius * 2f, radius * 2f));
                    bottom = center.y - radius;
                    top = center.y + radius;
                    return radius > 0.001f;
                }
                default:
                    footprint = default;
                    bottom = 0f;
                    top = 0f;
                    return false;
            }
        }

        /// <summary>
        /// Контур коробки в плане: стоит прямо — повёрнутый прямоугольник по двум её горизонтальным осям, наклонена —
        /// прямоугольник по осям мира, охватывающий все восемь углов.
        /// </summary>
        private static bool TryMeasureBox(Transform transform, Vector3 localCenter, Vector3 localSize,
            out RouteHintObstacle footprint, out float bottom, out float top)
        {
            Vector3 center = transform.TransformPoint(localCenter);
            Vector3 x = transform.TransformVector(new Vector3(localSize.x * 0.5f, 0f, 0f));
            Vector3 y = transform.TransformVector(new Vector3(0f, localSize.y * 0.5f, 0f));
            Vector3 z = transform.TransformVector(new Vector3(0f, 0f, localSize.z * 0.5f));
            float height = Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y);
            bottom = center.y - height;
            top = center.y + height;

            Vector2 planCenter = new(center.x, center.z);
            if (IsVertical(y))
            {
                footprint = RouteHintObstacle.FromBox(planCenter, new Vector2(x.x, x.z), new Vector2(z.x, z.z));
            }
            else if (IsVertical(x))
            {
                footprint = RouteHintObstacle.FromBox(planCenter, new Vector2(y.x, y.z), new Vector2(z.x, z.z));
            }
            else if (IsVertical(z))
            {
                footprint = RouteHintObstacle.FromBox(planCenter, new Vector2(x.x, x.z), new Vector2(y.x, y.z));
            }
            else
            {
                float halfX = Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x);
                float halfZ = Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z);
                footprint = RouteHintObstacle.FromRect(new Rect(center.x - halfX, center.z - halfZ, halfX * 2f,
                    halfZ * 2f));
            }

            return footprint.HasArea;
        }

        private static bool IsVertical(Vector3 axis) =>
            axis.sqrMagnitude > 0.0000001f && Mathf.Abs(axis.normalized.y) >= UprightCosine;
    }
}
