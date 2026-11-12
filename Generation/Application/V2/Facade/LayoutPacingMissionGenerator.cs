using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application.V2.Diagnostics;
using Neoxider.LevelGeneration.Generation.Application.V2.Domain;
using Neoxider.LevelGeneration.Generation.Application.V2.Planning;
using Neoxider.LevelGeneration.Generation.Application.V2.Policies;
using Neoxider.LevelGeneration.Generation.Domain.Enums;

namespace Neoxider.LevelGeneration.Generation.Application.V2.Facade
{
    public sealed class LayoutGenerationRequest
    {
        public LayoutGenerationRequest(
            int seed,
            LayoutPacingConfig config,
            MissionContentPlan contentPlan,
            float? timeBudgetOverrideSeconds = null)
        {
            Seed = seed;
            Config = config ?? LayoutPacingDefaults.StableRuntime();
            ContentPlan = contentPlan ?? new MissionContentPlan(Array.Empty<PlannedInteractiveContent>(),
                Array.Empty<PlannedInteractiveContent>());
            TimeBudgetOverrideSeconds = timeBudgetOverrideSeconds;
        }

        public int Seed { get; }
        public LayoutPacingConfig Config { get; }
        public MissionContentPlan ContentPlan { get; }
        public float? TimeBudgetOverrideSeconds { get; }
    }

    public sealed class LayoutGenerationResult
    {
        private LayoutGenerationResult(
            bool success,
            LayoutGenerationFailureReason failureReason,
            PacingGraph graph,
            int successfulSeed,
            LayoutRetryStage retryStage,
            long elapsedMilliseconds,
            LayoutGenerationDiagnostics diagnostics)
        {
            Success = success;
            FailureReason = failureReason;
            Graph = graph;
            SuccessfulSeed = successfulSeed;
            RetryStage = retryStage;
            ElapsedMilliseconds = elapsedMilliseconds;
            Diagnostics = diagnostics;
        }

        public bool Success { get; }
        public LayoutGenerationFailureReason FailureReason { get; }
        public PacingGraph Graph { get; }
        public int SuccessfulSeed { get; }
        public LayoutRetryStage RetryStage { get; }
        public long ElapsedMilliseconds { get; }
        public LayoutGenerationDiagnostics Diagnostics { get; }

        public static LayoutGenerationResult Succeeded(
            PacingGraph graph,
            int seed,
            LayoutRetryStage stage,
            long elapsedMilliseconds,
            LayoutGenerationDiagnostics diagnostics)
        {
            return new LayoutGenerationResult(true, LayoutGenerationFailureReason.None, graph, seed, stage,
                elapsedMilliseconds, diagnostics);
        }

        public static LayoutGenerationResult Failed(LayoutGenerationFailureReason reason, int seed, LayoutRetryStage stage, long elapsedMilliseconds, LayoutGenerationDiagnostics diagnostics) => new(false, reason, null, seed, stage, elapsedMilliseconds, diagnostics);
    }

    public sealed class LayoutPacingMissionGenerator
    {
        private readonly LayoutRetryPlanner retryPlanner;
        private readonly PacingMainPathPlanner mainPathPlanner;
        private readonly PacingBranchPlanner branchPlanner;
        private readonly PacingGraphSemanticValidator semanticValidator;

        public LayoutPacingMissionGenerator(
            LayoutRetryPlanner retryPlanner = null,
            PacingMainPathPlanner mainPathPlanner = null,
            PacingBranchPlanner branchPlanner = null,
            PacingGraphSemanticValidator semanticValidator = null)
        {
            this.retryPlanner = retryPlanner ?? new LayoutRetryPlanner();
            this.mainPathPlanner = mainPathPlanner ?? new PacingMainPathPlanner();
            this.branchPlanner = branchPlanner ?? new PacingBranchPlanner();
            this.semanticValidator = semanticValidator ?? new PacingGraphSemanticValidator();
        }

        public LayoutGenerationResult Generate(LayoutGenerationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            GenerationTimeBudgetPolicy budget = new(request.Config, request.TimeBudgetOverrideSeconds);
            IReadOnlyList<LayoutRetryStage> stages = GetStages(request.Config.RetryPolicy);
            LayoutGenerationDiagnostics lastDiagnostics = null;

            for (int attempt = 0; attempt < stages.Count; attempt++)
            {
                if (budget.IsExpired)
                {
                    return LayoutGenerationResult.Failed(
                        LayoutGenerationFailureReason.Timeout,
                        request.Seed,
                        stages[attempt],
                        budget.ElapsedMilliseconds,
                        lastDiagnostics ?? CreateDiagnostics(request, attempt, stages[attempt], null, 0, 0,
                            budget.ElapsedMilliseconds));
                }

                LayoutRetryStage stage = stages[attempt];
                LayoutPacingConfig stageConfig = retryPlanner.Apply(request.Config, stage);
                DeterministicRandom random = new(request.Seed + attempt);
                PacingGraphBuilder graphBuilder = new();
                MainPathPlanResult mainPath =
                    mainPathPlanner.Build(graphBuilder, stageConfig, request.ContentPlan.Mandatory, random);
                PacingGraph partialGraph = graphBuilder.Build();
                BranchPlanResult branches = branchPlanner.AddBranches(graphBuilder, partialGraph.Nodes, stageConfig,
                    request.ContentPlan.Optional, random);
                PacingGraph graph = graphBuilder.Build();

                lastDiagnostics = CreateDiagnostics(request, attempt, stage, graph, mainPath.MainPathNodeIds.Count,
                    branches.BranchCount, budget.ElapsedMilliseconds);
                lastDiagnostics.OptionalInteractiveCount = branches.OptionalInteractiveCount;

                if (semanticValidator.Validate(graph, request.ContentPlan))
                {
                    return LayoutGenerationResult.Succeeded(graph, request.Seed, stage, budget.ElapsedMilliseconds,
                        lastDiagnostics);
                }
            }

            return LayoutGenerationResult.Failed(
                LayoutGenerationFailureReason.NoValidLayout,
                request.Seed,
                stages[stages.Count - 1],
                budget.ElapsedMilliseconds,
                lastDiagnostics);
        }

        private static LayoutGenerationDiagnostics CreateDiagnostics(
            LayoutGenerationRequest request,
            int attempt,
            LayoutRetryStage stage,
            PacingGraph graph,
            int mainPathCount,
            int branchCount,
            long elapsedMilliseconds)
        {
            LayoutGenerationDiagnostics diagnostics = new()
            {
                Seed = request.Seed,
                Attempt = attempt,
                RetryStage = stage,
                MainPathCount = mainPathCount,
                MandatoryInteractiveCount = request.ContentPlan.Mandatory.Count,
                BranchCount = branchCount,
                ElapsedMilliseconds = elapsedMilliseconds
            };

            if (graph != null)
            {
                diagnostics.GraphNodeCount = graph.Nodes.Count;
                diagnostics.OptionalInteractiveCount = CountOptionalInteractive(graph);
            }

            return diagnostics;
        }

        private static int CountOptionalInteractive(PacingGraph graph)
        {
            int count = 0;
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (graph.Nodes[i].Kind == RoomKind.Interactive && !graph.Nodes[i].IsOnMainPath)
                {
                    count++;
                }
            }

            return count;
        }

        private static IReadOnlyList<LayoutRetryStage> GetStages(LayoutRetryPolicy retryPolicy) => ResolveRetryStages(retryPolicy);

        /// <summary>
        /// Single source of truth for the retry-stage ladder. The ladder is identical across all platforms
        /// (no <c>#if UNITY_WEBGL</c> fork). <see cref="MissionGenerator"/> reuses this list so the ladder cannot
        /// diverge between the V1 facade and the V2 generator.
        /// <para>
        /// Одинаковая лесенка — необходимое, но не достаточное условие одинаковой раскладки сида. Здесь, в
        /// <see cref="Generate"/>, истёкший <c>GenerationTimeBudgetPolicy</c> прерывает запрос (Failed/Timeout) до
        /// следующей стадии — это отказ, а не другая раскладка. <see cref="MissionGenerator"/> вызывает генератор по
        /// одной стадии (FailFast) без собственного таймера, ограничивает размещение счётными бюджетами и
        /// использует часы только для прерывания всего прогона, поэтому исход сида от скорости машины не зависит.
        /// </para>
        /// </summary>
        public static IReadOnlyList<LayoutRetryStage> ResolveRetryStages(LayoutRetryPolicy retryPolicy)
        {
            if (retryPolicy == LayoutRetryPolicy.FailFast || retryPolicy == LayoutRetryPolicy.None)
            {
                return new[] { LayoutRetryStage.Full };
            }

            return new[] { LayoutRetryStage.Full, LayoutRetryStage.TrimBranches, LayoutRetryStage.DisableOptionalInteractive, LayoutRetryStage.MandatoryOnly };
        }
    }
}
