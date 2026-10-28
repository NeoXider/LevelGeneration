using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// The two wall geometries of a room template: <see cref="RoomWallSegmentExtractor.Extract"/> (generation
    /// collision and the minimap) and <see cref="RoomWallSegmentExtractor.ExtractFootprints"/> (the walls wall
    /// posters hang on). They must agree wherever a rotation about the vertical cannot change the answer — that is
    /// what makes the footprints a safe stand-in for the collision walls — and the footprints must stay the true
    /// horizontal footprint where they cannot: a tilted plane, a mirrored mesh, a renderer without a mesh filter, a
    /// room root that stands somewhere else.
    /// <para>
    /// The thickness of a footprint is the REAL depth on the floor: a flat decal standing on a wall has no depth at
    /// all and must not become an 8 cm slab that cuts the wall run it lies on.
    /// </para>
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class RoomWallSegmentExtractorFootprintTests
    {
        private const float Tolerance = 0.01f;

        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        [TestCase(0f)]
        [TestCase(37f)]
        [TestCase(90f)]
        [TestCase(180f)]
        public void VerticalYaw_LeavesBothGeometriesEqual(float yawDegrees)
        {
            RoomTemplate room = Room(yawDegrees, new Vector3(3f, 0f, -2f));
            Cube(WallsOf(room), "WallNorth", new Vector3(0f, 1.75f, 1.7f), new Vector3(8f, 3.5f, 0.4f));

            SameSegment(RoomWallSegmentExtractor.Extract(room), RoomWallSegmentExtractor.ExtractFootprints(room));
        }

        [Test]
        public void NonUniformScale_LeavesBothGeometriesEqual()
        {
            RoomTemplate room = Room(37f, Vector3.zero);
            Cube(WallsOf(room), "WallThin", new Vector3(0f, 2f, 2f), new Vector3(8f, 4f, 0.12f));

            SameSegment(RoomWallSegmentExtractor.Extract(room), RoomWallSegmentExtractor.ExtractFootprints(room));
        }

        [Test]
        public void MirroredScale_LeavesBothGeometriesEqual()
        {
            RoomTemplate room = Room(0f, Vector3.zero);
            Cube(WallsOf(room), "WallMirrored", new Vector3(0f, 2f, -2f), new Vector3(-8f, 4f, 0.3f));

            SameSegment(RoomWallSegmentExtractor.Extract(room), RoomWallSegmentExtractor.ExtractFootprints(room));
        }

        [Test]
        public void RendererWithoutMeshFilter_LeavesBothGeometriesEqual()
        {
            RoomTemplate room = Room(90f, new Vector3(1f, 0f, 1f));
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Mesh quadMesh = quad.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(quad);
            GameObject boundsOnly = new("BoundsOnly");
            _created.Add(boundsOnly);
            boundsOnly.transform.SetParent(WallsOf(room), false);
            boundsOnly.AddComponent<SkinnedMeshRenderer>().sharedMesh = quadMesh;
            boundsOnly.transform.localPosition = new Vector3(0f, 2f, 2f);
            boundsOnly.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            boundsOnly.transform.localScale = new Vector3(8f, 4f, 1f);

            IReadOnlyList<RoomWallSegment2D> collision = RoomWallSegmentExtractor.Extract(room);
            Assert.That(collision, Has.Count.EqualTo(1), "Fixture check: a renderer without a mesh filter is measured by its world bounds.");
            SameSegment(collision, RoomWallSegmentExtractor.ExtractFootprints(room));
        }

        /// <summary>
        /// A plane leaning on the wall: its floor footprint is 10 m along the wall and 10 * sin(45°) across it, which
        /// is the depth the footprints must report. The collision path reads the mesh's own 10 x 10 extents instead
        /// and lays the segment along the mesh's own X axis — off the floor, and nowhere near the room.
        /// </summary>
        [Test]
        public void TiltedPlane_KeepsItsProjectedDepthInTheFootprints()
        {
            RoomTemplate room = Room(0f, Vector3.zero);
            Plane(WallsOf(room), "TiltedPanel", new Vector3(0f, 2f, 2.5f), Quaternion.Euler(45f, 0f, 0f), Vector3.one);

            RoomWallSegment2D footprint = RoomWallSegmentExtractor.ExtractFootprints(room).Single();

            Assert.That(Length(footprint), Is.EqualTo(10f).Within(Tolerance), "The full 10 m of the plane run along the wall.");
            Assert.That(footprint.WidthMeters, Is.EqualTo(10f * Mathf.Sin(45f * Mathf.Deg2Rad)).Within(Tolerance),
                "The depth on the floor is the projected depth, not the mesh extent.");
        }

        /// <summary>
        /// A flat decal standing on the wall has no depth on the floor; the footprints must say so instead of giving
        /// it the collision minimum of 8 cm, which would cut the wall run it lies on.
        /// </summary>
        [Test]
        public void UprightFlatPanel_KeepsItsRealDepthInsteadOfTheCollisionMinimum()
        {
            RoomTemplate room = Room(0f, Vector3.zero);
            Plane(WallsOf(room), "Decal", new Vector3(0f, 0.4f, 1.8f), Quaternion.Euler(90f, 0f, 0f), Vector3.one);

            RoomWallSegment2D footprint = RoomWallSegmentExtractor.ExtractFootprints(room).Single();

            Assert.That(footprint.WidthMeters, Is.LessThan(0.02f),
                "A panel standing on the wall is a plane on the floor, not an 8 cm slab.");
            Assert.That(footprint.WidthMeters, Is.GreaterThan(0f), "…but it still has a thickness to merge runs by.");
        }

        [Test]
        public void FlatPanelBesideAThickWall_MakesTheWallTheFaceOfTheRun()
        {
            RoomTemplate room = Room(0f, Vector3.zero);
            Cube(WallsOf(room), "WallThick", new Vector3(0f, 1.75f, 2f), new Vector3(8f, 3.5f, 0.4f));
            Plane(WallsOf(room), "Decal", new Vector3(0f, 0.3f, 1.81f), Quaternion.Euler(90f, 0f, 0f), Vector3.one);

            List<RoomWallSegment2D> footprints = RoomWallSegmentExtractor.ExtractFootprints(room).ToList();

            Assert.That(footprints.Count, Is.EqualTo(2));
            Assert.That(footprints.Max(segment => segment.WidthMeters), Is.EqualTo(0.4f).Within(Tolerance),
                "The run face stays the face of the thick tile; the decal is not a slab across it.");
        }

        /// <summary>Same wall, same thickness, both ways — a rotation about the vertical cannot tell them apart.</summary>
        private static void SameSegment(
            IReadOnlyList<RoomWallSegment2D> collision, IReadOnlyList<RoomWallSegment2D> footprints)
        {
            Assert.That(footprints.Count, Is.EqualTo(collision.Count));
            foreach (RoomWallSegment2D wall in collision)
            {
                Assert.That(footprints.Any(segment => Same(segment, wall)), Is.True,
                    $"Footprints and collision walls disagree about a wall: {Describe(wall)}.");
            }
        }

        private static bool Same(RoomWallSegment2D first, RoomWallSegment2D second) =>
            Mathf.Abs(first.WidthMeters - second.WidthMeters) < Tolerance
            && Near(first.Ax, first.Az, second.Ax, second.Az)
            && Near(first.Bx, first.Bz, second.Bx, second.Bz)
                || Mathf.Abs(first.WidthMeters - second.WidthMeters) < Tolerance
                && Near(first.Ax, first.Az, second.Bx, second.Bz)
                && Near(first.Bx, first.Bz, second.Ax, second.Az);

        private static bool Near(float ax, float az, float bx, float bz) =>
            Mathf.Abs(ax - bx) < Tolerance && Mathf.Abs(az - bz) < Tolerance;

        private static float Length(RoomWallSegment2D segment) =>
            Mathf.Sqrt(Mathf.Pow(segment.Bx - segment.Ax, 2f) + Mathf.Pow(segment.Bz - segment.Az, 2f));

        private static string Describe(RoomWallSegment2D segment) =>
            $"({segment.Ax:0.00}, {segment.Az:0.00}) - ({segment.Bx:0.00}, {segment.Bz:0.00}) x {segment.WidthMeters:0.00}";

        /// <summary>A room standing at its own yaw and offset, with one wall root under it.</summary>
        private RoomTemplate Room(float yawDegrees, Vector3 offset)
        {
            GameObject root = new("ExtractorRoom");
            _created.Add(root);
            root.transform.localPosition = offset;
            root.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);
            RoomTemplate room = root.AddComponent<RoomTemplate>();
            GameObject walls = new("Walls");
            walls.transform.SetParent(root.transform, false);
            SerializedObject serialized = new(room);
            serialized.FindProperty("minimapWallParent").objectReferenceValue = walls;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            room.InvalidateCachedGeometryForEditorTests();
            return room;
        }

        private static Transform WallsOf(RoomTemplate room) => room.MinimapWallParent.transform;

        private static GameObject Cube(Transform parent, string name, Vector3 position, Vector3 scale)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = scale;
            return cube;
        }

        private void Plane(Transform parent, string name, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            Object.DestroyImmediate(plane.GetComponent<Collider>());
            plane.name = name;
            plane.transform.SetParent(parent, false);
            plane.transform.localPosition = position;
            plane.transform.localRotation = rotation;
            plane.transform.localScale = scale;
            _created.Add(plane);
        }
    }
}