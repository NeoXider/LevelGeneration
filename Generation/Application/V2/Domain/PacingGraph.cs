using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Generation.Application.V2.Domain
{
    public sealed class PacingGraphNode
    {
        public PacingGraphNode(
            string nodeId,
            RoomKind kind,
            bool isOnMainPath,
            bool isTerminal = false,
            string contentId = null,
            int requiredDoorCount = 0,
            string source = null,
            BranchPurpose branchPurpose = BranchPurpose.Filler)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                throw new ArgumentException("Node id must be non-empty.", nameof(nodeId));
            }

            NodeId = nodeId;
            Kind = kind;
            IsOnMainPath = isOnMainPath;
            IsTerminal = isTerminal;
            ContentId = contentId;
            RequiredDoorCount = Math.Max(0, requiredDoorCount);
            Source = source ?? string.Empty;
            BranchPurpose = branchPurpose;
        }

        public string NodeId { get; }
        public RoomKind Kind { get; }
        public bool IsOnMainPath { get; }
        public bool IsTerminal { get; }
        public string ContentId { get; }
        public int RequiredDoorCount { get; }
        public string Source { get; }
        public BranchPurpose BranchPurpose { get; }

        public PacingGraphNode WithRequiredDoorCount(int requiredDoorCount)
        {
            return new PacingGraphNode(NodeId, Kind, IsOnMainPath, IsTerminal, ContentId, requiredDoorCount, Source,
                BranchPurpose);
        }
    }

    public sealed class PacingGraphEdge
    {
        public PacingGraphEdge(string fromNodeId, string toNodeId, bool isMainPath, string label = null)
        {
            FromNodeId = fromNodeId;
            ToNodeId = toNodeId;
            IsMainPath = isMainPath;
            Label = label ?? string.Empty;
        }

        public string FromNodeId { get; }
        public string ToNodeId { get; }
        public bool IsMainPath { get; }
        public string Label { get; }
    }

    public sealed class PacingGraph
    {
        public PacingGraph(IReadOnlyList<PacingGraphNode> nodes, IReadOnlyList<PacingGraphEdge> edges)
        {
            Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            Edges = edges ?? Array.Empty<PacingGraphEdge>();
        }

        public IReadOnlyList<PacingGraphNode> Nodes { get; }
        public IReadOnlyList<PacingGraphEdge> Edges { get; }

        public PacingGraphNode FindNode(string nodeId)
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].NodeId == nodeId)
                {
                    return Nodes[i];
                }
            }

            return null;
        }

        public bool IsReachable(string fromNodeId, string toNodeId)
        {
            HashSet<string> visited = new();
            Queue<string> queue = new();
            queue.Enqueue(fromNodeId);
            visited.Add(fromNodeId);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (current == toNodeId)
                {
                    return true;
                }

                for (int i = 0; i < Edges.Count; i++)
                {
                    PacingGraphEdge edge = Edges[i];
                    if (edge.FromNodeId != current || visited.Contains(edge.ToNodeId))
                    {
                        continue;
                    }

                    visited.Add(edge.ToNodeId);
                    queue.Enqueue(edge.ToNodeId);
                }
            }

            return false;
        }
    }

    public sealed class PacingGraphBuilder
    {
        private readonly List<PacingGraphNode> nodes = new();
        private readonly List<PacingGraphEdge> edges = new();

        public void AddNode(PacingGraphNode node) => nodes.Add(node);

        public void AddEdge(string fromNodeId, string toNodeId, bool isMainPath, string label = null) => edges.Add(new PacingGraphEdge(fromNodeId, toNodeId, isMainPath, label));

        public PacingGraph Build()
        {
            Dictionary<string, int> degreeByNode = new();
            for (int i = 0; i < edges.Count; i++)
            {
                IncrementDegree(degreeByNode, edges[i].FromNodeId);
                IncrementDegree(degreeByNode, edges[i].ToNodeId);
            }

            List<PacingGraphNode> finalNodes = new(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                PacingGraphNode node = nodes[i];
                int degree = degreeByNode.TryGetValue(node.NodeId, out int value) ? value : 0;
                finalNodes.Add(node.WithRequiredDoorCount(Math.Max(node.RequiredDoorCount, degree)));
            }

            return new PacingGraph(finalNodes, edges.ToArray());
        }

        private static void IncrementDegree(IDictionary<string, int> degreeByNode, string nodeId)
        {
            if (degreeByNode.TryGetValue(nodeId, out int value))
            {
                degreeByNode[nodeId] = value + 1;
            }
            else
            {
                degreeByNode[nodeId] = 1;
            }
        }
    }
}
