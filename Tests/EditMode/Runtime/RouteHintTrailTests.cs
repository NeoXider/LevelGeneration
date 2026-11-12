using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// The shared route-hint trail: one smoothed polyline for the floor stars and the minimap, a visible start
    /// that follows the player at a fixed distance ahead, and the re-plan rule (deviation threshold + throttle).
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class RouteHintTrailTests
    {
        private const float StartOffset = 0.75f;
        private const float MinVisible = 0.5f;
        private const float Deviation = 1.5f;
        private const float Cap = 4f;

        private static readonly RouteHintRibbonShape Smoothing = new(0.55f, 0.9f, 0.12f, 0.3f, 0.03f);

        [Test]
        public void Build_SmoothsCornersOnce_KeepsEndsAndMeasuresClearanceAfterSmoothing()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.CorridorWithTurn();
            List<Vector2> route = PlanClear(geometry, new Vector2(2.5f, 2.5f));
            RouteHintTrail trail = new();

            RouteHintTrailBuilder.Build(geometry, route, Smoothing, Cap, StartOffset, MinVisible, trail);

            Assert.Greater(trail.Points.Count, route.Count, "Straight runs are resampled and corners rounded.");
            Assert.Less(Vector2.Distance(trail.Points[0], route[0]), 0.001f);
            Assert.Less(Vector2.Distance(trail.Points[trail.Points.Count - 1], route[route.Count - 1]), 0.001f);
            Assert.AreEqual(trail.Points.Count, trail.Clearances.Count);
            Vector2 firstDoor = new(5f, 2.5f);
            Vector2 secondDoor = new(12.5f, 10f);
            for (int i = 0; i < trail.Points.Count; i++)
            {
                Assert.AreEqual(RouteHintTestLevels.WallClearance(geometry, trail.Points[i]), trail.Clearances[i], 0.001f,
                    "The stored free width is measured on the smoothed point itself.");
                bool doorway = Vector2.Distance(trail.Points[i], firstDoor) < 0.1f
                               || Vector2.Distance(trail.Points[i], secondDoor) < 0.1f;
                if (!doorway && Vector2.Distance(trail.Points[i], trail.Points[0]) > 1.6f)
                {
                    Assert.GreaterOrEqual(trail.Clearances[i], 0.9f - 0.02f,
                        $"Smoothed point {i} {trail.Points[i]} keeps the wall clearance.");
                }
            }
        }

        [Test]
        public void Build_DegenerateRoute_KeepsThePlannersPointsAndLengthZero()
        {
            RouteHintTrail trail = new();
            List<Vector2> route = new() { new Vector2(3f, 3f), new Vector2(3f, 3f) };

            RouteHintTrailBuilder.Build(null, route, Smoothing, Cap, StartOffset, MinVisible, trail);

            Assert.IsTrue(trail.HasPoints);
            Assert.AreEqual(0f, trail.LengthMeters);
        }

        [Test]
        public void Follow_PlayerOnTheTrail_HeadIsAheadOfHimAlongThePath_NotAtHisFeet()
        {
            RouteHintTrail trail = StraightTrail(20f);

            RouteHintTrailFollow follow = trail.Follow(new Vector2(4f, 0.4f), StartOffset, MinVisible, Deviation);

            Assert.IsTrue(follow.OnTrail);
            Assert.AreEqual(0.4f, follow.DeviationMeters, 0.001f);
            Assert.AreEqual(4f, trail.ProgressMeters, 0.001f);
            Assert.AreEqual(4f + StartOffset, trail.HeadDistance, 0.001f,
                "The visible trail starts 0.75 m ahead of the student, along the path.");
            Assert.AreEqual(new Vector2(4f + StartOffset, 0f), trail.HeadPoint);
        }

        [Test]
        public void Follow_PlayerWalksAlongTheTrail_HeadFollowsWithoutReplacingThePoints()
        {
            RouteHintTrail trail = StraightTrail(20f);
            int revision = trail.Revision;
            IReadOnlyList<Vector2> points = trail.Points;

            for (float x = 0f; x <= 15f; x += 0.5f)
            {
                trail.Follow(new Vector2(x, 0.2f), StartOffset, MinVisible, Deviation);
            }

            Assert.AreEqual(15f + StartOffset, trail.HeadDistance, 0.001f);
            Assert.AreEqual(revision, trail.Revision, "Walking along the trail is not a re-plan.");
            Assert.AreSame(points, trail.Points);
            Assert.AreEqual(5f - StartOffset, trail.RemainingMeters, 0.001f);
        }

        [Test]
        public void Follow_PlayerFarFromTheTrail_KeepsTheHeadAndReportsTheDeviation()
        {
            RouteHintTrail trail = StraightTrail(20f);
            trail.Follow(new Vector2(6f, 0f), StartOffset, MinVisible, Deviation);
            float head = trail.HeadDistance;

            RouteHintTrailFollow follow = trail.Follow(new Vector2(9f, 4f), StartOffset, MinVisible, Deviation);

            Assert.IsFalse(follow.OnTrail);
            Assert.AreEqual(4f, follow.DeviationMeters, 0.001f);
            Assert.AreEqual(head, trail.HeadDistance, 0.0001f, "Off the trail the head stays; a re-plan decides what next.");
        }

        [Test]
        public void Follow_PlayerNearTheTarget_NeverHidesTheLastMetres()
        {
            RouteHintTrail trail = StraightTrail(10f);

            trail.Follow(new Vector2(9.7f, 0f), StartOffset, MinVisible, Deviation);

            Assert.AreEqual(9.7f, trail.HeadDistance, 0.001f,
                "Within the last half metre the visible trail starts at the student, not past the target.");
            Assert.Less(trail.HeadDistance, trail.LengthMeters);
            trail.Follow(new Vector2(9f, 0f), StartOffset, MinVisible, Deviation);
            Assert.AreEqual(9.5f, trail.HeadDistance, 0.001f, "At least 0.5 m of trail stays visible before the target.");
        }

        [Test]
        public void FillVisible_StartsAtTheHead_ThenEveryLaterPoint()
        {
            RouteHintTrail trail = StraightTrail(4f);
            trail.Follow(new Vector2(1.6f, 0f), StartOffset, MinVisible, Deviation);
            List<Vector2> visible = new();

            trail.FillVisible(visible);

            Assert.AreEqual(trail.HeadPoint, visible[0]);
            Assert.AreEqual(new Vector2(1.6f + StartOffset, 0f), visible[0]);
            for (int i = 1; i < visible.Count; i++)
            {
                Assert.Greater(visible[i].x, visible[i - 1].x, "Only points beyond the head remain, in path order.");
            }

            Assert.AreEqual(trail.Points[trail.Points.Count - 1], visible[visible.Count - 1]);
        }

        [Test]
        public void Assign_ReplacesPoints_BumpsRevisionAndRestartsTheHead()
        {
            RouteHintTrail trail = StraightTrail(20f);
            trail.Follow(new Vector2(12f, 0f), StartOffset, MinVisible, Deviation);
            int revision = trail.Revision;

            trail.Assign(new List<Vector2> { new(0f, 5f), new(0f, 15f) }, null, Cap, StartOffset, MinVisible);

            Assert.AreNotEqual(revision, trail.Revision);
            Assert.AreEqual(10f, trail.LengthMeters, 0.001f);
            Assert.AreEqual(StartOffset, trail.HeadDistance, 0.001f);
            Assert.AreEqual(Cap, trail.Clearances[0], "Unknown free width counts as unlimited.");
        }

        [Test]
        public void Replan_WorldChanged_PlansAtOnce_EvenInsideTheThrottle()
        {
            RouteHintReplanReason reason = DecideRouteHintReplanUseCase.Execute(
                new RouteHintReplanInput(true, true, 0f, Deviation, 5f, 9f));

            Assert.AreEqual(RouteHintReplanReason.WorldChanged, reason);
        }

        [Test]
        public void Replan_PlayerOnTheTrail_NeverPlansAgain()
        {
            RouteHintReplanReason reason = DecideRouteHintReplanUseCase.Execute(
                new RouteHintReplanInput(true, false, Deviation - 0.01f, Deviation, 100f, 0f));

            Assert.AreEqual(RouteHintReplanReason.None, reason, "Within the threshold the trail only follows him.");
        }

        [Test]
        public void Replan_PlayerLeftTheTrail_WaitsForTheThrottleThenPlans()
        {
            RouteHintReplanReason early = DecideRouteHintReplanUseCase.Execute(
                new RouteHintReplanInput(true, false, Deviation + 2f, Deviation, 4.9f, 5f));
            RouteHintReplanReason due = DecideRouteHintReplanUseCase.Execute(
                new RouteHintReplanInput(true, false, Deviation + 2f, Deviation, 5f, 5f));

            Assert.AreEqual(RouteHintReplanReason.None, early, "Not every frame: the throttle holds the re-plan back.");
            Assert.AreEqual(RouteHintReplanReason.PlayerLeftTrail, due);
        }

        [Test]
        public void Replan_NoTrail_RetriesOnlyWhenTheThrottleOpens()
        {
            Assert.AreEqual(RouteHintReplanReason.None, DecideRouteHintReplanUseCase.Execute(
                new RouteHintReplanInput(false, false, 0f, Deviation, 1f, 2f)));
            Assert.AreEqual(RouteHintReplanReason.NoTrail, DecideRouteHintReplanUseCase.Execute(
                new RouteHintReplanInput(false, false, 0f, Deviation, 2f, 2f)));
        }

        [Test]
        public void AssignBlended_StartsAsTheOldLineAndMeltsIntoTheNewOneWithoutAJump()
        {
            RouteHintTrail trail = StraightTrail(20f);
            List<Vector2> shifted = new() { new Vector2(0f, 1.2f), new Vector2(10f, 0.6f), new Vector2(20f, 0f) };
            int revision = trail.Revision;

            trail.AssignBlended(shifted, null, Cap, StartOffset, MinVisible, 10f, 0.4f);

            Assert.IsTrue(trail.IsBlending);
            Assert.Greater(trail.Revision, revision);
            for (int i = 0; i < trail.Points.Count; i++)
            {
                Assert.AreEqual(0f, trail.Points[i].y, 0.001f, "Right after the re-plan the shown line is still the old one.");
            }

            Assert.IsTrue(trail.AdvanceBlend(10.2f));
            Assert.IsTrue(trail.IsBlending);
            Assert.AreEqual(0.6f, trail.Points[0].y, 0.001f, "Halfway the start has moved half of the way.");

            Assert.IsTrue(trail.AdvanceBlend(10.5f));
            Assert.IsFalse(trail.IsBlending);
            Assert.AreEqual(1.2f, trail.Points[0].y, 0.001f, "The last frame leaves exactly the planner's points.");
            Assert.AreEqual(0.6f, trail.Points[1].y, 0.001f);
            Assert.AreEqual(0f, trail.Points[2].y, 0.001f);
            Assert.IsFalse(trail.AdvanceBlend(11f), "Nothing left to blend.");
        }

        [Test]
        public void AssignBlended_RouteOfAnotherLength_OrNoTrailYet_ReplacesAtOnce()
        {
            RouteHintTrail trail = StraightTrail(20f);
            trail.AssignBlended(new List<Vector2> { new(0f, 0f), new(30f, 0f) }, null, Cap, StartOffset, MinVisible,
                0f, 0.4f);

            Assert.IsFalse(trail.IsBlending, "A route 10 m longer is a different route, not a drifted line.");
            Assert.AreEqual(30f, trail.LengthMeters, 0.001f);

            RouteHintTrail empty = new();
            empty.AssignBlended(new List<Vector2> { new(0f, 0f), new(5f, 0f) }, null, Cap, StartOffset, MinVisible,
                0f, 0.4f);

            Assert.IsFalse(empty.IsBlending);
            Assert.AreEqual(5f, empty.LengthMeters, 0.001f);
        }

        /// <summary>
        /// Высоты пола под линией — данные плана: кадры смешивания меняют точки (и <c>Revision</c>), но не план,
        /// поэтому высоты, прощупанные лучами один раз на перепланирование, остаются годными до следующего плана.
        /// </summary>
        [Test]
        public void PlanVersion_GrowsPerReplanOnly_FloorHeightsLiveUntilTheNextPlan()
        {
            RouteHintTrail trail = StraightTrail(20f);
            List<Vector2> shifted = new() { new Vector2(0f, 1.2f), new Vector2(10f, 0.6f), new Vector2(20f, 0f) };
            trail.AssignBlended(shifted, null, Cap, StartOffset, MinVisible, 10f, 0.4f);
            int plan = trail.PlanVersion;
            Assert.AreSame(trail.PlannedPoints, trail.PlannedPoints);
            Assert.AreEqual(shifted[0], trail.PlannedPoints[0], "During the blend the planned points are the new ones.");
            Assert.IsFalse(trail.HasFloorHeights);
            trail.SetFloorHeights(new List<float> { 0f, 0.1f, 0.2f });
            int revision = trail.Revision;

            trail.AdvanceBlend(10.2f);
            trail.AdvanceBlend(10.5f);

            Assert.Greater(trail.Revision, revision, "The blend frames change the points.");
            Assert.AreEqual(plan, trail.PlanVersion, "…but not the plan.");
            Assert.IsTrue(trail.HasFloorHeights, "So the heights probed for the plan stay valid.");
            Assert.AreSame(trail.Points, trail.PlannedPoints, "After the blend the shown points are the plan.");

            trail.Assign(shifted, null, Cap, StartOffset, MinVisible);

            Assert.Greater(trail.PlanVersion, plan);
            Assert.IsFalse(trail.HasFloorHeights, "A new plan needs its own floor heights.");
        }

        private static RouteHintTrail StraightTrail(float length)
        {
            List<Vector2> points = new();
            for (float x = 0f; x < length; x += 1f)
            {
                points.Add(new Vector2(x, 0f));
            }

            points.Add(new Vector2(length, 0f));
            RouteHintTrail trail = new();
            trail.Assign(points, null, Cap, StartOffset, MinVisible);
            return trail;
        }

        private static List<Vector2> PlanClear(RouteHintGeometry geometry, Vector2 student)
        {
            List<Vector2> route = new();
            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, student, "start",
                RouteHintTestLevels.CorridorExit(), route, null, new RouteHintPathOptions(1.02f, 1.4f));
            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            return route;
        }
    }
}
