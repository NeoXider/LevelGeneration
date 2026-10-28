using System;
using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// The golden star stream: scatter never leans on a wall (corners and doorways included), sizes are
    /// skewed small with a few large, a minority twinkles long and slowly, every star sways around a slow
    /// mean drift, and the stream is anchored to the path, not to the student.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class RouteHintStarFieldTests
    {
        private static RouteHintStarStyle DefaultStyle(float maxLateralJitter = 0.2f, float lateralWobble = 0.08f) =>
            new(0.1f, 400, 0.1f, 0.5f, 2f, 3f, 0.5f, 0.175f, 0.03f, maxLateralJitter, 0.125f, 2.4f, 0.5f, 0.45f,
                0.3f, 0.2f, 3.5f, 0.9f, 0.12f, lateralWobble, 5f, 0.35f, 0.65f, 0.35f, 0.3f);

        [Test]
        public void Sample_NearWallsCornersAndDoorways_StarsKeepTheirMarginFromTheWalls()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.CorridorWithTurn();
            RouteHintTrail trail = PlannedTrail(geometry);
            RouteHintStarStyle wide = DefaultStyle(0.8f, 0f);
            RouteHintStarField clamped = new(512);
            RouteHintStarField unclamped = new(512);
            float worstClamped = float.MaxValue;
            float worstUnclamped = float.MaxValue;
            float widest = 0f;

            for (float time = 0f; time < 6f; time += 0.37f)
            {
                int count = clamped.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters,
                    0f, 1f, 1f, time, wide);
                for (int i = 0; i < count; i++)
                {
                    Vector2 point = new(clamped.Stars[i].Position.x, clamped.Stars[i].Position.z);
                    worstClamped = Mathf.Min(worstClamped, RouteHintTestLevels.WallClearance(geometry, point));
                    widest = Mathf.Max(widest, DistanceToTrail(trail, point));
                }

                count = unclamped.Sample(Centerline(trail), trail.Distances, null, trail.LengthMeters, 0f, 1f, 1f,
                    time, wide);
                for (int i = 0; i < count; i++)
                {
                    Vector2 point = new(unclamped.Stars[i].Position.x, unclamped.Stars[i].Position.z);
                    worstUnclamped = Mathf.Min(worstUnclamped, RouteHintTestLevels.WallClearance(geometry, point));
                }
            }

            Assert.Less(worstUnclamped, wide.WallMarginMeters,
                "Precondition: an unrestricted 0.8 m scatter does push stars toward the doorway jambs.");
            Assert.GreaterOrEqual(worstClamped, wide.WallMarginMeters - 0.02f,
                "With the free width measured along the trail no star comes closer to a wall than the margin.");
            Assert.Greater(widest, 0.3f, "The scatter still fills the wide stretches of the corridor.");
        }

        [Test]
        public void Sample_DefaultScatter_NeverExceedsThreeDecimetres()
        {
            RouteHintGeometry geometry = RouteHintTestLevels.CorridorWithTurn();
            RouteHintTrail trail = PlannedTrail(geometry);
            RouteHintStarStyle style = DefaultStyle();
            RouteHintStarField field = new(512);
            float widest = 0f;

            for (float time = 0f; time < 8f; time += 0.5f)
            {
                int count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 0f,
                    1f, 1f, time, style);
                for (int i = 0; i < count; i++)
                {
                    widest = Mathf.Max(widest, DistanceToTrail(trail, new Vector2(field.Stars[i].Position.x,
                        field.Stars[i].Position.z)));
                }
            }

            Assert.LessOrEqual(widest, 0.3f, "Jitter plus sway stay inside 0.3 m of the path's line.");
            Assert.Greater(widest, 0.15f, "The stream has visible width.");
        }

        [Test]
        public void Sample_Sizes_AreSkewedSmallWithAFewBigOnes_InsideTheConfiguredRange()
        {
            List<float> scales = SampleStraight(DefaultStyle(), 3f, 40f, out RouteHintStarStyle style, out _, out _);

            scales.Sort();
            float median = scales[scales.Count / 2];
            float sum = 0f;
            int big = 0;
            int small = 0;
            foreach (float scale in scales)
            {
                sum += scale;
                big += scale > 1.5f ? 1 : 0;
                small += scale < 0.8f ? 1 : 0;
            }

            Assert.GreaterOrEqual(scales[0], style.SizeMinScale * (1f - style.TwinkleSize) - 0.001f);
            Assert.LessOrEqual(scales[scales.Count - 1], style.SizeMaxScale + 0.001f);
            Assert.Less(median, sum / scales.Count, "Most stars are smaller than the average: the size mix leans small.");
            Assert.Greater(big / (float)scales.Count, 0.02f, "Some stars are noticeably bigger.");
            Assert.Less(big / (float)scales.Count, 0.3f, "The big ones are few.");
            Assert.Greater(small / (float)scales.Count, 0.5f, "Most stars stay small.");
        }

        [Test]
        public void Sample_AMinorityTwinklesLongAndSlowly_TheRestFast()
        {
            RouteHintStarStyle style = DefaultStyle();
            RouteHintStarField field = new(512);
            RouteHintTrail trail = StraightTrail(40f);
            int count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 0f, 1f,
                1f, 3f, style);
            int slow = 0;
            for (int i = 0; i < count; i++)
            {
                slow += Mathf.Abs(field.Stars[i].LifetimeSeconds - style.LongTwinkleSeconds) < 0.001f ? 1 : 0;
            }

            float share = slow / (float)count;
            Assert.Greater(share, 0.1f);
            Assert.Less(share, 0.3f, "Only a minority of the stars twinkle slowly.");

            Dictionary<uint, List<float>> brightnessBySeed = new();
            Dictionary<uint, bool> isSlow = new();
            for (float time = 0f; time < 7f; time += 0.05f)
            {
                count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 0f, 1f,
                    1f, time, style);
                for (int i = 0; i < count; i++)
                {
                    RouteHintStar star = field.Stars[i];
                    if (star.Position.x < 6f || star.Position.x > 30f)
                    {
                        continue;
                    }

                    if (!brightnessBySeed.TryGetValue(star.Seed, out List<float> list))
                    {
                        brightnessBySeed[star.Seed] = list = new List<float>();
                        isSlow[star.Seed] = Mathf.Abs(star.LifetimeSeconds - style.LongTwinkleSeconds) < 0.001f;
                    }

                    list.Add(star.Brightness);
                }
            }

            float slowDepth = 0f;
            float fastDepth = 0f;
            int slowStars = 0;
            int fastStars = 0;
            foreach (KeyValuePair<uint, List<float>> pair in brightnessBySeed)
            {
                if (pair.Value.Count < 40)
                {
                    continue;
                }

                float min = float.MaxValue;
                float max = float.MinValue;
                foreach (float value in pair.Value)
                {
                    min = Mathf.Min(min, value);
                    max = Mathf.Max(max, value);
                }

                float depth = 1f - min / max;
                if (isSlow[pair.Key])
                {
                    slowDepth += depth;
                    slowStars++;
                }
                else
                {
                    fastDepth += depth;
                    fastStars++;
                }
            }

            Assert.Greater(slowStars, 3);
            Assert.Greater(fastStars, 20);
            Assert.Greater(slowDepth / slowStars, 0.75f, "A slow star nearly dies away and glows up again.");
            Assert.Less(fastDepth / fastStars, 0.7f, "A fast star only flickers.");
        }

        [Test]
        public void Sample_StarsDriftAtTheMeanSpeed_EachWithItsOwnVariation()
        {
            RouteHintStarStyle style = DefaultStyle();
            RouteHintStarField field = new(512);
            RouteHintTrail trail = StraightTrail(40f);
            Dictionary<uint, float> before = new();
            int count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 0f, 1f,
                1f, 1f, style);
            for (int i = 0; i < count; i++)
            {
                before[field.Stars[i].Seed] = field.Stars[i].Position.x;
            }

            count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 0f, 1f,
                1f, 3f, style);
            List<float> moved = new();
            for (int i = 0; i < count; i++)
            {
                RouteHintStar star = field.Stars[i];
                if (star.Position.x > 8f && star.Position.x < 30f && before.TryGetValue(star.Seed, out float x))
                {
                    moved.Add(star.Position.x - x);
                }
            }

            Assert.Greater(moved.Count, 100);
            float mean = 0f;
            foreach (float value in moved)
            {
                mean += value;
            }

            mean /= moved.Count;
            float variance = 0f;
            foreach (float value in moved)
            {
                variance += (value - mean) * (value - mean);
                Assert.LessOrEqual(Mathf.Abs(value - 2f * style.SpeedMetersPerSecond), 2f * style.AlongWobbleMeters + 0.001f,
                    "No star strays more than its own wobble from the mean drift.");
            }

            Assert.AreEqual(2f * style.SpeedMetersPerSecond, mean, 0.05f,
                "On average the stream drifts 0.5 m/s toward the target.");
            Assert.Greater(Mathf.Sqrt(variance / moved.Count), 0.02f,
                "Stars do not move in lockstep: each has its own speed jitter.");
        }

        [Test]
        public void Sample_StarsBeforeTheHeadAreNotDrawn_AndFadeInAtIt()
        {
            RouteHintStarStyle style = DefaultStyle();
            RouteHintStarField field = new(512);
            RouteHintTrail trail = StraightTrail(30f);

            int count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 5f, 1f,
                1f, 2f, style);

            float nearHead = 0f;
            float farFromHead = 0f;
            for (int i = 0; i < count; i++)
            {
                RouteHintStar star = field.Stars[i];
                Assert.Greater(star.Position.x, 5f, "The path behind the head is not drawn.");
                if (star.Position.x < 5.1f)
                {
                    nearHead = Mathf.Max(nearHead, star.Brightness);
                }
                else if (star.Position.x > 7f && star.Position.x < 20f)
                {
                    farFromHead = Mathf.Max(farFromHead, star.Brightness);
                }
            }

            Assert.Greater(count, 0);
            Assert.Less(nearHead, farFromHead * 0.5f, "The stream fades in at the head instead of popping on.");
        }

        [Test]
        public void Sample_StarsAreAnchoredToThePath_MovingTheHeadDoesNotDragThem()
        {
            RouteHintStarStyle style = DefaultStyle();
            RouteHintStarField first = new(512);
            RouteHintStarField second = new(512);
            RouteHintTrail trail = StraightTrail(40f);

            int a = first.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 1f, 1f, 1f,
                2.5f, style);
            int b = second.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 9f, 1f,
                1f, 2.5f, style);

            Dictionary<uint, Vector3> early = new();
            for (int i = 0; i < a; i++)
            {
                early[first.Stars[i].Seed] = first.Stars[i].Position;
            }

            int matched = 0;
            for (int i = 0; i < b; i++)
            {
                if (early.TryGetValue(second.Stars[i].Seed, out Vector3 position))
                {
                    matched++;
                    Assert.Less(Vector3.Distance(position, second.Stars[i].Position), 0.0001f,
                        "A star keeps its place on the path while the student walks ahead.");
                }
            }

            Assert.Greater(matched, 100);
        }

        [Test]
        public void Sample_LongPath_StaysWithinTheBudgetAndStillReachesTheTarget()
        {
            RouteHintStarStyle style = DefaultStyle();
            RouteHintStarField field = new(512);
            RouteHintTrail trail = StraightTrail(500f);

            int count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 0f, 1f,
                1f, 2f, style);

            float farthest = 0f;
            for (int i = 0; i < count; i++)
            {
                farthest = Mathf.Max(farthest, field.Stars[i].Position.x);
            }

            Assert.LessOrEqual(count, style.MaxStars);
            Assert.Greater(farthest, 450f, "A long route is thinned, not cut short.");
        }

        [Test]
        public void Sample_PartialReveal_LeavesTheTargetHalfUnlit()
        {
            RouteHintStarStyle style = DefaultStyle();
            RouteHintStarField field = new(512);
            RouteHintTrail trail = StraightTrail(20f);

            int count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 4f, 0.25f,
                1f, 0f, style);

            Assert.Greater(count, 0);
            for (int i = 0; i < count; i++)
            {
                Assert.Less(field.Stars[i].Position.x, 4f + 16f * 0.25f + 2f * style.AlongWobbleMeters + 0.01f,
                    "The reveal grows from the head toward the target.");
            }
        }

        private static List<float> SampleStraight(RouteHintStarStyle style, float time, float length,
            out RouteHintStarStyle usedStyle, out RouteHintTrail trail, out RouteHintStarField field)
        {
            usedStyle = style;
            trail = StraightTrail(length);
            field = new RouteHintStarField(512);
            int count = field.Sample(Centerline(trail), trail.Distances, trail.Clearances, trail.LengthMeters, 0f, 1f,
                1f, time, style);
            List<float> scales = new();
            for (int i = 0; i < count; i++)
            {
                scales.Add(field.Stars[i].Size / style.SizeMeters);
            }

            return scales;
        }

        private static RouteHintTrail PlannedTrail(RouteHintGeometry geometry)
        {
            List<Vector2> route = new();
            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, new Vector2(2.5f, 2.5f),
                "start", RouteHintTestLevels.CorridorExit(), route, null, new RouteHintPathOptions(1.02f, 1.4f));
            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            RouteHintTrail trail = new();
            RouteHintTrailBuilder.Build(geometry, route, new RouteHintRibbonShape(0.55f, 0.9f, 0.12f, 0.3f, 0.03f), 4f,
                0.75f, 0.5f, trail);
            return trail;
        }

        private static RouteHintTrail StraightTrail(float length)
        {
            List<Vector2> points = new();
            for (float x = 0f; x < length; x += 0.3f)
            {
                points.Add(new Vector2(x, 0f));
            }

            points.Add(new Vector2(length, 0f));
            RouteHintTrail trail = new();
            trail.Assign(points, null, 4f, 0.75f, 0.5f);
            return trail;
        }

        private static List<Vector3> Centerline(RouteHintTrail trail)
        {
            List<Vector3> line = new(trail.Points.Count);
            for (int i = 0; i < trail.Points.Count; i++)
            {
                line.Add(new Vector3(trail.Points[i].x, 0f, trail.Points[i].y));
            }

            return line;
        }

        private static float DistanceToTrail(RouteHintTrail trail, Vector2 point)
        {
            float best = float.MaxValue;
            for (int i = 1; i < trail.Points.Count; i++)
            {
                Vector2 a = trail.Points[i - 1];
                Vector2 segment = trail.Points[i] - a;
                float t = segment.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(point - a, segment) / segment.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(point, a + segment * t));
            }

            return best;
        }
    }
}
