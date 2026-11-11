using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// Лента подсказки маршрута на полу строится из той же полилинии, что рисует миникарта: проходит рядом с
    /// точками поворота (двери, обходы стен), не срезая углы через стены, лежит на полу и несёт расстояние
    /// вдоль пути для шевронов и проявления.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class RouteHintRibbonMeshBuilderTests
    {
        private static readonly RouteHintRibbonShape Shape = new(0.5f, 1f, 0.12f, 0.25f, 0.03f, 0.35f);

        [Test]
        public void Build_StraightPath_StripKeepsWidthAndCoversTheWholePath()
        {
            List<Vector2> polyline = new() { new Vector2(0f, 0f), new Vector2(4f, 0f) };
            RouteHintRibbonMeshData data = new();

            bool built = RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data);

            Assert.IsTrue(built);
            Assert.AreEqual(4f, data.LengthMeters, 0.001f, "The ribbon covers the whole path.");
            Assert.AreEqual(data.Centerline.Count * 2, data.Vertices.Count, "Two vertices per axis point.");
            Assert.AreEqual((data.Centerline.Count - 1) * 6, data.Triangles.Count, "Two triangles per span.");
            AssertWidthKept(data);
            foreach (int index in data.Triangles)
            {
                Assert.That(index, Is.InRange(0, data.Vertices.Count - 1), "Triangles reference real vertices.");
            }
        }

        /// <summary>
        /// Лента по уже известным высотам (кадры смешивания линии) — та же лента, что по лучам: высоты, которые
        /// построение по лучам вернуло, дают те же вершины без единого луча.
        /// </summary>
        [Test]
        public void BuildFromAxisAtHeights_HeightsOfAProbedBuild_GiveTheSameRibbonWithoutProbes()
        {
            List<Vector2> axis = new();
            for (float x = 0f; x <= 6.001f; x += 0.25f)
            {
                axis.Add(new Vector2(x, Mathf.Sin(x) * 0.5f));
            }

            RouteHintRibbonMeshData probed = new();
            List<float> heights = new();
            Assert.IsTrue(RouteHintRibbonMeshBuilder.BuildFromAxis(axis, Shape, 0f, Stairs, probed,
                out RouteHintRibbonFailure _, heights));
            Assert.AreEqual(axis.Count, heights.Count, "One floor height per axis point.");
            RouteHintRibbonMeshData known = new();

            Assert.IsTrue(RouteHintRibbonMeshBuilder.BuildFromAxisAtHeights(axis, heights, Shape, known,
                out RouteHintRibbonFailure _));

            Assert.AreEqual(probed.Vertices.Count, known.Vertices.Count);
            for (int i = 0; i < probed.Vertices.Count; i++)
            {
                Assert.AreEqual(probed.Vertices[i], known.Vertices[i], $"vertex {i}");
                Assert.AreEqual(probed.Uv0[i], known.Uv0[i], $"uv0 {i}");
                Assert.AreEqual(probed.Uv1[i], known.Uv1[i], $"uv1 {i}");
            }

            CollectionAssert.AreEqual(probed.Triangles, known.Triangles);
            Assert.AreEqual(probed.LengthMeters, known.LengthMeters);
        }

        [Test]
        public void Build_AlongPath_DistanceAndProgressGrowFromPlayerToTarget()
        {
            List<Vector2> polyline = new() { new Vector2(0f, 0f), new Vector2(3f, 0f), new Vector2(3f, 2f) };
            RouteHintRibbonMeshData data = new();

            RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data);

            Assert.AreEqual(0f, data.Uv1[0].x, 0.0001f, "Progress starts at the player.");
            Assert.AreEqual(1f, data.Uv1[data.Uv1.Count - 1].x, 0.0001f, "Progress ends at the target.");
            for (int i = 2; i < data.Uv0.Count; i += 2)
            {
                Assert.GreaterOrEqual(data.Uv0[i].y, data.Uv0[i - 2].y, "Distance along the ribbon only grows.");
                Assert.AreEqual(data.LengthMeters, data.Uv1[i].y, 0.0001f, "Every vertex knows the full length.");
            }
        }

        [Test]
        public void Build_StairsWithinMaxStep_RibbonClimbsStepByStep()
        {
            List<Vector2> polyline = new() { new Vector2(0f, 0f), new Vector2(6f, 0f) };
            RouteHintRibbonMeshData data = new();

            RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, Stairs, data);

            for (int i = 0; i < data.Centerline.Count; i++)
            {
                Vector3 point = data.Centerline[i];
                Assert.AreEqual(StairHeight(point.x) + Shape.LiftMeters, point.y, 0.0001f,
                    "Each step is within the max step from the previous point, so the ribbon follows the stairs.");
            }
        }

        [Test]
        public void Build_TableOnThePath_RibbonStaysOnTheFloor()
        {
            List<Vector2> polyline = new() { new Vector2(0f, 0f), new Vector2(6f, 0f) };
            RouteHintRibbonMeshData data = new();

            RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, TableAtTwoToThree, data);

            foreach (Vector3 point in data.Centerline)
            {
                Assert.AreEqual(Shape.LiftMeters, point.y, 0.0001f,
                    "A probe hit higher than the max step above the previous point is decor, not floor.");
            }
        }

        [TestCase(true, 0.2f, 0f, 0.35f, 0.2f)]
        [TestCase(true, 0.8f, 0f, 0.35f, 0f)]
        [TestCase(true, 1.2f, 0.9f, 0.35f, 1.2f)]
        [TestCase(false, 5f, 0.4f, 0.35f, 0.4f)]
        [TestCase(true, float.NaN, 0.4f, 0.35f, 0.4f)]
        public void ResolveFloorHeight_ComparesWithThePreviousAcceptedPoint(bool hit, float hitHeight,
            float previous, float maxStep, float expected)
        {
            float result = ResolveRouteHintFloorHeightUseCase.Execute(hit, hitHeight, previous, maxStep);

            Assert.AreEqual(expected, result, 0.0001f, "The ribbon accepts only floor within a step of its last point.");
        }

        [Test]
        public void SmoothCorners_RightAngleAtDoor_PassesNearTheDoorWithoutSharpTurns()
        {
            Vector2 door = new(3f, 0f);
            List<Vector2> polyline = new() { new Vector2(0f, 0f), door, new Vector2(3f, 3f) };
            List<Vector2> axis = new();

            RouteHintRibbonMeshBuilder.SmoothCorners(polyline, Shape, axis);

            Assert.LessOrEqual(DistanceToPolyline(door, axis), Shape.CornerMaxDeviationMeters + 0.001f,
                "The ribbon is routed through the door point like the minimap path.");
            for (int i = 2; i < axis.Count; i++)
            {
                Vector2 previous = (axis[i - 1] - axis[i - 2]).normalized;
                Vector2 next = (axis[i] - axis[i - 1]).normalized;
                Assert.Less(Vector2.Angle(previous, next), 60f, "The corner is smoothed, not a sharp turn.");
            }
        }

        [Test]
        public void SmoothCorners_ZigZag_NeverLeavesThePlannedPathByMoreThanTheDeviation()
        {
            List<Vector2> polyline = new()
            {
                new Vector2(0f, 0f), new Vector2(2f, 0f), new Vector2(2f, 2f), new Vector2(4f, 2f),
                new Vector2(4f, 0.5f), new Vector2(6f, 0.5f)
            };
            List<Vector2> axis = new();

            RouteHintRibbonMeshBuilder.SmoothCorners(polyline, Shape, axis);

            Assert.AreEqual(polyline[0], axis[0], "The ribbon starts at the player.");
            Assert.AreEqual(polyline[polyline.Count - 1], axis[axis.Count - 1], "The ribbon ends at the target.");
            AssertAxisFollowsPolyline(axis, polyline, Shape.CornerMaxDeviationMeters + 0.001f);
        }

        [TestCase(61, 2f)]
        [TestCase(46, 5f)]
        [TestCase(61, 20f)]
        public void SmoothCorners_StaircaseOverTheBudget_KeepsEveryTurnWithinTheDeviation(int points, float segment)
        {
            List<Vector2> polyline = Staircase(points, segment);
            List<Vector2> axis = new();

            RouteHintRibbonMeshBuilder.SmoothCorners(polyline, Shape, axis);

            Assert.LessOrEqual(axis.Count, RouteHintRibbonMeshBuilder.MaxSamples, "A long path stays a light mesh.");
            Assert.AreEqual(polyline[0], axis[0], "The ribbon starts at the player.");
            Assert.AreEqual(polyline[polyline.Count - 1], axis[axis.Count - 1], "The tail is never truncated.");
            for (int i = 1; i < polyline.Count - 1; i++)
            {
                Assert.LessOrEqual(DistanceToPolyline(polyline[i], axis), Shape.CornerMaxDeviationMeters + 0.001f,
                    $"Turn {i} (a door or a wall bypass) is passed within the deviation, not cut through the wall.");
            }

            AssertAxisFollowsPolyline(axis, polyline, Shape.CornerMaxDeviationMeters + 0.001f);
        }

        [Test]
        public void SmoothCorners_LongPathWithManyTurns_KeepsADenseAxisWithinTheBudget()
        {
            List<Vector2> polyline = Staircase(41, 10f);
            List<Vector2> axis = new();

            RouteHintRibbonMeshBuilder.SmoothCorners(polyline, Shape, axis);

            Assert.LessOrEqual(axis.Count, RouteHintRibbonMeshBuilder.MaxSamples);
            Assert.Greater(axis.Count, RouteHintRibbonMeshBuilder.MaxSamples / 2,
                "Only the straight step grows to fit the budget; reserving worst-case corners left ~120 points.");
        }

        [Test]
        public void SmoothCorners_VeryLongPath_StaysWithinTheSampleBudget()
        {
            List<Vector2> polyline = new() { new Vector2(0f, 0f), new Vector2(1000f, 0f), new Vector2(1000f, 800f) };
            List<Vector2> axis = new();

            RouteHintRibbonMeshBuilder.SmoothCorners(polyline, Shape, axis);

            Assert.LessOrEqual(axis.Count, RouteHintRibbonMeshBuilder.MaxSamples, "A long path stays a light mesh.");
            Assert.AreEqual(polyline[polyline.Count - 1], axis[axis.Count - 1], "The target is still reached.");
        }

        [Test]
        [Timeout(10000)]
        public void Build_HugeSegments_StaysBoundedInsteadOfOverflowing()
        {
            List<Vector2> polyline = new() { new Vector2(0f, 0f), new Vector2(5e8f, 0f), new Vector2(5e8f, 5e8f) };
            RouteHintRibbonMeshData data = new();

            bool built = RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data);

            Assert.LessOrEqual(data.Centerline.Count, RouteHintRibbonMeshBuilder.HardMaxSamples * 2,
                "Segments of hundreds of millions of meters must not overflow the sample count.");
            if (built)
            {
                Assert.LessOrEqual(data.Vertices.Count, RouteHintRibbonMeshBuilder.HardMaxSamples * 4);
            }
        }

        [Test]
        public void Build_ThousandsOfSharpTurns_RefusesInsteadOfExceedingTheMeshLimit()
        {
            List<Vector2> polyline = Zigzag(20000);
            RouteHintRibbonMeshData data = new();

            bool built = RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data,
                out RouteHintRibbonFailure failure);

            Assert.IsFalse(built, "A ribbon past the hard cap is skipped; the minimap shows the route at once.");
            Assert.AreEqual(RouteHintRibbonFailure.TooComplex, failure);
            Assert.IsEmpty(data.Vertices);
        }

        [Test]
        public void Build_ManyTurnsUnderTheHardCap_KeepsEveryTurnAndFitsA16BitMesh()
        {
            List<Vector2> polyline = Zigzag(300);
            RouteHintRibbonMeshData data = new();

            bool built = RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data,
                out RouteHintRibbonFailure failure);

            Assert.IsTrue(built);
            Assert.AreEqual(RouteHintRibbonFailure.None, failure);
            Assert.Greater(data.Centerline.Count, RouteHintRibbonMeshBuilder.MaxSamples,
                "Precondition: the soft cap is exceeded to keep the turns.");
            Assert.Less(data.Vertices.Count, 65535, "The ribbon mesh fits a 16-bit index buffer.");
        }

        [Test]
        public void Build_NonFinitePoints_AreDroppedAndNeverReachTheMesh()
        {
            List<Vector2> polyline = new()
            {
                new Vector2(0f, 0f), new Vector2(float.NaN, 1f), new Vector2(2f, 0f),
                new Vector2(float.PositiveInfinity, 0f), new Vector2(4f, 0f)
            };
            RouteHintRibbonMeshData data = new();

            bool built = RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data);

            Assert.IsTrue(built, "The finite part of the path is still shown.");
            foreach (Vector3 vertex in data.Vertices)
            {
                Assert.IsFalse(float.IsNaN(vertex.x) || float.IsNaN(vertex.y) || float.IsNaN(vertex.z)
                               || float.IsInfinity(vertex.x) || float.IsInfinity(vertex.z), "No NaN vertices.");
            }
        }

        [Test]
        public void Build_OnlyNonFinitePoints_BuildsNothing()
        {
            List<Vector2> polyline = new() { new Vector2(float.NaN, 0f), new Vector2(0f, float.NegativeInfinity) };
            RouteHintRibbonMeshData data = new();

            bool built = RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data);

            Assert.IsFalse(built);
            Assert.IsEmpty(data.Vertices);
        }

        [TestCase(180f)]
        [TestCase(179f)]
        [TestCase(165f)]
        public void Build_TurnBack_RibbonDoesNotFoldFlipOrPinch(float turnDegrees)
        {
            Vector2 turn = new(3f, 0f);
            float heading = turnDegrees * Mathf.Deg2Rad;
            List<Vector2> polyline = new()
            {
                new Vector2(0f, 0f), turn, turn + new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * 3f
            };
            RouteHintRibbonMeshData data = new();

            RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data);

            AssertWidthKept(data);
            Assert.Less(MinDistanceToCenterline(turn, data), 0.001f, "A reversal is not rounded: the turn is reached.");
            float halfWidth = Shape.WidthMeters * 0.5f;
            for (int i = 0; i + 1 < data.Centerline.Count; i++)
            {
                Vector2 from = Flat(data.Centerline[i]);
                Vector2 to = Flat(data.Centerline[i + 1]);
                Vector2 direction = to - from;
                if (direction.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                direction.Normalize();
                Assert.Greater(Cross(direction, Flat(data.Vertices[i * 2]) - from), halfWidth * 0.5f,
                    "The left edge stays on the left of a moving span and the ribbon is not pinched.");
                Assert.Greater(Cross(direction, Flat(data.Vertices[(i + 1) * 2]) - to), halfWidth * 0.5f,
                    "The left edge stays on the left at the span end and the ribbon is not pinched.");
            }
        }

        [Test]
        public void Build_DegeneratePath_BuildsNothing()
        {
            List<Vector2> polyline = new() { new Vector2(1f, 1f), new Vector2(1f, 1f) };
            RouteHintRibbonMeshData data = new();

            bool built = RouteHintRibbonMeshBuilder.Build(polyline, Shape, 0f, null, data);

            Assert.IsFalse(built, "A path without length has nothing to show on the floor.");
            Assert.IsEmpty(data.Vertices);
        }

        private static List<Vector2> Zigzag(int points)
        {
            List<Vector2> polyline = new();
            for (int i = 0; i < points; i++)
            {
                polyline.Add(new Vector2(i * 0.5f, i % 2 == 0 ? 0f : 0.5f));
            }

            return polyline;
        }

        private static List<Vector2> Staircase(int points, float segment)
        {
            List<Vector2> polyline = new() { Vector2.zero };
            for (int i = 1; i < points; i++)
            {
                Vector2 last = polyline[polyline.Count - 1];
                polyline.Add(last + (i % 2 == 1 ? new Vector2(segment, 0f) : new Vector2(0f, segment)));
            }

            return polyline;
        }

        private static float StairHeight(float x) => Mathf.Floor(x) * 0.2f;

        private static bool Stairs(Vector2 point, float fromFloorHeight, out float hitHeight)
        {
            hitHeight = StairHeight(point.x);
            return true;
        }

        private static bool TableAtTwoToThree(Vector2 point, float fromFloorHeight, out float hitHeight)
        {
            hitHeight = point.x >= 2f && point.x <= 3f ? 0.8f : 0f;
            return true;
        }

        private static void AssertWidthKept(RouteHintRibbonMeshData data)
        {
            for (int i = 0; i < data.Vertices.Count; i += 2)
            {
                Assert.AreEqual(Shape.WidthMeters, Vector2.Distance(Flat(data.Vertices[i]), Flat(data.Vertices[i + 1])),
                    0.001f, "The ribbon keeps its width.");
            }
        }

        private static void AssertAxisFollowsPolyline(IReadOnlyList<Vector2> axis, IReadOnlyList<Vector2> polyline,
            float tolerance)
        {
            for (int i = 0; i < axis.Count; i++)
            {
                Assert.LessOrEqual(DistanceToPolyline(axis[i], polyline), tolerance,
                    "Smoothing must not cut corners through walls next to the planned path.");
                if (i > 0)
                {
                    Assert.LessOrEqual(DistanceToPolyline((axis[i - 1] + axis[i]) * 0.5f, polyline), tolerance,
                        "Every axis segment stays next to the planned path, not only its points.");
                }
            }
        }

        private static float MinDistanceToCenterline(Vector2 point, RouteHintRibbonMeshData data)
        {
            float best = float.MaxValue;
            foreach (Vector3 center in data.Centerline)
            {
                best = Mathf.Min(best, Vector2.Distance(point, Flat(center)));
            }

            return best;
        }

        private static Vector2 Flat(Vector3 point) => new(point.x, point.z);

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static float DistanceToPolyline(Vector2 point, IReadOnlyList<Vector2> polyline)
        {
            float best = float.MaxValue;
            for (int i = 1; i < polyline.Count; i++)
            {
                Vector2 a = polyline[i - 1];
                Vector2 span = polyline[i] - a;
                float t = span.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(point - a, span) / span.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(point, a + span * t));
            }

            return best;
        }
    }
}
