using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Application
{
    /// <summary>The reason a route could not be planned, for a log line that says what to fix.</summary>
    public enum RouteHintFailureCause
    {
        None = 0,

        /// <summary>The student is farther from every revealed floor than the start may be snapped from.</summary>
        StudentFarFromFloor = 1,

        /// <summary>The revealed rooms are not connected to the target room through open doors.</summary>
        NoOpenDoorChain = 2,

        /// <summary>There is no free step from where the student stands.</summary>
        StudentWalledIn = 3,

        /// <summary>The rooms are connected, but walls leave no route inside one of them.</summary>
        RoomGeometryBlocksRoute = 4,

        /// <summary>The target door has no geometry (layout or door data defect).</summary>
        TargetDoorUnknown = 5,

        /// <summary>The target room is not revealed on the map yet.</summary>
        TargetRoomHidden = 6
    }

    public readonly struct RouteHintFailure
    {
        public RouteHintFailure(RouteHintFailureCause cause, string roomId, float meters, int lockedDoors)
        {
            Cause = cause;
            RoomId = roomId;
            Meters = meters;
            LockedDoors = lockedDoors;
        }

        public RouteHintFailureCause Cause { get; }
        public string RoomId { get; }
        public float Meters { get; }
        public int LockedDoors { get; }

        /// <summary>Equal for equal causes in the same room: the log writes a line once per change, not per attempt.</summary>
        public int Key => unchecked(((int)Cause * 397) ^ (RoomId != null ? RoomId.GetHashCode() : 0));

        public string Describe()
        {
            string meters = Meters.ToString("0.00", CultureInfo.InvariantCulture);
            switch (Cause)
            {
                case RouteHintFailureCause.StudentFarFromFloor:
                    return "student is " + meters + " m away from the floor of '" + RoomId + "'";
                case RouteHintFailureCause.NoOpenDoorChain:
                    return "no open door chain from '" + RoomId + "' to the target room ("
                           + LockedDoors.ToString(CultureInfo.InvariantCulture) + " locked or disabled door(s) next to the reachable rooms)";
                case RouteHintFailureCause.StudentWalledIn:
                    return "student is walled in inside '" + RoomId + "': no free step (wall clearance "
                           + meters + " m)";
                case RouteHintFailureCause.RoomGeometryBlocksRoute:
                    return "walls leave no route inside '" + RoomId + "' to the target door";
                case RouteHintFailureCause.TargetDoorUnknown:
                    return "target door of '" + RoomId + "' has no geometry";
                case RouteHintFailureCause.TargetRoomHidden:
                    return "target room '" + RoomId + "' is not revealed";
                default:
                    return "unknown";
            }
        }
    }

    public static partial class PlanRouteHintPathUseCase
    {
        /// <summary>
        /// Explains a failed plan: where the student stands relative to the floor, whether open doors connect
        /// the revealed rooms to the target, whether he can take a step at all. Called once after a failure,
        /// never for a successful plan, so it may be thorough rather than fast.
        /// </summary>
        public static RouteHintFailure Diagnose(RouteHintGeometry geometry, Func<string, bool> isRevealed,
            Vector2 studentPosition, string currentRoomId, CriticalPathDoor target,
            Func<RouteHintDoor, bool> isTransitDoorLocked, RouteHintPathOutcome outcome)
        {
            if (outcome == RouteHintPathOutcome.MissingGeometry)
            {
                return new RouteHintFailure(RouteHintFailureCause.TargetDoorUnknown, target.FromNodeId, 0f, 0);
            }

            if (outcome == RouteHintPathOutcome.TargetRoomHidden)
            {
                return new RouteHintFailure(RouteHintFailureCause.TargetRoomHidden, target.FromNodeId, 0f, 0);
            }

            Func<string, bool> revealed = isRevealed ?? (_ => true);
            if (geometry == null || !TryResolveStart(geometry, revealed, studentPosition, currentRoomId,
                    out string startRoom, out Vector2 start)
                || !geometry.TryGetRoom(startRoom, out RouteHintRoom room))
            {
                return new RouteHintFailure(RouteHintFailureCause.StudentFarFromFloor, currentRoomId, float.MaxValue, 0);
            }

            room.TryGetNearestFloorPoint(studentPosition, out Vector2 _, out float gap);
            if (gap > StartSnapMeters)
            {
                return new RouteHintFailure(RouteHintFailureCause.StudentFarFromFloor, startRoom, gap, 0);
            }

            HashSet<string> reachable = new(StringComparer.Ordinal) { startRoom };
            Queue<string> queue = new();
            queue.Enqueue(startRoom);
            int lockedDoors = 0;
            IReadOnlyList<RouteHintDoor> doors = geometry.Doors;
            while (queue.Count > 0)
            {
                string node = queue.Dequeue();
                for (int i = 0; i < doors.Count; i++)
                {
                    RouteHintDoor door = doors[i];
                    if (!door.Touches(node) || !revealed(door.NodeA) || !revealed(door.NodeB))
                    {
                        continue;
                    }

                    if (isTransitDoorLocked != null && isTransitDoorLocked(door) && !door.Matches(target, true))
                    {
                        lockedDoors++;
                        continue;
                    }

                    string other = string.Equals(door.NodeA, node, StringComparison.Ordinal) ? door.NodeB : door.NodeA;
                    if (reachable.Add(other))
                    {
                        queue.Enqueue(other);
                    }
                }
            }

            if (!reachable.Contains(target.FromNodeId))
            {
                return new RouteHintFailure(RouteHintFailureCause.NoOpenDoorChain, startRoom, 0f, lockedDoors);
            }

            if (!HasWalkableStep(room, start))
            {
                return new RouteHintFailure(RouteHintFailureCause.StudentWalledIn, startRoom,
                    room.MeasureWallClearance(start, ClearanceCapMeters), 0);
            }

            return new RouteHintFailure(RouteHintFailureCause.RoomGeometryBlocksRoute, startRoom, 0f, 0);
        }

        private static bool HasWalkableStep(RouteHintRoom room, Vector2 from)
        {
            for (int ring = 0; ring < RecoveryRadii.Length; ring++)
            {
                for (int i = 0; i < RecoveryDirections; i++)
                {
                    float angle = i * (2f * Mathf.PI / RecoveryDirections);
                    Vector2 candidate = from + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * RecoveryRadii[ring];
                    if (room.Contains(candidate, 0f) && IsStepWalkable(room, from, candidate))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
