namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Lightweight diagnostics for level generation. These counters are intentionally runtime-safe and allocation-free
    /// after creation so editor preview and mission runtime can expose the same generation audit data.
    /// </summary>
    public sealed class LevelGenerationDebugCounters
    {
        public int Attempts { get; private set; }
        public int Backtracks { get; private set; }
        public int CandidatesBuilt { get; private set; }
        public int OverlapRejects { get; private set; }
        public int OverlapChecks { get; private set; }
        public int BroadphaseSkips { get; private set; }
        public int DomainEmptyRejects { get; private set; }
        public int LastAttemptSeed { get; private set; }
        public string LastStage { get; private set; } = "not-started";
        public string TimeoutStage { get; private set; } = string.Empty;

        public void Reset()
        {
            Attempts = 0;
            Backtracks = 0;
            CandidatesBuilt = 0;
            OverlapRejects = 0;
            OverlapChecks = 0;
            BroadphaseSkips = 0;
            DomainEmptyRejects = 0;
            LastAttemptSeed = 0;
            LastStage = "not-started";
            TimeoutStage = string.Empty;
        }

        public void BeginAttempt(int seed)
        {
            Attempts++;
            LastAttemptSeed = seed;
            Backtracks = 0;
            CandidatesBuilt = 0;
            OverlapRejects = 0;
            OverlapChecks = 0;
            BroadphaseSkips = 0;
            DomainEmptyRejects = 0;
            SetStage("attempt");
        }

        public void SetStage(string stage) => LastStage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage;

        public void MarkTimeout() => TimeoutStage = LastStage;

        public void IncrementBacktracks() => Backtracks++;

        public void IncrementCandidatesBuilt() => CandidatesBuilt++;

        public void IncrementOverlapRejects() => OverlapRejects++;

        public void IncrementOverlapChecks() => OverlapChecks++;

        public void IncrementBroadphaseSkips() => BroadphaseSkips++;

        public void IncrementDomainEmptyRejects() => DomainEmptyRejects++;

        public override string ToString()
        {
            return $"attempts={Attempts}, backtracks={Backtracks}, candidatesBuilt={CandidatesBuilt}, " +
                   $"overlapRejects={OverlapRejects}, overlapChecks={OverlapChecks}, " +
                   $"broadphaseSkips={BroadphaseSkips}, domainEmptyRejects={DomainEmptyRejects}, " +
                   $"lastSeed={LastAttemptSeed}, lastStage={LastStage}, timeoutStage={TimeoutStage}";
        }
    }
}
