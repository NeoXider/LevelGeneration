using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Application
{
    /// <summary>Where the student is relative to the shown trail after one <see cref="RouteHintTrail.Follow"/>.</summary>
    public readonly struct RouteHintTrailFollow
    {
        public RouteHintTrailFollow(bool hasTrail, float deviationMeters, bool onTrail)
        {
            HasTrail = hasTrail;
            DeviationMeters = deviationMeters;
            OnTrail = onTrail;
        }

        public bool HasTrail { get; }

        /// <summary>Distance from the student to the nearest point of the trail, metres.</summary>
        public float DeviationMeters { get; }

        /// <summary>The student is close enough to the trail for it to follow him.</summary>
        public bool OnTrail { get; }
    }

    /// <summary>
    /// THE route-hint polyline: one smoothed list of world-XZ points that both the floor stars and the minimap
    /// dashes draw, so they can never disagree. It is replaced only at a re-plan (<see cref="Revision"/> grows);
    /// between re-plans only the visible start moves, along the same points: the trail begins
    /// <c>startOffset</c> metres ahead of the student's projection on it. Per-point wall clearance travels
    /// with the points, so the star stream can keep its scatter off the walls.
    /// Pure data and arithmetic: no scene, no allocation after the lists reach their working size.
    /// </summary>
    public sealed class RouteHintTrail
    {
        private readonly List<Vector2> _points = new();
        private readonly List<float> _distances = new();
        private readonly List<float> _clearances = new();
        private readonly List<Vector2> _blendFrom = new();
        private readonly List<float> _blendFromDistances = new();
        private readonly List<Vector2> _blendTo = new();
        private readonly List<float> _floorHeights = new();
        private int _floorHeightsPlan = -1;
        private float _blendFromLength;
        private float _blendStartTime;
        private float _blendSeconds;
        private float _progressMeters;

        /// <summary>A re-plan this far longer or shorter than the shown trail is a different route: no morph, a cut.</summary>
        public const float MaxBlendLengthChangeMeters = 2.5f;

        public IReadOnlyList<Vector2> Points => _points;

        /// <summary>Distance of every point from the first one, metres.</summary>
        public IReadOnlyList<float> Distances => _distances;

        /// <summary>Distance from each point to the nearest wall surface, metres (capped).</summary>
        public IReadOnlyList<float> Clearances => _clearances;

        /// <summary>
        /// Grows whenever the points change — every <see cref="Assign"/> and every frame of a blend
        /// (<see cref="AdvanceBlend"/>): views rebuild what they derive from the points.
        /// </summary>
        public int Revision { get; private set; }

        /// <summary>
        /// Grows with every re-plan (<see cref="Assign"/>, <see cref="AssignBlended"/>, <see cref="Clear"/>) but NOT
        /// with the frames of a blend. What is expensive to derive and depends only on the planned route — the floor
        /// heights under it (raycasts) — is keyed on this, not on <see cref="Revision"/>.
        /// </summary>
        public int PlanVersion { get; private set; }

        /// <summary>
        /// Where the points end up: the planner's points. During a blend <see cref="Points"/> are still melting into
        /// them; otherwise both are the same list. Same count as <see cref="Points"/>.
        /// </summary>
        public IReadOnlyList<Vector2> PlannedPoints => IsBlending ? _blendTo : _points;

        /// <summary>
        /// Floor height under each point of the current plan (<see cref="SetFloorHeights"/>), probed once per re-plan
        /// at <see cref="PlannedPoints"/>; a blend moves the points only in XZ and keeps these heights.
        /// </summary>
        public IReadOnlyList<float> FloorHeights => _floorHeights;

        /// <summary>The floor heights belong to the current plan and cover every point.</summary>
        public bool HasFloorHeights => _floorHeightsPlan == PlanVersion && _floorHeights.Count == _points.Count
                                       && _points.Count > 0;

        /// <summary>Stores the floor heights probed for the current plan (one per point).</summary>
        public void SetFloorHeights(IReadOnlyList<float> heights)
        {
            if (heights == null)
            {
                throw new ArgumentNullException(nameof(heights));
            }

            _floorHeights.Clear();
            for (int i = 0; i < heights.Count; i++)
            {
                _floorHeights.Add(heights[i]);
            }

            _floorHeightsPlan = PlanVersion;
        }

        public float LengthMeters { get; private set; }

        public bool HasPoints => _points.Count >= 2;

        /// <summary>A re-plan is melting into the shown trail (<see cref="AssignBlended"/>); see <see cref="AdvanceBlend"/>.</summary>
        public bool IsBlending { get; private set; }

        /// <summary>Where the visible trail starts, metres along the points.</summary>
        public float HeadDistance { get; private set; }

        public Vector2 HeadPoint { get; private set; }

        /// <summary>Metres of trail left to draw, from the head to the target.</summary>
        public float RemainingMeters => Mathf.Max(0f, LengthMeters - HeadDistance);

        /// <summary>The student's own position along the trail, metres.</summary>
        public float ProgressMeters => _progressMeters;

        public void Clear()
        {
            _points.Clear();
            _distances.Clear();
            _clearances.Clear();
            LengthMeters = 0f;
            HeadDistance = 0f;
            HeadPoint = default;
            _progressMeters = 0f;
            IsBlending = false;
            unchecked
            {
                Revision++;
                PlanVersion++;
            }
        }

        /// <summary>
        /// Replaces the points (a re-plan). The head restarts at the first point plus the start offset; the
        /// caller follows the student right away. <paramref name="clearances"/> may be null (unknown).
        /// </summary>
        public void Assign(IReadOnlyList<Vector2> points, IReadOnlyList<float> clearances, float clearanceCapMeters,
            float startOffsetMeters, float minVisibleMeters)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            _points.Clear();
            _distances.Clear();
            _clearances.Clear();
            float total = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    total += Vector2.Distance(points[i - 1], points[i]);
                }

                _points.Add(points[i]);
                _distances.Add(total);
                _clearances.Add(clearances != null && i < clearances.Count ? clearances[i] : clearanceCapMeters);
            }

            LengthMeters = total;
            _progressMeters = 0f;
            IsBlending = false;
            SetHead(0f, startOffsetMeters, minVisibleMeters);
            unchecked
            {
                Revision++;
                PlanVersion++;
            }
        }

        /// <summary>
        /// A re-plan of the SAME route (the student drifted off the line, nothing in the world changed): the new
        /// points replace the old ones, but the shown trail starts exactly as the old one looked and melts into
        /// the new one over <paramref name="seconds"/> (<see cref="AdvanceBlend"/> every frame) instead of
        /// jumping sideways. Both lines end at the same door, so they are matched by the distance left to
        /// it. A different route (length differs by more than <see cref="MaxBlendLengthChangeMeters"/>) or no
        /// trail yet is a plain <see cref="Assign"/>.
        /// </summary>
        public void AssignBlended(IReadOnlyList<Vector2> points, IReadOnlyList<float> clearances,
            float clearanceCapMeters, float startOffsetMeters, float minVisibleMeters, float now, float seconds)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            float newLength = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                newLength += Vector2.Distance(points[i - 1], points[i]);
            }

            bool morph = seconds > 0.0001f && HasPoints && points.Count >= 2
                         && Mathf.Abs(newLength - LengthMeters) <= MaxBlendLengthChangeMeters;
            if (morph)
            {
                _blendFrom.Clear();
                _blendFromDistances.Clear();
                _blendFrom.AddRange(_points);
                _blendFromDistances.AddRange(_distances);
                _blendFromLength = LengthMeters;
            }

            Assign(points, clearances, clearanceCapMeters, startOffsetMeters, minVisibleMeters);
            if (!morph)
            {
                return;
            }

            _blendTo.Clear();
            _blendTo.AddRange(points);
            _blendStartTime = now;
            _blendSeconds = seconds;
            IsBlending = true;
            ApplyBlend(0f);
        }

        /// <summary>
        /// One frame of the morph started by <see cref="AssignBlended"/>. Returns whether the points changed
        /// (the views rebuild what they derive from them: <see cref="Revision"/> grew). The last frame leaves
        /// exactly the planner's points.
        /// </summary>
        public bool AdvanceBlend(float now)
        {
            if (!IsBlending)
            {
                return false;
            }

            float t = Mathf.Clamp01((now - _blendStartTime) / Mathf.Max(0.0001f, _blendSeconds));
            ApplyBlend(t);
            if (t >= 1f)
            {
                IsBlending = false;
            }

            unchecked
            {
                Revision++;
            }

            return true;
        }

        private void ApplyBlend(float t)
        {
            float eased = t * t * (3f - 2f * t);
            float newLength = 0f;
            for (int i = 1; i < _blendTo.Count; i++)
            {
                newLength += Vector2.Distance(_blendTo[i - 1], _blendTo[i]);
            }

            float total = 0f;
            float along = 0f;
            for (int i = 0; i < _blendTo.Count; i++)
            {
                if (i > 0)
                {
                    along += Vector2.Distance(_blendTo[i - 1], _blendTo[i]);
                }

                Vector2 target = _blendTo[i];
                Vector2 point = target;
                if (eased < 1f)
                {
                    point = Vector2.Lerp(OldPointAtDistanceFromStart(_blendFromLength - (newLength - along)), target,
                        eased);
                }

                _points[i] = point;
                if (i > 0)
                {
                    total += Vector2.Distance(_points[i - 1], point);
                }

                _distances[i] = total;
            }

            LengthMeters = total;
        }

        /// <summary>Where the old line was <paramref name="distance"/> metres from its start; before the start it extends backwards.</summary>
        private Vector2 OldPointAtDistanceFromStart(float distance)
        {
            int count = _blendFrom.Count;
            if (distance <= 0f)
            {
                Vector2 first = _blendFrom[0];
                Vector2 direction = first - _blendFrom[1];
                return direction.sqrMagnitude > 0.000001f ? first + direction.normalized * -distance : first;
            }

            if (distance >= _blendFromLength)
            {
                return _blendFrom[count - 1];
            }

            int to = 1;
            while (to < count - 1 && _blendFromDistances[to] < distance)
            {
                to++;
            }

            float span = _blendFromDistances[to] - _blendFromDistances[to - 1];
            float t = span > 0.0001f ? Mathf.Clamp01((distance - _blendFromDistances[to - 1]) / span) : 0f;
            return Vector2.Lerp(_blendFrom[to - 1], _blendFrom[to], t);
        }

        /// <summary>
        /// Moves the visible start after the student. Within <paramref name="deviationLimitMeters"/> of the trail
        /// the head follows his projection plus the start offset (never closer than <paramref name="minVisibleMeters"/>
        /// to the target); farther away it stays where it was and the caller decides about a re-plan.
        /// </summary>
        public RouteHintTrailFollow Follow(Vector2 student, float startOffsetMeters, float minVisibleMeters,
            float deviationLimitMeters)
        {
            if (!HasPoints)
            {
                return new RouteHintTrailFollow(false, float.MaxValue, false);
            }

            float progress = Project(student, out float deviation);
            bool onTrail = deviation <= deviationLimitMeters;
            if (onTrail)
            {
                _progressMeters = progress;
                SetHead(progress, startOffsetMeters, minVisibleMeters);
            }

            return new RouteHintTrailFollow(true, deviation, onTrail);
        }

        /// <summary>Point at <paramref name="distance"/> metres along the trail, clamped to its ends.</summary>
        public Vector2 PointAt(float distance)
        {
            if (_points.Count == 0)
            {
                return default;
            }

            if (_points.Count == 1 || distance <= 0f)
            {
                return _points[0];
            }

            if (distance >= LengthMeters)
            {
                return _points[_points.Count - 1];
            }

            int to = FirstIndexAtOrAfter(distance);
            int from = to - 1;
            float span = _distances[to] - _distances[from];
            float t = span > 0.0001f ? Mathf.Clamp01((distance - _distances[from]) / span) : 0f;
            return Vector2.Lerp(_points[from], _points[to], t);
        }

        /// <summary>
        /// The drawn polyline: the head, then every point beyond it. This is what the minimap projects;
        /// the floor view uses the same points with the same head.
        /// </summary>
        public void FillVisible(List<Vector2> output)
        {
            output.Clear();
            if (!HasPoints)
            {
                return;
            }

            output.Add(HeadPoint);
            for (int i = 0; i < _points.Count; i++)
            {
                if (_distances[i] > HeadDistance + 0.0001f)
                {
                    output.Add(_points[i]);
                }
            }
        }

        private void SetHead(float progressMeters, float startOffsetMeters, float minVisibleMeters)
        {
            float latest = Mathf.Max(progressMeters, LengthMeters - Mathf.Max(0f, minVisibleMeters));
            HeadDistance = Mathf.Clamp(progressMeters + Mathf.Max(0f, startOffsetMeters), progressMeters, latest);
            HeadPoint = PointAt(HeadDistance);
        }

        /// <summary>Distance along the trail of the point nearest to <paramref name="student"/>.</summary>
        private float Project(Vector2 student, out float deviation)
        {
            float bestDeviationSquared = float.MaxValue;
            float bestScore = float.MaxValue;
            float bestDistance = 0f;
            for (int i = 1; i < _points.Count; i++)
            {
                Vector2 a = _points[i - 1];
                Vector2 segment = _points[i] - a;
                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared > 0.000001f
                    ? Mathf.Clamp01(Vector2.Dot(student - a, segment) / lengthSquared) : 0f;
                float distanceSquared = (student - (a + segment * t)).sqrMagnitude;
                float along = _distances[i - 1] + (_distances[i] - _distances[i - 1]) * t;
                // A path that doubles back near itself: of two equally near candidates, stay with the one
                // closest to where the student already was.
                float score = Mathf.Sqrt(distanceSquared) + Mathf.Abs(along - _progressMeters) * 0.0005f;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestDeviationSquared = distanceSquared;
                    bestDistance = along;
                }
            }

            deviation = Mathf.Sqrt(bestDeviationSquared);
            return bestDistance;
        }

        private int FirstIndexAtOrAfter(float distance)
        {
            int low = 1;
            int high = _distances.Count - 1;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (_distances[middle] < distance)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }
    }

    /// <summary>
    /// Builds the shared trail from the planner's polyline: smooths the corners once (so the floor stars and
    /// the minimap draw the same curve) and measures the free width along it. Corners are rounded within the
    /// configured deviation, so a route planned with that much extra clearance keeps its clearance after
    /// smoothing.
    /// </summary>
    public static class RouteHintTrailBuilder
    {
        public static void Build(RouteHintGeometry geometry, IReadOnlyList<Vector2> route,
            RouteHintRibbonShape smoothing, float clearanceCapMeters, float startOffsetMeters,
            float minVisibleMeters, RouteHintTrail trail, float blendNow = 0f, float blendSeconds = 0f)
        {
            if (trail == null)
            {
                throw new ArgumentNullException(nameof(trail));
            }

            List<Vector2> axis = new();
            RouteHintRibbonFailure failure = RouteHintRibbonMeshBuilder.SmoothCorners(route, smoothing, axis);
            if (failure != RouteHintRibbonFailure.None)
            {
                // Degenerate (zero length) or too complex: keep the planner's own points, unsmoothed.
                axis.Clear();
                for (int i = 0; i < route.Count; i++)
                {
                    axis.Add(route[i]);
                }
            }

            List<float> clearances = new(axis.Count);
            for (int i = 0; i < axis.Count; i++)
            {
                clearances.Add(geometry != null
                    ? geometry.MeasureWallClearance(axis[i], clearanceCapMeters) : clearanceCapMeters);
            }

            if (blendSeconds > 0f)
            {
                trail.AssignBlended(axis, clearances, clearanceCapMeters, startOffsetMeters, minVisibleMeters,
                    blendNow, blendSeconds);
                return;
            }

            trail.Assign(axis, clearances, clearanceCapMeters, startOffsetMeters, minVisibleMeters);
        }
    }
}
