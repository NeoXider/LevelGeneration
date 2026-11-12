using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using UnityEngine;

namespace Neoxider.LevelGeneration.Generation.Presentation.Runtime
{
    public static class RoomWallSegmentExtractor
    {
        private const float MinWallLengthMeters = 0.35f;
        private const float MinWallWidthMeters = 0.08f;

        /// <summary>
        /// Floor of the thickness in the FOOTPRINT path only: a flat decal standing on a wall has no depth on the
        /// floor, and the collision minimum of 8 cm would turn it into a slab cutting the wall run it lies on (the
        /// tunnels lost a metre of wall that way). The collision path keeps its own minimum.
        /// </summary>
        private const float MinFootprintThicknessMeters = 0.01f;

        /// <summary>
        /// Wall segments of the room for generation collision and the minimap (<see cref="RoomTemplate.WallCollisionSegments"/>).
        /// <para>
        /// Known defect, kept on purpose: the length and thickness are read from the mesh's own X and Z axes, so a mesh
        /// standing on its side gets a wrong footprint — a decorative plane upright on a wall becomes a slab as thick as
        /// the plane is tall, a pipe lying along a wall becomes a short stub across it. Switching generation to
        /// <see cref="ExtractFootprints"/> changes room placement for existing seeds and every minimap, so it waits for
        /// an owner decision.
        /// </para>
        /// </summary>
        public static IReadOnlyList<RoomWallSegment2D> Extract(RoomTemplate template) => ExtractSegments(template, footprints: false);

        /// <summary>
        /// The same wall renderers as <see cref="Extract"/>, each measured by the real horizontal footprint of its
        /// oriented bounds in the room frame: the length runs along the box axis that is longest on the floor plane,
        /// the thickness is the box's extent across it (a plane upright on a wall stays a plane, a pipe along a wall
        /// stays along it). Used where the wall faces matter (wall posters). The thickness keeps its real depth down
        /// to 1 cm — see <see cref="MinFootprintThicknessMeters"/>.
        /// </summary>
        public static IReadOnlyList<RoomWallSegment2D> ExtractFootprints(RoomTemplate template) => ExtractSegments(template, footprints: true);

        private static IReadOnlyList<RoomWallSegment2D> ExtractSegments(RoomTemplate template, bool footprints)
        {
            if (template == null || !template.UseMinimapWallGeometry)
            {
                return Array.Empty<RoomWallSegment2D>();
            }

            List<Transform> roots = ResolveRoots(template);
            if (roots.Count == 0)
            {
                return Array.Empty<RoomWallSegment2D>();
            }

            List<Renderer> renderers = CollectRenderers(roots, ShouldFilterWholeRoomRoot(template, roots));
            if (renderers.Count == 0)
            {
                return Array.Empty<RoomWallSegment2D>();
            }

            return BuildSegments(template.transform, renderers, footprints);
        }

        private static List<Transform> ResolveRoots(RoomTemplate template)
        {
            List<Transform> result = new(4);
            if (template.MinimapWallParent != null)
            {
                result.Add(template.MinimapWallParent.transform);
                return result;
            }

            IReadOnlyList<Transform> roots = template.MinimapWallRoots;
            if (roots == null)
            {
                return result;
            }

            for (int i = 0; i < roots.Count; i++)
            {
                if (roots[i] != null)
                {
                    result.Add(roots[i]);
                }
            }

            return result;
        }

        private static List<Renderer> CollectRenderers(IReadOnlyList<Transform> roots, bool requireWallLikeName)
        {
            List<Renderer> result = new();
            for (int i = 0; i < roots.Count; i++)
            {
                Transform root = roots[i];
                if (root == null)
                {
                    continue;
                }

                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    if (renderer != null
                        && renderer.enabled
                        && renderer.gameObject.activeSelf
                        && (!requireWallLikeName || IsWallLikeRenderer(renderer, root)))
                    {
                        result.Add(renderer);
                    }
                }
            }

            return result;
        }

        private static bool ShouldFilterWholeRoomRoot(RoomTemplate template, IReadOnlyList<Transform> roots)
        {
            return template != null
                   && template.MinimapWallParent != null
                   && template.MinimapWallParent.transform == template.transform
                   && roots != null
                   && roots.Count == 1
                   && roots[0] == template.transform;
        }

        private static bool IsWallLikeRenderer(Renderer renderer, Transform root)
        {
            Transform current = renderer != null ? renderer.transform : null;
            while (current != null && current != root)
            {
                if (IsWallLikeName(current.name))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static bool IsWallLikeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("pillar", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("barrier", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<RoomWallSegment2D> BuildSegments(Transform roomRoot, IReadOnlyList<Renderer> renderers, bool footprints)
        {
            List<RoomWallSegment2D> result = new(renderers.Count);
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                bool built = footprints
                    ? TryBuildFootprintSegment(roomRoot, renderer, out RoomWallSegment2D segment)
                    : TryBuildMeshSegment(roomRoot, renderer, out segment);
                if (built)
                {
                    result.Add(segment);
                }
            }

            return result;
        }

        /// <summary>
        /// The horizontal footprint of the mesh's oriented bounds in the room frame. For a mesh turned only about
        /// the vertical axis it equals <see cref="TryBuildLocalBoundsSegment"/>.
        /// </summary>
        private static bool TryBuildFootprintSegment(Transform roomRoot, Renderer renderer, out RoomWallSegment2D segment)
        {
            segment = default;
            if (roomRoot == null || renderer == null)
            {
                return false;
            }

            if (!renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
            {
                return TryBuildWorldBoundsSegment(roomRoot, renderer.bounds, out segment);
            }

            Bounds bounds = filter.sharedMesh.bounds;
            Matrix4x4 toRoom = roomRoot.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Vector2 axisX = OnFloor(toRoom.GetColumn(0) * bounds.size.x);
            Vector2 axisY = OnFloor(toRoom.GetColumn(1) * bounds.size.y);
            Vector2 axisZ = OnFloor(toRoom.GetColumn(2) * bounds.size.z);
            Vector2 longest = axisX;
            if (axisZ.sqrMagnitude > longest.sqrMagnitude)
            {
                longest = axisZ;
            }

            if (axisY.sqrMagnitude > longest.sqrMagnitude)
            {
                longest = axisY;
            }

            float length = longest.magnitude;
            if (length < MinWallLengthMeters)
            {
                return false;
            }

            Vector2 along = longest / length;
            Vector2 across = new(-along.y, along.x);
            float halfLength = 0.5f * (Extent(axisX, along) + Extent(axisY, along) + Extent(axisZ, along));
            float thickness = Extent(axisX, across) + Extent(axisY, across) + Extent(axisZ, across);
            Vector3 center = toRoom.MultiplyPoint3x4(bounds.center);
            segment = new RoomWallSegment2D(
                center.x - along.x * halfLength,
                center.z - along.y * halfLength,
                center.x + along.x * halfLength,
                center.z + along.y * halfLength,
                Mathf.Max(MinFootprintThicknessMeters, thickness));
            return true;
        }

        private static Vector2 OnFloor(Vector4 axis) => new(axis.x, axis.z);

        private static float Extent(Vector2 axis, Vector2 direction) => Mathf.Abs(Vector2.Dot(axis, direction));

        private static bool TryBuildMeshSegment(Transform roomRoot, Renderer renderer, out RoomWallSegment2D segment)
        {
            segment = default;
            if (roomRoot == null || renderer == null)
            {
                return false;
            }

            if (renderer.TryGetComponent<MeshFilter>(out MeshFilter filter)
                && filter.sharedMesh != null)
            {
                return TryBuildLocalBoundsSegment(roomRoot, filter.sharedMesh.bounds, renderer.transform, out segment);
            }

            return TryBuildWorldBoundsSegment(roomRoot, renderer.bounds, out segment);
        }

        private static bool TryBuildLocalBoundsSegment(
            Transform roomRoot,
            Bounds localBounds,
            Transform transform,
            out RoomWallSegment2D segment)
        {
            segment = default;
            if (transform == null)
            {
                return false;
            }

            Vector3 extents = localBounds.extents;
            Vector3 center = localBounds.center;
            float xLength = extents.x * 2f;
            float zLength = extents.z * 2f;
            if (Mathf.Max(xLength, zLength) < MinWallLengthMeters)
            {
                return false;
            }

            // Pick the LENGTH axis by WORLD size (mesh extent x that axis' own scale), not the raw mesh extent,
            // and take the PERPENDICULAR world extent as the wall thickness. Modular wall tiles often share a
            // square mesh (e.g. 10x10) and encode the real thin wall through a non-uniform transform scale
            // (e.g. 0.10 x 0.35 -> a 1.0m x 3.5m wall). The old code compared raw (equal) mesh extents and set
            // the width to AverageScale(x,z) of BOTH scales, which produced fat 2-3.5m-wide collision boxes that
            // overlapped neighbours the thin wall never touches.
            float scaleX = Mathf.Abs(transform.lossyScale.x);
            float scaleZ = Mathf.Abs(transform.lossyScale.z);
            float worldX = xLength * scaleX;
            float worldZ = zLength * scaleZ;

            Vector3 localA;
            Vector3 localB;
            float widthMeters;
            if (worldX >= worldZ)
            {
                localA = new Vector3(center.x - extents.x, center.y, center.z);
                localB = new Vector3(center.x + extents.x, center.y, center.z);
                widthMeters = Mathf.Max(MinWallWidthMeters, worldZ);
            }
            else
            {
                localA = new Vector3(center.x, center.y, center.z - extents.z);
                localB = new Vector3(center.x, center.y, center.z + extents.z);
                widthMeters = Mathf.Max(MinWallWidthMeters, worldX);
            }

            Vector3 worldA = transform.TransformPoint(localA);
            Vector3 worldB = transform.TransformPoint(localB);
            Vector3 roomLocalA = roomRoot.InverseTransformPoint(worldA);
            Vector3 roomLocalB = roomRoot.InverseTransformPoint(worldB);
            segment = new RoomWallSegment2D(roomLocalA.x, roomLocalA.z, roomLocalB.x, roomLocalB.z, widthMeters);
            return true;
        }

        private static bool TryBuildWorldBoundsSegment(Transform roomRoot, Bounds bounds, out RoomWallSegment2D segment)
        {
            segment = default;
            float xLength = bounds.size.x;
            float zLength = bounds.size.z;
            if (Mathf.Max(xLength, zLength) < MinWallLengthMeters)
            {
                return false;
            }

            Vector2 center = new(bounds.center.x, bounds.center.z);
            Vector3 worldA = xLength >= zLength
                ? new Vector3(bounds.min.x, bounds.center.y, center.y)
                : new Vector3(center.x, bounds.center.y, bounds.min.z);
            Vector3 worldB = xLength >= zLength
                ? new Vector3(bounds.max.x, bounds.center.y, center.y)
                : new Vector3(center.x, bounds.center.y, bounds.max.z);
            Vector3 roomLocalA = roomRoot.InverseTransformPoint(worldA);
            Vector3 roomLocalB = roomRoot.InverseTransformPoint(worldB);
            segment = new RoomWallSegment2D(
                roomLocalA.x,
                roomLocalA.z,
                roomLocalB.x,
                roomLocalB.z,
                Mathf.Max(MinWallWidthMeters, xLength >= zLength ? zLength : xLength));
            return true;
        }

        private static float AverageScale(float a, float b) => (Mathf.Abs(a) + Mathf.Abs(b)) * 0.5f;
    }
}
