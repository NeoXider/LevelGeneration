using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Generation.Application.V2.Planning
{
    public sealed class PacingBranchPlanner
    {
        private readonly QuizBranchGraphExpander quizBranchGraphExpander;

        public PacingBranchPlanner(QuizBranchGraphExpander quizBranchGraphExpander = null)
        {
            this.quizBranchGraphExpander = quizBranchGraphExpander ?? new QuizBranchGraphExpander();
        }

        public BranchPlanResult AddBranches(
            PacingGraphBuilder graph,
            IReadOnlyList<PacingGraphNode> currentNodes,
            LayoutPacingConfig config,
            IReadOnlyList<PlannedInteractiveContent> optionalContent,
            IDeterministicRandom random)
        {
            List<PacingGraphNode> hosts = CollectBranchHosts(currentNodes);
            if (hosts.Count == 0)
            {
                return new BranchPlanResult(0, 0);
            }

            int requestedBranches = config.SideBranchCount.Sample(random);
            int optionalPlaced = 0;
            int branchCreated = 0;
            int fillerUsed = 0;
            List<PacingGraphNode> availableHosts = new(hosts);
            List<PlannedInteractiveContent> optional = ShuffleOptionalContent(
                optionalContent ?? System.Array.Empty<PlannedInteractiveContent>(),
                random);

            for (int branchIndex = 0; branchIndex < requestedBranches && availableHosts.Count > 0; branchIndex++)
            {
                int hostIndex = random.Next(0, availableHosts.Count);
                PacingGraphNode host = availableHosts[hostIndex];
                availableHosts.RemoveAt(hostIndex);
                int branchLength = config.SideBranchLengthRooms.Sample(random);
                bool canPlaceOptional = optionalPlaced < config.MaxOptionalInteractiveBranches &&
                                        optionalPlaced < optional.Count;

                if (canPlaceOptional)
                {
                    PlannedInteractiveContent content = optional[optionalPlaced];
                    string optionalNodeId = "n_branch_" + branchIndex + "_optional";
                    int requiredDoors = content.IsSingleCorrectDoorBranch ? content.OptionCount + 1 : 1;
                    graph.AddNode(new PacingGraphNode(
                        optionalNodeId,
                        RoomKind.Interactive,
                        false,
                        branchLength == 0,
                        content.ContentId,
                        requiredDoors,
                        "optional_interactive",
                        BranchPurpose.OptionalInteractive));
                    graph.AddEdge(host.NodeId, optionalNodeId, false, "branch");
                    string previous = quizBranchGraphExpander.Expand(graph, optionalNodeId, content, false);
                    branchLength = ReserveFillerLength(branchLength, config, ref fillerUsed);
                    AddOrdinaryBranchTail(graph, previous, branchIndex, branchLength, "optional_tail_");
                    optionalPlaced++;
                    branchCreated++;
                    continue;
                }

                branchLength = ReserveFillerLength(branchLength, config, ref fillerUsed);
                if (branchLength <= 0)
                {
                    continue;
                }

                AddOrdinaryBranchTail(graph, host.NodeId, branchIndex, branchLength, "ordinary_");
                branchCreated++;
            }

            return new BranchPlanResult(branchCreated, optionalPlaced);
        }

        private static List<PlannedInteractiveContent> ShuffleOptionalContent(
            IReadOnlyList<PlannedInteractiveContent> optionalContent,
            IDeterministicRandom random)
        {
            List<PlannedInteractiveContent> result = new(optionalContent.Count);
            for (int i = 0; i < optionalContent.Count; i++)
            {
                result.Add(optionalContent[i]);
            }

            for (int i = result.Count - 1; i > 0; i--)
            {
                int j = random.Next(0, i + 1);
                (result[i], result[j]) = (result[j], result[i]);
            }

            return result;
        }

        private static void AddOrdinaryBranchTail(
            PacingGraphBuilder graph,
            string hostNodeId,
            int branchIndex,
            int length,
            string prefix)
        {
            string previous = hostNodeId;
            for (int i = 0; i < length; i++)
            {
                bool isTerminal = i == length - 1;
                string nodeId = "n_branch_" + branchIndex + "_" + prefix + i;
                graph.AddNode(new PacingGraphNode(nodeId, RoomKind.Passage, false, isTerminal, null, isTerminal ? 1 : 2,
                    "side_branch", BranchPurpose.Filler));
                graph.AddEdge(previous, nodeId, false, "branch");
                previous = nodeId;
            }
        }

        private static int ReserveFillerLength(int requestedLength, LayoutPacingConfig config, ref int fillerUsed)
        {
            if (requestedLength <= 0)
            {
                return 0;
            }

            int cap = config.OptionalFillerCap;
            if (cap <= 0)
            {
                fillerUsed += requestedLength;
                return requestedLength;
            }

            int remaining = cap - fillerUsed;
            if (remaining <= 0)
            {
                return 0;
            }

            int accepted = System.Math.Min(requestedLength, remaining);
            fillerUsed += accepted;
            return accepted;
        }

        private static List<PacingGraphNode> CollectBranchHosts(IReadOnlyList<PacingGraphNode> nodes)
        {
            List<PacingGraphNode> result = new();
            for (int i = 0; i < nodes.Count; i++)
            {
                PacingGraphNode node = nodes[i];
                if (node.Kind == RoomKind.Passage && node.IsOnMainPath)
                {
                    result.Add(node);
                }
            }

            return result;
        }
    }

    public sealed class BranchPlanResult
    {
        public BranchPlanResult(int branchCount, int optionalInteractiveCount)
        {
            BranchCount = branchCount;
            OptionalInteractiveCount = optionalInteractiveCount;
        }

        public int BranchCount { get; }
        public int OptionalInteractiveCount { get; }
    }
}
