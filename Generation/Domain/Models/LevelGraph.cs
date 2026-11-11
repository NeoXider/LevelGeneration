using System;
using System.Collections.Generic;

namespace RedoSchool.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Pure logical graph of a mission: ordered list of <see cref="RoomNode"/>s plus directed edges by NodeId.
    /// Built by <c>IMissionGraphBuilder</c> before any Unity-side placement.
    /// </summary>
    public sealed class LevelGraph
    {
        public LevelGraph(IReadOnlyList<RoomNode> nodes, IReadOnlyList<LevelGraphEdge> edges)
        {
            Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            Edges = edges ?? Array.Empty<LevelGraphEdge>();
        }

        public IReadOnlyList<RoomNode> Nodes { get; }
        public IReadOnlyList<LevelGraphEdge> Edges { get; }
    }

    /// <summary>
    /// Directed edge between two <see cref="RoomNode"/>s in the level graph.
    /// </summary>
    public enum EdgeBranchRole
    {
        None = 0,
        MazeEntry = 1,
        MazeCorrectExit = 2,
        MazeWrongDeadEnd = 3
    }

    public readonly struct LevelGraphEdge
    {
        public readonly string FromNodeId;
        public readonly string ToNodeId;
        public readonly bool IsMainPath;
        public readonly EdgeBranchRole BranchRole;

        public LevelGraphEdge(
            string fromNodeId,
            string toNodeId,
            bool isMainPath,
            EdgeBranchRole branchRole = EdgeBranchRole.None)
        {
            FromNodeId = fromNodeId;
            ToNodeId = toNodeId;
            IsMainPath = isMainPath;
            BranchRole = branchRole;
        }
    }
}
