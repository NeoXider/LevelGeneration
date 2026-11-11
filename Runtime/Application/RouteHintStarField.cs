using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// Everything that shapes the golden star stream; the values come from the route hint ScriptableObject
    /// (<c>LevelMinimapSettings.CreateRouteHintStarStyle</c>), none of them is a constant in code.
    /// </summary>
    public readonly struct RouteHintStarStyle
    {
        public RouteHintStarStyle(
            float spacingMeters, int maxStars, float sizeMeters, float sizeMinScale, float sizeMaxScale,
            float sizeSkew, float speedMetersPerSecond, float heightMeters, float liftMeters,
            float lateralJitterMeters, float verticalJitterMeters, float twinkleHertz, float twinkleHertzSpread,
            float twinkleAlpha, float twinkleSize, float longTwinkleFraction, float longTwinkleSeconds,
            float longTwinkleDepth, float alongWobbleMeters, float lateralWobbleMeters, float wobbleSeconds,
            float startFadeMeters, float endFadeMeters, float revealFeatherMeters, float wallMarginMeters)
        {
            SpacingMeters = Mathf.Max(0.01f, spacingMeters);
            MaxStars = Mathf.Max(0, maxStars);
            SizeMeters = Mathf.Max(0.001f, sizeMeters);
            SizeMinScale = Mathf.Max(0.01f, sizeMinScale);
            SizeMaxScale = Mathf.Max(SizeMinScale, sizeMaxScale);
            SizeSkew = Mathf.Max(0.1f, sizeSkew);
            SpeedMetersPerSecond = Mathf.Max(0f, speedMetersPerSecond);
            HeightMeters = Mathf.Max(0f, heightMeters);
            LiftMeters = Mathf.Max(0f, liftMeters);
            LateralJitterMeters = Mathf.Max(0f, lateralJitterMeters);
            VerticalJitterMeters = Mathf.Max(0f, verticalJitterMeters);
            TwinkleHertz = Mathf.Max(0f, twinkleHertz);
            TwinkleHertzSpread = Mathf.Clamp01(twinkleHertzSpread);
            TwinkleAlpha = Mathf.Clamp01(twinkleAlpha);
            TwinkleSize = Mathf.Clamp01(twinkleSize);
            LongTwinkleFraction = Mathf.Clamp01(longTwinkleFraction);
            LongTwinkleSeconds = Mathf.Max(0.1f, longTwinkleSeconds);
            LongTwinkleDepth = Mathf.Clamp01(longTwinkleDepth);
            AlongWobbleMeters = Mathf.Max(0f, alongWobbleMeters);
            LateralWobbleMeters = Mathf.Max(0f, lateralWobbleMeters);
            WobbleSeconds = Mathf.Max(0.1f, wobbleSeconds);
            StartFadeMeters = Mathf.Max(0.001f, startFadeMeters);
            EndFadeMeters = Mathf.Max(0.001f, endFadeMeters);
            RevealFeatherMeters = Mathf.Max(0.001f, revealFeatherMeters);
            WallMarginMeters = Mathf.Max(0f, wallMarginMeters);
        }

        public float SpacingMeters { get; }
        public int MaxStars { get; }
        public float SizeMeters { get; }
        public float SizeMinScale { get; }
        public float SizeMaxScale { get; }

        /// <summary>Exponent on the random size fraction: above 1 most stars stay small and a few grow large.</summary>
        public float SizeSkew { get; }

        /// <summary>Mean drift of the stream toward the target; every star wobbles around it.</summary>
        public float SpeedMetersPerSecond { get; }

        public float HeightMeters { get; }
        public float LiftMeters { get; }
        public float LateralJitterMeters { get; }
        public float VerticalJitterMeters { get; }
        public float TwinkleHertz { get; }

        /// <summary>Per-star spread of the short twinkle frequency, a fraction of it (0.5 — from half to one and a half).</summary>
        public float TwinkleHertzSpread { get; }

        public float TwinkleAlpha { get; }
        public float TwinkleSize { get; }

        /// <summary>Share of stars that twinkle slowly and deeply instead of fast.</summary>
        public float LongTwinkleFraction { get; }

        public float LongTwinkleSeconds { get; }
        public float LongTwinkleDepth { get; }

        /// <summary>Amplitude of each star's slow back-and-forth along the trail: its own speed jitter.</summary>
        public float AlongWobbleMeters { get; }

        /// <summary>Amplitude of each star's slow sideways sway.</summary>
        public float LateralWobbleMeters { get; }

        public float WobbleSeconds { get; }
        public float StartFadeMeters { get; }
        public float EndFadeMeters { get; }
        public float RevealFeatherMeters { get; }

        /// <summary>Gap a star keeps from the nearest wall surface; scatter never uses it up.</summary>
        public float WallMarginMeters { get; }

        /// <summary>The widest a star can stray from the trail's line: jitter plus sway.</summary>
        public float MaxLateralMeters => LateralJitterMeters + LateralWobbleMeters;
    }

    /// <summary>One star of the stream, ready to be copied into a particle.</summary>
    public struct RouteHintStar
    {
        public Vector3 Position;
        public Vector3 Direction;
        public float Size;
        public float Brightness;
        public float LifetimeSeconds;
        public uint Seed;
    }

    /// <summary>
    /// The star stream as a pure function of the trail and the clock: a lattice of slots drifts along the
    /// trail, each slot owns one star whose size, twinkle (a minority twinkles long and slowly), sway and
    /// speed jitter come from its seed. Stars are anchored to distance along the trail, so a student walking
    /// ahead does not drag them with him; the visible part begins at the head and fades in there. Scatter to
    /// the side is clamped by the free width measured along the trail, so the stream never leans on a wall,
    /// at corners and doorways as well. No allocation: results go into a reused array.
    /// </summary>
    public sealed class RouteHintStarField
    {
        public RouteHintStarField(int capacity)
        {
            Stars = new RouteHintStar[Math.Max(0, capacity)];
        }

        public RouteHintStar[] Stars { get; }

        /// <summary>
        /// Fills <see cref="Stars"/> and returns how many are alive. <paramref name="reveal"/> is the eased share
        /// (0..1) of the visible trail already lit; <paramref name="clearances"/> may be null (no limit).
        /// </summary>
        public int Sample(IReadOnlyList<Vector3> centerline, IReadOnlyList<float> distances,
            IReadOnlyList<float> clearances, float lengthMeters, float headDistance, float reveal, float alpha,
            float elapsedSeconds, in RouteHintStarStyle style)
        {
            int cap = Math.Min(style.MaxStars, Stars.Length);
            float visible = lengthMeters - headDistance;
            if (cap <= 0 || centerline == null || centerline.Count < 2 || visible <= 0.0001f || alpha <= 0.001f)
            {
                return 0;
            }

            float spacing = Mathf.Max(style.SpacingMeters, lengthMeters / cap);
            float travel = elapsedSeconds * style.SpeedMetersPerSecond + spacing * 0.5f;
            int cycle = Mathf.FloorToInt(travel / spacing);
            float phase = Mathf.Repeat(travel, spacing);
            float revealedUntil = headDistance + visible * Mathf.Clamp01(reveal);
            bool fullyRevealed = reveal >= 1f;
            int first = Math.Max(0, Mathf.CeilToInt((headDistance - style.AlongWobbleMeters - phase) / spacing));
            int count = 0;
            for (int i = first; count < cap; i++)
            {
                float slot = phase + i * spacing;
                if (slot - style.AlongWobbleMeters >= lengthMeters || slot - style.AlongWobbleMeters >= revealedUntil)
                {
                    break;
                }

                uint seed = unchecked((uint)(cycle - i) * 747796405u + 2891336453u);
                float sideHash = UnitHash(seed ^ 0x9e3779b9u);
                float sizeHash = UnitHash(seed ^ 0x85ebca6bu);
                float kindHash = UnitHash(seed ^ 0xc2b2ae35u);
                float rateHash = UnitHash(seed ^ 0x27d4eb2fu);
                float swayHash = UnitHash(seed ^ 0x165667b1u);
                float seedPhase = UnitHash(seed) * (Mathf.PI * 2f);

                float swayPeriod = style.WobbleSeconds * Mathf.Lerp(0.6f, 1.4f, swayHash);
                float swayAngle = elapsedSeconds * (Mathf.PI * 2f) / swayPeriod;
                float along = slot + style.AlongWobbleMeters * Mathf.Sin(swayAngle + sideHash * (Mathf.PI * 2f));
                if (along <= headDistance || along >= lengthMeters)
                {
                    continue;
                }

                bool slow = kindHash < style.LongTwinkleFraction;
                float flicker = 1f;
                float depth = 0f;
                if (slow)
                {
                    float period = style.LongTwinkleSeconds * Mathf.Lerp(0.7f, 1.3f, rateHash);
                    flicker = 0.5f + 0.5f * Mathf.Sin(elapsedSeconds * (Mathf.PI * 2f) / period + seedPhase);
                    depth = style.LongTwinkleDepth;
                }
                else if (style.TwinkleHertz > 0f)
                {
                    float hertz = style.TwinkleHertz
                                  * Mathf.Lerp(1f - style.TwinkleHertzSpread, 1f + style.TwinkleHertzSpread, rateHash);
                    flicker = 0.5f + 0.5f * Mathf.Sin(elapsedSeconds * hertz * (Mathf.PI * 2f) + seedPhase);
                    depth = style.TwinkleAlpha;
                }

                float startFade = Mathf.Clamp01((along - headDistance) / style.StartFadeMeters);
                float endFade = Mathf.Clamp01((lengthMeters - along) / style.EndFadeMeters);
                float front = fullyRevealed ? 1f
                    : Mathf.Clamp01((revealedUntil - along) / style.RevealFeatherMeters);
                float brightness = Mathf.Clamp01(alpha) * startFade * endFade * front * Mathf.Lerp(1f - depth, 1f, flicker);

                Vector3 point = PointAt(centerline, distances, along, out Vector3 tangent);
                Vector3 side = Vector3.Cross(Vector3.up, tangent).normalized;
                float freeWidth = clearances == null
                    ? float.MaxValue
                    : Mathf.Max(0f, ClearanceAt(clearances, distances, along) - style.WallMarginMeters);
                float lateralLimit = Mathf.Min(style.MaxLateralMeters, freeWidth);
                float lateral = (sideHash * 2f - 1f) * style.LateralJitterMeters
                                + style.LateralWobbleMeters
                                * Mathf.Sin(swayAngle * 0.7f + swayHash * (Mathf.PI * 2f));
                lateral = Mathf.Clamp(lateral, -lateralLimit, lateralLimit);
                point += side * lateral;
                point.y += style.HeightMeters - style.LiftMeters
                           + Mathf.Sin(swayAngle * 1.3f + seedPhase) * style.VerticalJitterMeters;

                float sizeScale = Mathf.Lerp(style.SizeMinScale, style.SizeMaxScale, Mathf.Pow(sizeHash, style.SizeSkew));
                float twinkleSize = Mathf.Lerp(1f - style.TwinkleSize, 1f, flicker);
                Stars[count++] = new RouteHintStar
                {
                    Position = point,
                    Direction = tangent.normalized,
                    Size = style.SizeMeters * sizeScale * twinkleSize,
                    Brightness = brightness,
                    LifetimeSeconds = slow ? style.LongTwinkleSeconds : 1f / Mathf.Max(0.1f, style.TwinkleHertz),
                    Seed = seed
                };
            }

            return count;
        }

        private static Vector3 PointAt(IReadOnlyList<Vector3> centerline, IReadOnlyList<float> distances,
            float distance, out Vector3 tangent)
        {
            int to = Mathf.Clamp(FirstIndexAtOrAfter(distances, distance), 1, centerline.Count - 1);
            int from = to - 1;
            float span = distances[to] - distances[from];
            float t = span > 0.0001f ? Mathf.Clamp01((distance - distances[from]) / span) : 0f;
            tangent = centerline[to] - centerline[from];
            tangent.y = 0f;
            if (tangent.sqrMagnitude < 0.000001f)
            {
                tangent = Vector3.forward;
            }

            return Vector3.Lerp(centerline[from], centerline[to], t);
        }

        private static float ClearanceAt(IReadOnlyList<float> clearances, IReadOnlyList<float> distances, float distance)
        {
            int to = Mathf.Clamp(FirstIndexAtOrAfter(distances, distance), 1, clearances.Count - 1);
            // The tighter of the two neighbouring samples: the free width between them is never larger.
            return Mathf.Min(clearances[to - 1], clearances[to]);
        }

        private static int FirstIndexAtOrAfter(IReadOnlyList<float> distances, float distance)
        {
            int low = 1;
            int high = distances.Count - 1;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (distances[middle] < distance)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        /// <summary>Deterministic 0..1 hash used for every per-star random choice.</summary>
        public static float UnitHash(uint value)
        {
            value ^= value >> 16;
            value = unchecked(value * 2246822519u);
            value ^= value >> 13;
            return (value & 0x00ffffffu) / 16777215f;
        }
    }
}
