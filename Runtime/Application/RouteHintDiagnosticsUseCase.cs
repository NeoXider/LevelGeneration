namespace RedoSchool.LevelGeneration.Runtime.Application
{
    public static class RouteHintDiagnosticsUseCase
    {
        public const float BlockedDelaySeconds = 20f;
        public const float LogIntervalSeconds = 15f;

        public static string SelectBlocker(bool enabled, bool playerInControl, string controlBlocker,
            RouteHintOutcome outcome, bool scaledTimeAdvancing, bool planned)
        {
            if (!enabled) return "HintDisabled";
            if (!playerInControl) return controlBlocker;
            if (outcome == RouteHintOutcome.NoCriticalPath) return "NoCriticalPath";
            if (outcome == RouteHintOutcome.Waiting && !scaledTimeAdvancing) return "ScaledTimeStopped";
            if (outcome == RouteHintOutcome.ShowHint && !planned) return "PathNotPlanned";
            return string.Empty;
        }

        public static bool ShouldLog(float blockedSeconds, float now, float lastLogTime) =>
            blockedSeconds >= BlockedDelaySeconds && now - lastLogTime >= LogIntervalSeconds;
    }
}
