using System;
using System.Collections.Generic;
using System.Threading;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Application.V2.Diagnostics;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.V2.Placement
{
    public sealed class PacingSocketPlacementRunner
    {
        private readonly IRoomPlacer roomPlacer;

        public PacingSocketPlacementRunner(IRoomPlacer roomPlacer)
        {
            this.roomPlacer = roomPlacer ?? throw new ArgumentNullException(nameof(roomPlacer));
        }

        public PacingPlacementResult Place(
            PacingGraph graph,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            IDeterministicRng rng,
            CancellationToken cancellationToken = default)
        {
            PlacementDiagnostics diagnostics = new();
            if (cancellationToken.IsCancellationRequested)
            {
                diagnostics.UnplacedNodeCount = graph?.Nodes.Count ?? 0;
                return PacingPlacementResult.Failed(diagnostics);
            }

            try
            {
                LevelGraph levelGraph = ToLevelGraph(graph);
                LevelLayout layout = roomPlacer.Place(levelGraph, selections, rng, cancellationToken);
                if (layout == null)
                {
                    diagnostics.UnplacedNodeCount = graph?.Nodes.Count ?? 0;
                    return PacingPlacementResult.Failed(diagnostics);
                }

                diagnostics.CandidateCount = layout.Rooms.Count;
                diagnostics.UnplacedNodeCount = Math.Max(0, (graph?.Nodes.Count ?? 0) - layout.Rooms.Count);
                return PacingPlacementResult.Succeeded(layout, diagnostics);
            }
            catch (OperationCanceledException)
            {
                diagnostics.UnplacedNodeCount = graph?.Nodes.Count ?? 0;
                return PacingPlacementResult.Failed(diagnostics);
            }
        }

        public static LevelGraph ToLevelGraph(PacingGraph graph)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            List<RoomNode> nodes = new(graph.Nodes.Count);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                nodes.Add(Selection.V2RoomSelectionAdapter.ToRoomNode(graph.Nodes[i]));
            }

            List<LevelGraphEdge> edges = new(graph.Edges.Count);
            HashSet<string> mazeBranchNodeIds = FindMazeBranchNodeIds(graph);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                PacingGraphEdge edge = graph.Edges[i];
                edges.Add(new LevelGraphEdge(graph.Edges[i].FromNodeId, graph.Edges[i].ToNodeId,
                    graph.Edges[i].IsMainPath, ResolveBranchRole(graph, edge, mazeBranchNodeIds)));
            }

            return new LevelGraph(nodes, edges);
        }

        private static HashSet<string> FindMazeBranchNodeIds(PacingGraph graph)
        {
            HashSet<string> result = new(StringComparer.Ordinal);
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                PacingGraphEdge edge = graph.Edges[i];
                if (IsMazeAnswerLabel(edge.Label))
                {
                    result.Add(edge.FromNodeId);
                }
            }

            return result;
        }

        private static EdgeBranchRole ResolveBranchRole(
            PacingGraph graph,
            PacingGraphEdge edge,
            ISet<string> mazeBranchNodeIds)
        {
            if (edge == null || mazeBranchNodeIds == null || mazeBranchNodeIds.Count == 0)
            {
                return EdgeBranchRole.None;
            }

            if (string.Equals(edge.Label, "correct", StringComparison.Ordinal)
                && mazeBranchNodeIds.Contains(edge.FromNodeId))
            {
                return EdgeBranchRole.MazeCorrectExit;
            }

            if (string.Equals(edge.Label, "wrong", StringComparison.Ordinal)
                && mazeBranchNodeIds.Contains(edge.FromNodeId)
                && graph.FindNode(edge.ToNodeId)?.BranchPurpose == BranchPurpose.WrongAnswerDeadEnd)
            {
                return EdgeBranchRole.MazeWrongDeadEnd;
            }

            if (mazeBranchNodeIds.Contains(edge.ToNodeId))
            {
                return EdgeBranchRole.MazeEntry;
            }

            return EdgeBranchRole.None;
        }

        private static bool IsMazeAnswerLabel(string label)
        {
            return string.Equals(label, "correct", StringComparison.Ordinal)
                   || string.Equals(label, "wrong", StringComparison.Ordinal);
        }
    }

    public sealed class PacingPlacementResult
    {
        private PacingPlacementResult(bool success, LevelLayout layout, PlacementDiagnostics diagnostics)
        {
            Success = success;
            Layout = layout;
            Diagnostics = diagnostics;
        }

        public bool Success { get; }
        public LevelLayout Layout { get; }
        public PlacementDiagnostics Diagnostics { get; }

        public static PacingPlacementResult Succeeded(LevelLayout layout, PlacementDiagnostics diagnostics) => new(true, layout, diagnostics);

        public static PacingPlacementResult Failed(PlacementDiagnostics diagnostics) => new(false, null, diagnostics);
    }
}
