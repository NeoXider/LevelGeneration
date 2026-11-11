namespace RedoSchool.LevelGeneration.Runtime.Application
{
    /// <summary>Why the route hint is re-planned (or not).</summary>
    public enum RouteHintReplanReason
    {
        /// <summary>The shown trail is still good: keep it and only move its start after the student.</summary>
        None = 0,

        /// <summary>Target, revealed rooms, target task or a transit door changed: the old trail is wrong.</summary>
        WorldChanged = 1,

        /// <summary>The student walked away from the trail by more than the allowed deviation.</summary>
        StudentLeftTrail = 2,

        /// <summary>There is no trail yet, or the last attempt failed: try again.</summary>
        NoTrail = 3
    }

    /// <summary>What the controller knows about the trail this frame.</summary>
    public readonly struct RouteHintReplanInput
    {
        public RouteHintReplanInput(bool hasTrail, bool worldChanged, float deviationMeters,
            float deviationLimitMeters, float now, float nextAllowedTime)
        {
            HasTrail = hasTrail;
            WorldChanged = worldChanged;
            DeviationMeters = deviationMeters;
            DeviationLimitMeters = deviationLimitMeters;
            Now = now;
            NextAllowedTime = nextAllowedTime;
        }

        public bool HasTrail { get; }

        /// <summary>Target version, reveal version, task state or the locked-door set differ from the plan.</summary>
        public bool WorldChanged { get; }

        /// <summary>Distance from the student to the trail (<see cref="RouteHintTrail.Follow"/>), metres.</summary>
        public float DeviationMeters { get; }

        public float DeviationLimitMeters { get; }

        /// <summary>Game time, seconds.</summary>
        public float Now { get; }

        /// <summary>The earliest time a throttled re-plan may happen.</summary>
        public float NextAllowedTime { get; }
    }

    /// <summary>
    /// Decides when the route hint plans again. A changed world re-plans at once; a student who left the trail
    /// re-plans from where he stands, but not more often than the throttle allows and never every frame; a
    /// student on the trail only drags its start along. Without state: the controller holds the times.
    /// </summary>
    public static class DecideRouteHintReplanUseCase
    {
        public static RouteHintReplanReason Execute(RouteHintReplanInput input)
        {
            if (input.WorldChanged)
            {
                return RouteHintReplanReason.WorldChanged;
            }

            bool throttleOpen = input.Now >= input.NextAllowedTime;
            if (!input.HasTrail)
            {
                return throttleOpen ? RouteHintReplanReason.NoTrail : RouteHintReplanReason.None;
            }

            return throttleOpen && input.DeviationMeters > input.DeviationLimitMeters
                ? RouteHintReplanReason.StudentLeftTrail
                : RouteHintReplanReason.None;
        }
    }
}
