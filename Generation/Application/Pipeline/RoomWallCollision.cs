using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Application.Pipeline
{
    internal static class RoomWallCollision
    {
        private const float DoorSnapMeters = LevelGenerationGridMetrics.DoorSnapMeters;

        private const float Epsilon = 0.001f;

        // Door frames are resolved to the centerline between corridor side walls. At a valid seam
        // those side-wall OBBs can touch around half a 5m snap cell away from the center point.
        private const float ConnectedDoorSeamRadiusMeters = DoorSnapMeters * 0.5f;

        public static bool TryOverlaps(
            GridPose aPose,
            IRoomTemplateDescriptor aTemplate,
            GridPose bPose,
            IRoomTemplateDescriptor bTemplate,
            (float x, float z)? seamPointDoorSteps,
            out bool overlaps)
        {
            overlaps = false;
            if (!TryGetSegments(aTemplate, out IReadOnlyList<RoomWallSegment2D> aSegments) ||
                !TryGetSegments(bTemplate, out IReadOnlyList<RoomWallSegment2D> bSegments))
            {
                return false;
            }

            (float x, float z)? seamPointMeters = seamPointDoorSteps.HasValue
                ? (x: seamPointDoorSteps.Value.x * DoorSnapMeters, z: seamPointDoorSteps.Value.z * DoorSnapMeters)
                : ((float x, float z)?)null;
            overlaps = SegmentsOverlap(Transform(aSegments, aPose), Transform(bSegments, bPose), seamPointMeters);
            return true;
        }

        public static bool TryOverlaps(
            PlacedRoom a,
            IRoomTemplateDescriptor aTemplate,
            PlacedRoom b,
            IRoomTemplateDescriptor bTemplate,
            (float x, float z)? seamPointMeters,
            out bool overlaps)
        {
            overlaps = false;
            if (!TryGetSegments(aTemplate, out IReadOnlyList<RoomWallSegment2D> aSegments) ||
                !TryGetSegments(bTemplate, out IReadOnlyList<RoomWallSegment2D> bSegments))
            {
                return false;
            }

            overlaps = SegmentsOverlap(Transform(aSegments, a), Transform(bSegments, b), seamPointMeters);
            return true;
        }

        internal static bool TryTransform(
            PlacedRoom room,
            IRoomTemplateDescriptor template,
            out TransformedWallSegments transformed)
        {
            transformed = default;
            if (!TryGetSegments(template, out IReadOnlyList<RoomWallSegment2D> segments))
            {
                return false;
            }

            transformed = Transform(segments, room);
            return true;
        }

        /// <summary>
        /// То же, что <see cref="TryTransform"/>, но пишет стены в переиспользуемый буфер вызывающего, а не в новый
        /// массив. Горячий путь размещения проверяет так каждую позу-кандидата; результат живёт только до
        /// следующего вызова с тем же буфером, поэтому его нельзя кэшировать (для кэша — <see cref="TryTransform"/>).
        /// </summary>
        internal static bool TryTransformInto(
            PlacedRoom room,
            IRoomTemplateDescriptor template,
            ref WallObb[] buffer,
            out TransformedWallSegments transformed)
        {
            transformed = default;
            if (!TryGetSegments(template, out IReadOnlyList<RoomWallSegment2D> segments))
            {
                return false;
            }

            if (buffer == null || buffer.Length < segments.Count)
            {
                buffer = new WallObb[Math.Max(segments.Count, (buffer?.Length ?? 0) * 2)];
            }

            ResolveOriginAndRotation(room, out (float x, float z) origin, out float rotation);
            RotationBasis basis = new(rotation);
            for (int i = 0; i < segments.Count; i++)
            {
                buffer[i] = BuildObb(segments[i], origin, basis);
            }

            transformed = new TransformedWallSegments(buffer, segments.Count);
            return true;
        }

        /// <summary>
        /// Axis-aligned bounding box of already-transformed wall OBBs, used as a broadphase pre-reject
        /// so the per-segment SAT test only runs for rooms that can actually touch.
        /// </summary>
        internal static Aabb BoundsOf(TransformedWallSegments segments)
        {
            if (!segments.HasSegments)
            {
                return Aabb.Empty;
            }

            WallObb first = segments[0];
            float minX = first.MinX;
            float minZ = first.MinZ;
            float maxX = first.MaxX;
            float maxZ = first.MaxZ;
            for (int i = 1; i < segments.Count; i++)
            {
                WallObb obb = segments[i];
                if (obb.MinX < minX)
                {
                    minX = obb.MinX;
                }

                if (obb.MinZ < minZ)
                {
                    minZ = obb.MinZ;
                }

                if (obb.MaxX > maxX)
                {
                    maxX = obb.MaxX;
                }

                if (obb.MaxZ > maxZ)
                {
                    maxZ = obb.MaxZ;
                }
            }

            return new Aabb(minX, minZ, maxX, maxZ);
        }

        internal static bool TryOverlaps(
            TransformedWallSegments a,
            TransformedWallSegments b,
            (float x, float z)? seamPointMeters,
            out bool overlaps)
        {
            overlaps = false;
            if (!a.HasSegments || !b.HasSegments)
            {
                return false;
            }

            overlaps = SegmentsOverlap(a, b, seamPointMeters);
            return true;
        }

        private static bool TryGetSegments(
            IRoomTemplateDescriptor template,
            out IReadOnlyList<RoomWallSegment2D> segments)
        {
            segments = template is IRoomWallCollisionDescriptor descriptor
                ? descriptor.WallCollisionSegments
                : null;
            return segments != null && segments.Count > 0;
        }

        private static TransformedWallSegments Transform(IReadOnlyList<RoomWallSegment2D> segments, GridPose pose)
        {
            WallObb[] result = new WallObb[segments.Count];
            (float x, float z) origin = (x: pose.CellX * DoorSnapMeters, z: pose.CellY * DoorSnapMeters);
            RotationBasis basis = new(pose.RotationDegrees);
            for (int i = 0; i < segments.Count; i++)
            {
                result[i] = BuildObb(segments[i], origin, basis);
            }

            return new TransformedWallSegments(result);
        }

        private static TransformedWallSegments Transform(IReadOnlyList<RoomWallSegment2D> segments, PlacedRoom room)
        {
            WallObb[] result = new WallObb[segments.Count];
            ResolveOriginAndRotation(room, out (float x, float z) origin, out float rotation);
            RotationBasis basis = new(rotation);
            for (int i = 0; i < segments.Count; i++)
            {
                result[i] = BuildObb(segments[i], origin, basis);
            }

            return new TransformedWallSegments(result);
        }

        private static void ResolveOriginAndRotation(
            PlacedRoom room,
            out (float x, float z) origin,
            out float rotationDegrees)
        {
            origin = room.WorldPose.HasValue
                ? (x: room.WorldPose.Value.X, z: room.WorldPose.Value.Z)
                : (x: room.Pose.CellX * DoorSnapMeters, z: room.Pose.CellY * DoorSnapMeters);
            rotationDegrees = room.WorldPose.HasValue ? YawDegrees(room.WorldPose.Value) : room.Pose.RotationDegrees;
        }

        private static WallObb BuildObb(RoomWallSegment2D segment, (float x, float z) origin, RotationBasis basis)
        {
            (float x, float z) a = Add(origin, basis.Rotate((segment.Ax, segment.Az)));
            (float x, float z) b = Add(origin, basis.Rotate((segment.Bx, segment.Bz)));
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length <= Epsilon)
            {
                dx = 1f;
                dz = 0f;
                length = 1f;
            }

            float endShrink = Math.Max(0f, RoomWallCollisionSettings.PlacementWallEndShrinkMeters);
            if (length > endShrink * 2f + Epsilon)
            {
                float directionX = dx / length;
                float directionZ = dz / length;
                a = (
                    x: a.x + directionX * endShrink,
                    z: a.z + directionZ * endShrink);
                b = (
                    x: b.x - directionX * endShrink,
                    z: b.z - directionZ * endShrink);
                dx = b.x - a.x;
                dz = b.z - a.z;
                length = (float)Math.Sqrt(dx * dx + dz * dz);
            }

            float halfWidth = Math.Max(
                0.01f,
                segment.WidthMeters * 0.5f -
                Math.Max(0f, RoomWallCollisionSettings.PlacementWallHalfWidthShrinkMeters));
            float px = -dz / length * halfWidth;
            float pz = dx / length * halfWidth;
            return new WallObb(
                (x: a.x + px, z: a.z + pz),
                (x: b.x + px, z: b.z + pz),
                (x: b.x - px, z: b.z - pz),
                (x: a.x - px, z: a.z - pz),
                a,
                b,
                halfWidth);
        }

        private static bool SegmentsOverlap(
            TransformedWallSegments aSegments,
            TransformedWallSegments bSegments,
            (float x, float z)? seamPointMeters)
        {
            for (int a = 0; a < aSegments.Count; a++)
            {
                WallObb aObb = aSegments[a];
                for (int b = 0; b < bSegments.Count; b++)
                {
                    WallObb bObb = bSegments[b];
                    if (!RectsOverlap(aObb, bObb) || !Overlaps(aObb, bObb))
                    {
                        continue;
                    }

                    if (seamPointMeters.HasValue && IsNearSeam(aObb, bObb, seamPointMeters.Value))
                    {
                        continue;
                    }

                    return true;
                }
            }

            return false;
        }

        private static bool RectsOverlap(WallObb a, WallObb b)
        {
            return a.MaxX > b.MinX + Epsilon
                   && b.MaxX > a.MinX + Epsilon
                   && a.MaxZ > b.MinZ + Epsilon
                   && b.MaxZ > a.MinZ + Epsilon;
        }

        private static bool IsNearSeam(WallObb a, WallObb b, (float x, float z) seamPoint)
        {
            return IsWallNearSeam(a, seamPoint)
                   && IsWallNearSeam(b, seamPoint);
        }

        private static bool IsWallNearSeam(WallObb obb, (float x, float z) seamPoint)
        {
            float maxDistance = ConnectedDoorSeamRadiusMeters + obb.HalfWidth;
            return DistancePointToSegmentSqr(seamPoint, obb.A, obb.B) <= maxDistance * maxDistance;
        }

        private static float DistancePointToSegmentSqr(
            (float x, float z) point,
            (float x, float z) a,
            (float x, float z) b)
        {
            (float x, float z) ab = (x: b.x - a.x, z: b.z - a.z);
            (float x, float z) ap = (x: point.x - a.x, z: point.z - a.z);
            float lengthSqr = ab.x * ab.x + ab.z * ab.z;
            if (lengthSqr <= Epsilon * Epsilon)
            {
                float dx = point.x - a.x;
                float dz = point.z - a.z;
                return dx * dx + dz * dz;
            }

            float t = Math.Max(0f, Math.Min(1f, Dot(ap, ab) / lengthSqr));
            (float x, float z) closest = (x: a.x + ab.x * t, z: a.z + ab.z * t);
            (float x, float z) delta = (x: point.x - closest.x, z: point.z - closest.z);
            return delta.x * delta.x + delta.z * delta.z;
        }

        private static bool Overlaps(WallObb a, WallObb b) => !HasSeparatingAxis(a, b) && !HasSeparatingAxis(b, a);

        private static bool HasSeparatingAxis(WallObb source, WallObb target)
        {
            for (int i = 0; i < 4; i++)
            {
                (float x, float z) p0 = source.GetPoint(i);
                (float x, float z) p1 = source.GetPoint((i + 1) % 4);
                (float x, float z) edge = (x: p1.x - p0.x, z: p1.z - p0.z);
                (float x, float z) axis = (x: -edge.z, z: edge.x);
                float axisLength = (float)Math.Sqrt(axis.x * axis.x + axis.z * axis.z);
                if (axisLength <= Epsilon)
                {
                    continue;
                }

                axis = (axis.x / axisLength, axis.z / axisLength);
                Project(source, axis, out float sourceMin, out float sourceMax);
                Project(target, axis, out float targetMin, out float targetMax);
                if (sourceMax <= targetMin + Epsilon || targetMax <= sourceMin + Epsilon)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Project(WallObb obb, (float x, float z) axis, out float min, out float max)
        {
            min = max = Dot(obb.GetPoint(0), axis);
            for (int i = 1; i < 4; i++)
            {
                float value = Dot(obb.GetPoint(i), axis);
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }
        }

        private static float YawDegrees(RoomWorldPose pose)
        {
            float x = pose.RotationX;
            float y = pose.RotationY;
            float z = pose.RotationZ;
            float w = pose.RotationW;
            float siny = 2f * (w * y + z * x);
            float cosy = 1f - 2f * (y * y + x * x);
            float yaw = (float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        // Синус и косинус поворота комнаты считаются один раз на трансформацию, а не дважды на каждую стену.
        // Формулы те же, что были в прежнем Rotate на точку, поэтому позы стен и исход пересечений бит-в-бит прежние.
        private readonly struct RotationBasis
        {
            private readonly float _cos;
            private readonly float _sin;

            public RotationBasis(float degrees)
            {
                double radians = degrees * Math.PI / 180.0;
                _cos = (float)Math.Cos(radians);
                _sin = (float)Math.Sin(radians);
            }

            public (float x, float z) Rotate((float x, float z) point) =>
                (point.x * _cos + point.z * _sin, -point.x * _sin + point.z * _cos);
        }

        private static (float x, float z) Add((float x, float z) a, (float x, float z) b) => (a.x + b.x, a.z + b.z);

        private static float Dot((float x, float z) a, (float x, float z) b) => a.x * b.x + a.z * b.z;

        internal readonly struct Aabb
        {
            public static readonly Aabb Empty = new(0f, 0f, 0f, 0f, false);

            public readonly float MinX;
            public readonly float MinZ;
            public readonly float MaxX;
            public readonly float MaxZ;
            public readonly bool HasValue;

            public Aabb(float minX, float minZ, float maxX, float maxZ)
                : this(minX, minZ, maxX, maxZ, true)
            {
            }

            private Aabb(float minX, float minZ, float maxX, float maxZ, bool hasValue)
            {
                MinX = minX;
                MinZ = minZ;
                MaxX = maxX;
                MaxZ = maxZ;
                HasValue = hasValue;
            }

            /// <summary>
            /// Conservative overlap test used for broadphase. Returns true when the boxes (expanded by
            /// <paramref name="margin"/>) could touch. A negative result is a guaranteed non-overlap, so the
            /// detailed per-segment test can be skipped without changing placement results.
            /// </summary>
            public bool Overlaps(in Aabb other, float margin)
            {
                if (!HasValue || !other.HasValue)
                {
                    return true;
                }

                return MaxX + margin >= other.MinX
                       && other.MaxX + margin >= MinX
                       && MaxZ + margin >= other.MinZ
                       && other.MaxZ + margin >= MinZ;
            }
        }

        internal readonly struct TransformedWallSegments
        {
            private readonly WallObb[] _segments;
            private readonly int _count;

            public TransformedWallSegments(WallObb[] segments)
                : this(segments, segments?.Length ?? 0)
            {
            }

            // count меньше длины массива — буфер переиспользуется (см. TryTransformInto), значимы первые count стен.
            public TransformedWallSegments(WallObb[] segments, int count)
            {
                _segments = segments;
                _count = segments == null ? 0 : Math.Max(0, Math.Min(count, segments.Length));
            }

            public bool HasSegments => _count > 0;
            public int Count => _count;

            public WallObb this[int index] => _segments[index];
        }

        internal readonly struct WallObb
        {
            private readonly (float x, float z) _p0;
            private readonly (float x, float z) _p1;
            private readonly (float x, float z) _p2;
            private readonly (float x, float z) _p3;
            public readonly (float x, float z) A;
            public readonly (float x, float z) B;
            public readonly float HalfWidth;
            public readonly float MinX;
            public readonly float MaxX;
            public readonly float MinZ;
            public readonly float MaxZ;

            public WallObb(
                (float x, float z) p0,
                (float x, float z) p1,
                (float x, float z) p2,
                (float x, float z) p3,
                (float x, float z) a,
                (float x, float z) b,
                float halfWidth)
            {
                _p0 = p0;
                _p1 = p1;
                _p2 = p2;
                _p3 = p3;
                A = a;
                B = b;
                HalfWidth = halfWidth;
                MinX = MaxX = p0.x;
                MinZ = MaxZ = p0.z;
                for (int i = 1; i < 4; i++)
                {
                    (float x, float z) point = GetPoint(i);
                    if (point.x < MinX)
                    {
                        MinX = point.x;
                    }

                    if (point.x > MaxX)
                    {
                        MaxX = point.x;
                    }

                    if (point.z < MinZ)
                    {
                        MinZ = point.z;
                    }

                    if (point.z > MaxZ)
                    {
                        MaxZ = point.z;
                    }
                }
            }

            public (float x, float z) GetPoint(int index)
            {
                return index switch
                {
                    0 => _p0,
                    1 => _p1,
                    2 => _p2,
                    _ => _p3
                };
            }
        }
    }
}
