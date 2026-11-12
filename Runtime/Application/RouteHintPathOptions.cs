using UnityEngine;

namespace Neoxider.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// How <see cref="PlanRouteHintPathUseCase"/> shapes the route inside rooms. <see cref="Legacy"/> (all zeros)
    /// is the shortest route with the old 0.45 m wall inset. With a clearance the route keeps that distance
    /// from wall surfaces wherever the room is wide enough, crosses every doorway at its centre
    /// perpendicular to the wall and prefers the middle of rooms; narrow places degrade step by step down to
    /// the legacy inset instead of losing the route.
    /// </summary>
    public readonly struct RouteHintPathOptions
    {
        /// <summary>A clearance at or below the legacy inset is not worth a separate search.</summary>
        public const float MinClearanceMeters = 0.5f;

        public static RouteHintPathOptions Legacy => default;

        /// <summary>Wanted distance from wall surfaces for ordinary route points, metres. Zero — legacy planner.</summary>
        public readonly float ClearanceMeters;

        /// <summary>Straight run through a doorway, perpendicular to the wall, metres from the door plane.</summary>
        public readonly float DoorApproachMeters;

        /// <summary>
        /// How far the route of a target door carries on past the door into the next room, metres. The trail then
        /// goes THROUGH the door instead of stopping at it (owner 2026-10-16). Zero — it ends at the door.
        /// </summary>
        public readonly float DoorLeadOutMeters;

        public RouteHintPathOptions(float clearanceMeters, float doorApproachMeters)
            : this(clearanceMeters, doorApproachMeters, 0f)
        {
        }

        public RouteHintPathOptions(float clearanceMeters, float doorApproachMeters, float doorLeadOutMeters)
        {
            ClearanceMeters = Mathf.Max(0f, clearanceMeters);
            DoorApproachMeters = Mathf.Max(0f, doorApproachMeters);
            DoorLeadOutMeters = Mathf.Max(0f, doorLeadOutMeters);
        }

        public bool UsesClearance => ClearanceMeters > MinClearanceMeters;
    }
}
