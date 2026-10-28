using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Application.V2.Domain;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.V2.Diagnostics
{
    public sealed class LayoutGenerationDiagnostics
    {
        public int Seed { get; set; }
        public int Attempt { get; set; }
        public LayoutRetryStage RetryStage { get; set; }
        public int GraphNodeCount { get; set; }
        public int MainPathCount { get; set; }
        public int MandatoryInteractiveCount { get; set; }
        public int OptionalInteractiveCount { get; set; }
        public int BranchCount { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public PlacementDiagnostics Placement { get; } = new();
        public List<DoorSnapDiagnostic> DoorSnapDiagnostics { get; } = new();
    }

    public sealed class PlacementDiagnostics
    {
        public int CandidateCount { get; set; }
        public int RejectedCollisionCount { get; set; }
        public int RejectedDomainCount { get; set; }
        public int SkippedByMemoryCount { get; set; }
        public int UnplacedNodeCount { get; set; }
    }

    public sealed class DoorSnapDiagnostic
    {
        public string ConnectionId { get; set; }
        public float CenterDeltaMeters { get; set; }
        public float YawDeltaDegrees { get; set; }
        public float FrameWidthDeltaMeters { get; set; }
        public string Source { get; set; }
    }
}
