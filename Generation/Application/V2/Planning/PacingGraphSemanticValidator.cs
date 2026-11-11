using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;

namespace RedoSchool.LevelGeneration.Generation.Application.V2.Planning
{
    public sealed class PacingGraphSemanticValidator
    {
        public bool Validate(PacingGraph graph, MissionContentPlan contentPlan)
        {
            if (graph == null ||
                graph.FindNode("n_start") == null ||
                graph.FindNode("n_exit") == null ||
                !graph.IsReachable("n_start", "n_exit"))
            {
                return false;
            }

            if (!ValidateContentPlacement(graph, contentPlan))
            {
                return false;
            }

            return ValidateAnswerSemantics(graph);
        }

        private static bool ValidateContentPlacement(PacingGraph graph, MissionContentPlan contentPlan)
        {
            if (contentPlan == null)
            {
                return true;
            }

            HashSet<string> mandatoryIds = new();
            for (int i = 0; i < contentPlan.Mandatory.Count; i++)
            {
                mandatoryIds.Add(contentPlan.Mandatory[i].ContentId);
            }

            HashSet<string> optionalIds = new();
            for (int i = 0; i < contentPlan.Optional.Count; i++)
            {
                optionalIds.Add(contentPlan.Optional[i].ContentId);
            }

            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                PacingGraphNode node = graph.Nodes[i];
                if (string.IsNullOrWhiteSpace(node.ContentId))
                {
                    continue;
                }

                if (mandatoryIds.Contains(node.ContentId) && !node.IsOnMainPath)
                {
                    return false;
                }

                if (optionalIds.Contains(node.ContentId) && node.IsOnMainPath)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ValidateAnswerSemantics(PacingGraph graph)
        {
            for (int i = 0; i < graph.Edges.Count; i++)
            {
                PacingGraphEdge edge = graph.Edges[i];
                if (edge.Label == "wrong")
                {
                    PacingGraphNode wrongNode = graph.FindNode(edge.ToNodeId);
                    if (wrongNode == null ||
                        wrongNode.IsOnMainPath ||
                        !wrongNode.IsTerminal ||
                        wrongNode.BranchPurpose != BranchPurpose.WrongAnswerDeadEnd)
                    {
                        return false;
                    }
                }

                if (edge.Label == "correct")
                {
                    PacingGraphNode fromNode = graph.FindNode(edge.FromNodeId);
                    PacingGraphNode toNode = graph.FindNode(edge.ToNodeId);
                    if (fromNode != null &&
                        fromNode.IsOnMainPath &&
                        (toNode == null || !toNode.IsOnMainPath || !edge.IsMainPath))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
