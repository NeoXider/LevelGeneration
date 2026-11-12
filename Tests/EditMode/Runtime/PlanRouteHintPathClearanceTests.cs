using System;
using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// The planner keeps the route off the walls (owner 2026-10-16: the golden stars ran right along walls and
    /// pillars) and no longer gives up on a player standing in a doorway (hint never appeared for 2000+ s).
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class PlanRouteHintPathClearanceTests
    {
        private const float WantedClearance = 0.9f;
        private const float Tolerance = 0.02f;

        /// <summary>Planner clearance = wanted clearance + the corner smoothing deviation (as the settings do).</summary>
        private static readonly RouteHintPathOptions Clear = new(WantedClearance + 0.12f, 1.4f);

        [TestCase(false)]
        [TestCase(true)]
        public void Plan_PlayerStandingInDoorwayJustOutsideTheFloor_StartsFromTheNearestFloor(bool withClearance)
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall",
                new Vector2(10.15f, 5f), "hall", RouteHintTestLevels.HallWestDoor(), path, null,
                withClearance ? Clear : RouteHintPathOptions.Legacy);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "A student standing in the east doorway (0.15 m past the floor edge) must still get a route.");
            Assert.AreEqual(new Vector2(10.15f, 5f), path[0], "The route starts where the student really stands.");
            Assert.IsTrue(geometry.TryGetRoom("hall", out RouteHintRoom hall));
            Assert.IsTrue(hall.Contains(path[1], 0f), "The first planned point is on the floor next to the doorway.");
            AssertEndsAt(new Vector2(0f, 5f), path);
        }

        [TestCase(10.3f, 5f, 2.1f)]
        [TestCase(10.1f, 5f, 1.2f)]
        [TestCase(10.15f, 5.8f, 2.1f)]
        public void Plan_PlayerInDoorway_AnyPositionInsideTheFrame_IsPlanned(float x, float y, float frame)
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall(frame);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall",
                new Vector2(x, y), "hall", RouteHintTestLevels.HallWestDoor(), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
        }

        [Test]
        public void Plan_PlayerFarOutsideEveryRevealedFloor_IsStillNotPlanned()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall",
                new Vector2(14f, 5f), "hall", RouteHintTestLevels.HallWestDoor(), path);

            Assert.AreNotEqual(RouteHintPathOutcome.Planned, outcome,
                "Snapping the start has a limit: a student far from the floor is a real failure, not a doorway.");
            Assert.IsEmpty(path);
        }

        [Test]
        public void Plan_PlayerNextToAClosedDoor_TargetBeyondIt_PathGoesThroughTheDoor()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.ThreeRoomsInARow();
            List<Vector2> path = new();

            // The door is closed but not locked: it opens for the player, so planning treats it as passable.
            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true,
                new Vector2(4.7f, 2.5f), "a", RouteHintTestLevels.SecondDoor(), path, door => false);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Assert.IsTrue(ContainsPoint(path, new Vector2(5f, 2.5f)), "The route crosses the closed door a|b at its centre.");
            AssertEndsAt(new Vector2(10f, 2.5f), path);
        }

        [Test]
        public void Plan_PlayerInTheClosedDoorway_TargetBeyondIt_PathGoesThroughTheDoor()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.ThreeRoomsInARow();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "a" || id == "b",
                new Vector2(5.12f, 2.5f), "a", RouteHintTestLevels.SecondDoor(), path, door => false, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            AssertEndsAt(new Vector2(10f, 2.5f), path);
        }

        [Test]
        public void Plan_LockedTransitDoor_StillBlocksTheRoute()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.ThreeRoomsInARow();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true,
                new Vector2(4.7f, 2.5f), "a", RouteHintTestLevels.SecondDoor(), path,
                door => door.NodeA == "a" && door.NodeB == "b");

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, outcome, "Only a locked or disabled door is a wall.");
        }

        [Test]
        public void Plan_AroundPartition_LegacyHugsTheWallEnd_ClearanceKeepsOff()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.HallWithPartition();
            List<Vector2> legacy = new();
            List<Vector2> clear = new();
            Vector2 student = new(2.5f, 2.5f);

            PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall", student, "hall",
                RouteHintTestLevels.HallEastDoor(), legacy);
            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall", student,
                "hall", RouteHintTestLevels.HallEastDoor(), clear, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Assert.Less(MinClearanceAlong(geometry, legacy, 0.05f), WantedClearance - 0.2f,
                "Precondition: the old shortest route does run close to the partition end.");
            Assert.GreaterOrEqual(MinClearanceAlong(geometry, clear, 0.05f), WantedClearance - Tolerance,
                "Every part of the new route keeps the wanted distance from the walls.");
            AssertEndsAt(new Vector2(10f, 2.5f), clear);
        }

        [Test]
        public void Plan_CorridorTurnAndDoorways_KeepsClearanceAndCrossesDoorsAtTheirCentre()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.CorridorWithTurn();
            List<Vector2> path = new();
            Vector2 student = new(1f, 1f);

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, student, "start",
                RouteHintTestLevels.CorridorExit(), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Vector2 firstDoor = new(5f, 2.5f);
            Vector2 secondDoor = new(12.5f, 10f);
            Assert.IsTrue(ContainsPoint(path, firstDoor), "The route passes the exact centre of the first doorway.");
            Assert.IsTrue(ContainsPoint(path, secondDoor), "The route passes the exact centre of the second doorway.");
            AssertPerpendicularThroughDoor(path, firstDoor, Vector2.right);
            AssertPerpendicularThroughDoor(path, secondDoor, Vector2.up);
            Assert.GreaterOrEqual(MinClearanceAlong(geometry, path, 0.05f, firstDoor, secondDoor),
                WantedClearance - Tolerance, "Corridor, turn and rooms: the route never runs along a wall.");
        }

        [Test]
        public void Plan_RoomTooNarrowForTheClearance_DegradesInsteadOfLosingTheRoute()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("tunnel", Vector2.zero, 0f, new[] { new MinimapCellSnapshot(0f, 0f, 12f, 2f) },
                RouteHintTestLevels.Box(0f, 0f, 12f, 2f));
            geometry.AddRoom("next", new Vector2(12f, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, 5f, 2f) });
            geometry.AddDoor("tunnel", "e", "next", "w", new Vector2(12f, 1f), RouteHintTestLevels.DoorFrame);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "tunnel",
                new Vector2(1f, 1f), "tunnel", new CriticalPathDoor("tunnel", "e", "next", "w"), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "A tunnel with only 0.85 m of free width on each side still gets its route.");
            AssertEndsAt(new Vector2(12f, 1f), path);
        }

        [Test]
        public void Plan_LegacyOptions_GiveTheSameRouteAsThePlainOverload()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.HallWithPartition();
            List<Vector2> plain = new();
            List<Vector2> legacy = new();

            PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall", new Vector2(2.5f, 2.5f), "hall",
                RouteHintTestLevels.HallEastDoor(), plain);
            PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall", new Vector2(2.5f, 2.5f), "hall",
                RouteHintTestLevels.HallEastDoor(), legacy, null, RouteHintPathOptions.Legacy);

            CollectionAssert.AreEqual(plain, legacy);
        }

        [Test]
        public void Plan_PlayerJustInFrontOfTheTargetDoor_GoesStraightToItWithoutDetourBackwards()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall",
                new Vector2(1f, 5f), "hall", RouteHintTestLevels.HallWestDoor(), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            for (int i = 1; i < path.Count; i++)
            {
                Assert.LessOrEqual(path[i].x, path[i - 1].x + 0.001f,
                    "The route only moves toward the door: no backing away to an approach point first.");
            }

            AssertEndsAt(new Vector2(0f, 5f), path);
        }

        [Test]
        public void Plan_PlayerStandingExactlyInTheTargetDoorway_HasAZeroLengthRoute()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, id => id == "hall",
                new Vector2(0f, 5f), "hall", RouteHintTestLevels.HallWestDoor(), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Assert.AreEqual(2, path.Count);
            Assert.Less(Vector2.Distance(path[0], path[1]), 0.01f, "Nowhere left to lead: no hairpin through an approach point.");
        }

        [Test]
        public void PlanToRoomTask_WithClearance_LeadsInFrontOfTheWallPanel_KeepingOffTheWallsUntilTheLastMetre()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();
            List<Vector2> path = new();
            Vector2 panel = new(5f, 9.9f);

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.ExecuteToRoomTask(geometry, id => id == "hall",
                new Vector2(2.5f, 2.5f), "hall", "hall", panel, path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Assert.Less(Vector2.Distance(path[path.Count - 1], panel), 1f, "The route ends right in front of the panel.");
            for (int i = 1; i < path.Count - 1; i++)
            {
                Assert.GreaterOrEqual(RouteHintTestLevels.WallClearance(geometry, path[i]), WantedClearance - Tolerance,
                    $"Point {i} {path[i]} is not a wall-hugging point: only the final step reaches toward the panel.");
            }
        }

        [Test]
        public void Plan_RepeatedWithClearance_IsStableAndUsesTheDoorPairCache()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.CorridorWithTurn();
            List<Vector2> first = new();
            List<Vector2> second = new();

            PlanRouteHintPathUseCase.Execute(geometry, _ => true, new Vector2(2f, 2f), "start",
                RouteHintTestLevels.CorridorExit(), first, null, Clear);
            PlanRouteHintPathUseCase.Execute(geometry, _ => true, new Vector2(2f, 2f), "start",
                RouteHintTestLevels.CorridorExit(), second, null, Clear);

            CollectionAssert.AreEqual(first, second, "A re-plan from the same spot gives the same route.");
        }

        [Test]
        public void Diagnose_PlayerFarFromTheFloor_NamesTheDistance()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();

            RouteHintFailure failure = PlanRouteHintPathUseCase.Diagnose(geometry, id => id == "hall",
                new Vector2(14f, 5f), "hall", RouteHintTestLevels.HallWestDoor(), null,
                RouteHintPathOutcome.Unreachable);

            Assert.AreEqual(RouteHintFailureCause.PlayerFarFromFloor, failure.Cause);
            Assert.Greater(failure.Meters, 3f);
            StringAssert.Contains("hall", failure.Describe());
        }

        [Test]
        public void Diagnose_LockedDoorCutsTheRoute_NamesTheDoorChain()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.ThreeRoomsInARow();

            RouteHintFailure failure = PlanRouteHintPathUseCase.Diagnose(geometry, _ => true,
                new Vector2(2f, 2.5f), "a", RouteHintTestLevels.SecondDoor(),
                door => door.NodeA == "a" && door.NodeB == "b", RouteHintPathOutcome.Unreachable);

            Assert.AreEqual(RouteHintFailureCause.NoOpenDoorChain, failure.Cause);
            Assert.AreEqual(1, failure.LockedDoors);
        }

        [Test]
        public void Diagnose_SameCauseTwice_HasTheSameLogKey()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();

            RouteHintFailure first = PlanRouteHintPathUseCase.Diagnose(geometry, id => id == "hall",
                new Vector2(14f, 5f), "hall", RouteHintTestLevels.HallWestDoor(), null, RouteHintPathOutcome.Unreachable);
            RouteHintFailure second = PlanRouteHintPathUseCase.Diagnose(geometry, id => id == "hall",
                new Vector2(14.3f, 5.2f), "hall", RouteHintTestLevels.HallWestDoor(), null, RouteHintPathOutcome.Unreachable);

            Assert.AreEqual(first.Key, second.Key, "A student pacing in the same trouble does not re-log it.");
        }

        [Test]
        public void Plan_WithDoorLeadOut_RouteCarriesOnThroughTheTargetDoorIntoTheNextRoom()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.CorridorWithTurn();
            List<Vector2> withLeadOut = new();
            List<Vector2> plain = new();
            Vector2 student = new(12.5f, 7.5f);

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, student, "corridor",
                RouteHintTestLevels.CorridorExit(), withLeadOut, null,
                new RouteHintPathOptions(WantedClearance + 0.12f, 1.4f, 2f));
            PlanRouteHintPathUseCase.Execute(geometry, _ => true, student, "corridor",
                RouteHintTestLevels.CorridorExit(), plain, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            AssertEndsAt(new Vector2(12.5f, 10f), plain);
            Vector2 end = withLeadOut[withLeadOut.Count - 1];
            Assert.AreEqual(12.5f, end.x, 0.05f, "The stub goes straight on the way the route came in.");
            Assert.AreEqual(12f, end.y, 0.05f, "Two metres past the door, inside the next room.");
            Assert.IsTrue(ContainsPoint(withLeadOut, new Vector2(12.5f, 10f)), "The door itself stays a point of the route.");
        }

        [Test]
        public void Plan_WithDoorLeadOut_StubLongerThanTheNextRoom_IsClippedToWhatFits()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.CorridorWithTurn();
            List<Vector2> path = new();

            PlanRouteHintPathUseCase.Execute(geometry, _ => true, new Vector2(12.5f, 7.5f), "corridor",
                RouteHintTestLevels.CorridorExit(), path, null,
                new RouteHintPathOptions(WantedClearance + 0.12f, 1.4f, 9f));

            Vector2 end = path[path.Count - 1];
            Assert.Less(end.y, 13.01f, "The stub is clipped to the next room: 5 m deep, walls keep it off by their inset.");
            Assert.Greater(end.y, 10.5f, "A shorter stub still carries the route past the door.");
        }

        private static void AssertEndsAt(Vector2 expected, List<Vector2> path)
        {
            Assert.Greater(path.Count, 1);
            Assert.Less(Vector2.Distance(expected, path[path.Count - 1]), 0.02f);
        }

        private static bool ContainsPoint(List<Vector2> path, Vector2 point)
        {
            for (int i = 0; i < path.Count; i++)
            {
                if (Vector2.Distance(path[i], point) < 0.02f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The route runs straight through a door along its normal: the point before it (and, unless the door is
        /// the target, the point after it) lies on the axis through the door, perpendicular to its wall.
        /// </summary>
        private static void AssertPerpendicularThroughDoor(List<Vector2> path, Vector2 door, Vector2 axis)
        {
            int index = path.FindIndex(point => Vector2.Distance(point, door) < 0.02f);
            Assert.Greater(index, 0);
            Vector2 sideways = new(axis.y, axis.x);
            Assert.Less(Mathf.Abs(Vector2.Dot(path[index - 1] - door, sideways)), 0.02f,
                "The point before the door is straight in front of it.");
            if (index < path.Count - 1)
            {
                Assert.Less(Mathf.Abs(Vector2.Dot(path[index + 1] - door, sideways)), 0.02f,
                    "The point after the door is straight behind it.");
            }
        }

        /// <summary>Smallest wall distance along a polyline sampled every <paramref name="step"/> metres, skipping exempt points.</summary>
        private static float MinClearanceAlong(RouteHintGeometry geometry, List<Vector2> path, float step,
            params Vector2[] exemptPoints)
        {
            float min = float.MaxValue;
            for (int i = 1; i < path.Count; i++)
            {
                float length = Vector2.Distance(path[i - 1], path[i]);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / step));
                for (int s = 0; s <= steps; s++)
                {
                    Vector2 point = Vector2.Lerp(path[i - 1], path[i], s / (float)steps);
                    if (IsExempt(point, path[0], exemptPoints))
                    {
                        continue;
                    }

                    min = Mathf.Min(min, RouteHintTestLevels.WallClearance(geometry, point));
                }
            }

            return min;
        }

        /// <summary>The player's own start and the exact doorway points have less room by definition.</summary>
        private static bool IsExempt(Vector2 point, Vector2 start, Vector2[] doorways)
        {
            if (Vector2.Distance(point, start) < 1.6f)
            {
                return true;
            }

            for (int i = 0; i < doorways.Length; i++)
            {
                if (Vector2.Distance(point, doorways[i]) < 0.06f)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
