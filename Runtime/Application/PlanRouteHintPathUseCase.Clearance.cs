using System.Collections.Generic;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Application
{
    /// <summary>Visibility graph of one room for one clearance level; built once, valid until a doorway is registered.</summary>
    internal sealed class RouteHintNavigationGraph
    {
        public RouteHintNavigationGraph(List<Vector2> points, float[,] distances)
        {
            Points = points;
            Distances = distances;
        }

        public List<Vector2> Points { get; }
        public float[,] Distances { get; }
    }

    /// <summary>
    /// Clearance-aware routing inside one room (<see cref="RouteHintPathOptions"/>). A leg between two points of a
    /// room is searched on a visibility graph whose edges keep a clearance from walls and from the outlines of solid
    /// props on the floor (<see cref="RouteHintRoom.Blockers"/>); the clearance steps down
    /// (full, three quarters, a little over half) before the legacy 0.45 m search takes over, so a narrow room
    /// never loses its route. Doorways are entered and left through an approach point straight in front of
    /// the door, and an endpoint that stands too close to a wall (student, task panel) is pulled to the nearest
    /// point with the wanted clearance first.
    /// </summary>
    public static partial class PlanRouteHintPathUseCase
    {
        private const float ClearanceCapMeters = 4f;
        private const float ClearanceSlackMeters = 0.001f;
        private const float ApproachExtraMeters = 0.3f;
        private const int EndpointDirections = 16;
        private static readonly float[] ClearanceLadder = { 1f, 0.75f, 0.55f };
        private static readonly float[] EndpointRadii = { 0.3f, 0.6f, 0.9f, 1.2f, 1.5f };

        // Centre first (the centre line of corridors), then the quarter points of every floor cell: room to go
        // around an inner corner or a pillar at the wanted clearance (dense graph only).
        private static readonly Vector2[] LatticeFractions =
        {
            new Vector2(0.5f, 0.5f), new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f),
            new Vector2(0.25f, 0.75f), new Vector2(0.75f, 0.75f)
        };

        private static bool TryFindRoomPathClear(RouteHintRoom room, Vector2 from, Vector2 to, List<Vector2> result,
            bool fromPlayer, RouteHintPathOptions options)
        {
            if (room == null || room.Cells.Count == 0)
            {
                return false;
            }

            if (Vector2.Distance(from, to) < DuplicatePointMeters)
            {
                AppendPoint(result, to);
                return true;
            }

            List<Vector2> leg = new();
            for (int level = 0; level < ClearanceLadder.Length; level++)
            {
                float clearance = options.ClearanceMeters * ClearanceLadder[level];
                if (clearance <= RouteHintPathOptions.MinClearanceMeters)
                {
                    break;
                }

                leg.Clear();
                if (!TryFindLegAtClearance(room, from, to, fromPlayer, clearance, options, leg))
                {
                    continue;
                }

                for (int i = 0; i < leg.Count; i++)
                {
                    AppendPoint(result, leg[i]);
                }

                return true;
            }

            return TryFindRoomPathLegacy(room, from, to, result, fromPlayer);
        }

        private static bool TryFindLegAtClearance(RouteHintRoom room, Vector2 from, Vector2 to, bool fromPlayer,
            float clearance, RouteHintPathOptions options, List<Vector2> leg)
        {
            if (!ResolveLegEnd(room, from, to, clearance, options, fromPlayer, out Vector2 start)
                || !ResolveLegEnd(room, to, from, clearance, options, false, out Vector2 end))
            {
                return false;
            }

            if (start != from)
            {
                leg.Add(start);
            }

            if (!TryFindClearPath(room, start, end, clearance, leg))
            {
                return false;
            }

            if (end != to)
            {
                leg.Add(to);
            }

            return true;
        }

        /// <summary>
        /// The point a leg is routed from or to. A doorway becomes its approach point (the doorway itself is
        /// appended by the caller), a point too close to a wall becomes the nearest point with the wanted
        /// clearance that the original point can step to; anything else stays as it is.
        /// </summary>
        private static bool ResolveLegEnd(RouteHintRoom room, Vector2 point, Vector2 other, float clearance,
            RouteHintPathOptions options, bool fromPlayer, out Vector2 resolved)
        {
            resolved = point;
            if (room.IsPortal(point))
            {
                float approach = Mathf.Max(options.DoorApproachMeters, clearance + ApproachExtraMeters);
                // The other end is already in front of the doorway: no detour backwards to an approach point,
                // the short direct leg of the legacy search is the right one.
                if (options.DoorApproachMeters <= 0f || Vector2.Distance(point, other) <= approach + 0.1f
                    || !room.TryGetPortalInward(point, out Vector2 inward))
                {
                    return false;
                }

                for (int attempt = 0; attempt < 4; attempt++, approach *= 0.75f)
                {
                    Vector2 candidate = point + inward * approach;
                    if (room.Contains(candidate, 0f)
                        && ClearanceAt(room, candidate) >= clearance - ClearanceSlackMeters
                        && IsSegmentInside(room, point, candidate))
                    {
                        resolved = candidate;
                        return true;
                    }
                }

                return false;
            }

            if (ClearanceAt(room, point) >= clearance - ClearanceSlackMeters)
            {
                return true;
            }

            for (int ring = 0; ring < EndpointRadii.Length; ring++)
            {
                bool found = false;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < EndpointDirections; i++)
                {
                    float angle = i * (2f * Mathf.PI / EndpointDirections);
                    Vector2 candidate = point + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * EndpointRadii[ring];
                    if (!room.Contains(candidate, 0f)
                        || ClearanceAt(room, candidate) < clearance - ClearanceSlackMeters
                        || !(fromPlayer
                            ? IsSegmentInsideFromPlayer(room, point, candidate)
                            : IsSegmentInside(room, point, candidate)))
                    {
                        continue;
                    }

                    float distance = Vector2.Distance(point, candidate);
                    if (distance < bestDistance)
                    {
                        found = true;
                        bestDistance = distance;
                        resolved = candidate;
                    }
                }

                if (found)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Appends the points after <paramref name="from"/> up to and including <paramref name="to"/>. The cheap
        /// graph (cell centres and partition ends) is tried first; the dense one (quarter points of every cell,
        /// built only when the room needs it) finds the way around pillars and corners in open halls.
        /// </summary>
        private static bool TryFindClearPath(RouteHintRoom room, Vector2 from, Vector2 to, float clearance,
            List<Vector2> result)
        {
            if (Vector2.Distance(from, to) < DuplicatePointMeters || IsSegmentClear(room, from, to, clearance))
            {
                AppendPoint(result, to);
                return true;
            }

            return TryFindClearPathOnGraph(room, from, to, clearance, false, result)
                   || TryFindClearPathOnGraph(room, from, to, clearance, true, result);
        }

        private static bool TryFindClearPathOnGraph(RouteHintRoom room, Vector2 from, Vector2 to, float clearance,
            bool dense, List<Vector2> result)
        {
            RouteHintNavigationGraph graph = EnsureClearanceGraph(room, clearance, dense);
            List<Vector2> candidates = new(graph.Points.Count + 2) { from, to };
            candidates.AddRange(graph.Points);
            int count = candidates.Count;
            float[] startCosts = new float[count];
            float[] targetCosts = new float[count];
            startCosts[1] = float.MaxValue;
            for (int i = 2; i < count; i++)
            {
                startCosts[i] = IsSegmentClear(room, from, candidates[i], clearance)
                    ? Vector2.Distance(from, candidates[i]) : float.MaxValue;
                targetCosts[i] = IsSegmentClear(room, to, candidates[i], clearance)
                    ? Vector2.Distance(to, candidates[i]) : float.MaxValue;
            }

            float[] distance = new float[count];
            int[] previous = new int[count];
            bool[] done = new bool[count];
            for (int i = 0; i < count; i++)
            {
                distance[i] = float.MaxValue;
                previous[i] = -1;
            }

            distance[0] = 0f;
            for (int step = 0; step < count; step++)
            {
                int current = -1;
                for (int i = 0; i < count; i++)
                {
                    if (!done[i] && distance[i] < float.MaxValue && (current < 0 || distance[i] < distance[current]))
                    {
                        current = i;
                    }
                }

                if (current < 0 || current == 1)
                {
                    break;
                }

                done[current] = true;
                for (int next = 1; next < count; next++)
                {
                    if (done[next])
                    {
                        continue;
                    }

                    float edge = current == 0 ? startCosts[next]
                        : next == 1 ? targetCosts[current]
                        : graph.Distances[current - 2, next - 2];
                    if (edge == float.MaxValue)
                    {
                        continue;
                    }

                    float candidate = distance[current] + edge;
                    if (candidate < distance[next])
                    {
                        distance[next] = candidate;
                        previous[next] = current;
                    }
                }
            }

            if (distance[1] == float.MaxValue)
            {
                return false;
            }

            List<Vector2> path = new();
            for (int cursor = 1; cursor > 0; cursor = previous[cursor])
            {
                path.Add(candidates[cursor]);
            }

            for (int i = path.Count - 1; i >= 0; i--)
            {
                AppendPoint(result, path[i]);
            }

            return true;
        }

        private static float ClearanceAt(RouteHintRoom room, Vector2 point) =>
            room.MeasureWallClearance(point, ClearanceCapMeters);

        /// <summary>The whole segment lies on the floor and keeps <paramref name="clearance"/> from every wall surface.</summary>
        private static bool IsSegmentClear(RouteHintRoom room, Vector2 from, Vector2 to, float clearance)
        {
            if (!IsCoveredByFloor(room, from, to) || room.IsInsideObstacle(from) || room.IsInsideObstacle(to))
            {
                return false;
            }

            for (int i = 0; i < room.Blockers.Count; i++)
            {
                RouteHintWall wall = room.Blockers[i];
                float limit = wall.WidthMeters * 0.5f + clearance;
                if (Mathf.Max(from.x, to.x) + limit < Mathf.Min(wall.A.x, wall.B.x)
                    || Mathf.Min(from.x, to.x) - limit > Mathf.Max(wall.A.x, wall.B.x)
                    || Mathf.Max(from.y, to.y) + limit < Mathf.Min(wall.A.y, wall.B.y)
                    || Mathf.Min(from.y, to.y) - limit > Mathf.Max(wall.A.y, wall.B.y))
                {
                    continue;
                }

                if (SegmentsDistanceSquared(from, to, wall.A, wall.B) < limit * limit)
                {
                    return false;
                }
            }

            return true;
        }

        private static RouteHintNavigationGraph EnsureClearanceGraph(RouteHintRoom room, float clearance, bool dense)
        {
            if (room.ClearanceGraphs.TryGetValue((clearance, dense), out RouteHintNavigationGraph cached))
            {
                return cached;
            }

            List<Vector2> candidates = new();
            for (int i = 0; i < room.Cells.Count; i++)
            {
                MinimapCellSnapshot cell = room.Cells[i];
                int fractions = dense ? LatticeFractions.Length : 1;
                for (int q = 0; q < fractions; q++)
                {
                    AddClearCandidate(room, candidates, room.LocalToWorld(new Vector2(
                        cell.X + cell.Width * LatticeFractions[q].x,
                        cell.Y + cell.Depth * LatticeFractions[q].y)), clearance);
                }
            }

            for (int i = 0; i < room.Blockers.Count; i++)
            {
                RouteHintWall wall = room.Blockers[i];
                Vector2 direction = (wall.B - wall.A).normalized;
                if (wall.IsObstacleEdge || direction.sqrMagnitude < 0.5f)
                {
                    continue;
                }

                Vector2 normal = new(-direction.y, direction.x);
                float offset = Mathf.Max(0.22f, wall.WidthMeters * 0.5f + clearance + 0.05f);
                Vector2 midpoint = (wall.A + wall.B) * 0.5f;
                if (!room.Contains(midpoint + normal * offset, 0f) || !room.Contains(midpoint - normal * offset, 0f))
                {
                    continue;
                }

                AddClearCandidate(room, candidates, wall.A - direction * offset + normal * offset, clearance);
                AddClearCandidate(room, candidates, wall.A - direction * offset - normal * offset, clearance);
                AddClearCandidate(room, candidates, wall.B + direction * offset + normal * offset, clearance);
                AddClearCandidate(room, candidates, wall.B + direction * offset - normal * offset, clearance);
            }

            // Предметы обходятся по углам: одна точка на угол (см. TryObstacleCornerDetour), а не четыре на сторону.
            float obstacleOffset = Mathf.Max(0.22f, clearance + 0.05f);
            for (int i = 0; i < room.Obstacles.Count; i++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    if (TryObstacleCornerDetour(room.Obstacles[i], corner, obstacleOffset, out Vector2 detour))
                    {
                        AddClearCandidate(room, candidates, detour, clearance);
                    }
                }
            }

            int count = candidates.Count;
            float[,] distances = new float[count, count];
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    float cost = IsSegmentClear(room, candidates[i], candidates[j], clearance)
                        ? Vector2.Distance(candidates[i], candidates[j]) : float.MaxValue;
                    distances[i, j] = cost;
                    distances[j, i] = cost;
                }
            }

            RouteHintNavigationGraph graph = new(candidates, distances);
            room.ClearanceGraphs.Add((clearance, dense), graph);
            return graph;
        }

        private static void AddClearCandidate(RouteHintRoom room, List<Vector2> candidates, Vector2 point,
            float clearance)
        {
            if (room.Contains(point, 0f) && ClearanceAt(room, point) >= clearance - ClearanceSlackMeters)
            {
                candidates.Add(point);
            }
        }
    }
}
