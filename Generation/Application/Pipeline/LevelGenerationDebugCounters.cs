namespace RedoSchool.Features.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Lightweight diagnostics for level generation. These counters are intentionally runtime-safe and allocation-free
    /// after creation so editor preview and mission runtime can expose the same generation audit data.
    /// <para>
    /// Счётчики без префикса Run* сбрасываются на каждую попытку (<see cref="BeginAttempt"/>) и описывают последнюю
    /// стадию. Run* копятся за весь прогон (<see cref="Reset"/>) — по ним калибруется счётный бюджет размещения
    /// (<see cref="SocketBacktrackingRoomPlacer.CandidateEvaluationBudget"/>).
    /// </para>
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

        /// <summary>Оценённые позы-кандидаты (единица счётного бюджета размещения) в последней попытке.</summary>
        public int CandidateEvaluations { get; private set; }

        /// <summary>Сколько вызовов размещения в последней попытке исчерпали счётный бюджет работы.</summary>
        public int PlacementBudgetExhaustions { get; private set; }

        /// <summary>Оценённые позы-кандидаты за весь прогон (все попытки и стадии).</summary>
        public long RunCandidateEvaluations { get; private set; }

        /// <summary>Наибольшее число оценённых поз за одну попытку прогона — для калибровки бюджета.</summary>
        public int RunMaxCandidateEvaluationsPerAttempt { get; private set; }

        /// <summary>Сколько вызовов размещения за весь прогон исчерпали счётный бюджет работы.</summary>
        public int RunPlacementBudgetExhaustions { get; private set; }

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
            CandidateEvaluations = 0;
            PlacementBudgetExhaustions = 0;
            RunCandidateEvaluations = 0;
            RunMaxCandidateEvaluationsPerAttempt = 0;
            RunPlacementBudgetExhaustions = 0;
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
            CandidateEvaluations = 0;
            PlacementBudgetExhaustions = 0;
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

        public void IncrementCandidateEvaluations()
        {
            CandidateEvaluations++;
            RunCandidateEvaluations++;
            if (CandidateEvaluations > RunMaxCandidateEvaluationsPerAttempt)
            {
                RunMaxCandidateEvaluationsPerAttempt = CandidateEvaluations;
            }
        }

        public void IncrementPlacementBudgetExhaustions()
        {
            PlacementBudgetExhaustions++;
            RunPlacementBudgetExhaustions++;
        }

        public override string ToString()
        {
            return $"attempts={Attempts}, backtracks={Backtracks}, candidatesBuilt={CandidatesBuilt}, " +
                   $"overlapRejects={OverlapRejects}, overlapChecks={OverlapChecks}, " +
                   $"broadphaseSkips={BroadphaseSkips}, domainEmptyRejects={DomainEmptyRejects}, " +
                   $"candidateEvaluations={CandidateEvaluations}, " +
                   $"placementBudgetExhaustions={PlacementBudgetExhaustions}, " +
                   $"runCandidateEvaluations={RunCandidateEvaluations}, " +
                   $"runMaxCandidateEvaluationsPerAttempt={RunMaxCandidateEvaluationsPerAttempt}, " +
                   $"runPlacementBudgetExhaustions={RunPlacementBudgetExhaustions}, " +
                   $"lastSeed={LastAttemptSeed}, lastStage={LastStage}, timeoutStage={TimeoutStage}";
        }
    }
}
