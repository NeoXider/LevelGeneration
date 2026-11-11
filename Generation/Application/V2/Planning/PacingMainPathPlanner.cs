using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Generation.Application.V2.Planning
{
    public sealed class PacingMainPathPlanner
    {
        // Dense mandatory chains must keep the authored content intact without turning every wrong quiz answer
        // into a backtracking graph branch. For those missions, the main graph keeps only the correct
        // MazeBranch tunnel; free answer doors are left for DeadEndRoomPostProcessor after the main route is
        // placed. A small spacer range still gives rigid rooms enough room to turn without producing a huge
        // 30+ room lesson.
        private const int DenseMandatoryThreshold = 5;
        private const int BetweenMandatoryMinGap = 1;
        private const int BetweenMandatoryMaxGap = 2;

        private readonly QuizBranchGraphExpander quizBranchGraphExpander;

        public PacingMainPathPlanner(QuizBranchGraphExpander quizBranchGraphExpander = null)
        {
            this.quizBranchGraphExpander = quizBranchGraphExpander ?? new QuizBranchGraphExpander();
        }

        public MainPathPlanResult Build(
            PacingGraphBuilder graph,
            LayoutPacingConfig config,
            IReadOnlyList<PlannedInteractiveContent> mandatoryContent,
            IDeterministicRandom random)
        {
            List<string> mainPathNodeIds = new();
            List<string> interactiveNodeIds = new();

            graph.AddNode(new PacingGraphNode("n_start", RoomKind.Start, true, false, null, 1, "start"));
            mainPathNodeIds.Add("n_start");

            string previousNodeId = "n_start";
            IReadOnlyList<PlannedInteractiveContent> mandatory =
                mandatoryContent ?? System.Array.Empty<PlannedInteractiveContent>();

            // Wider spacing is only needed BETWEEN consecutive rigid interactive rooms, so it applies to gaps
            // before every interactive after the first; the start->first and last->exit gaps keep the authored
            // spacing (the endpoints are flexible Start/Exit rooms, not rigid).
            bool denseMandatoryChain = mandatory.Count >= DenseMandatoryThreshold;
            PacingIntRange betweenMandatoryGap = denseMandatoryChain
                ? new PacingIntRange(BetweenMandatoryMinGap, BetweenMandatoryMaxGap)
                : config.MainPathGapRooms;
            for (int i = 0; i < mandatory.Count; i++)
            {
                PacingIntRange gapRange = i > 0 ? betweenMandatoryGap : config.MainPathGapRooms;
                previousNodeId = AddGap(graph, gapRange, random, previousNodeId, mainPathNodeIds,
                    "n_main_gap_" + i + "_");

                PlannedInteractiveContent content = mandatory[i];
                int requiredDoors = content.IsSingleCorrectDoorBranch ? content.OptionCount + 1 : 2;
                string interactiveNodeId = "n_main_interactive_" + i;
                graph.AddNode(new PacingGraphNode(
                    interactiveNodeId,
                    RoomKind.Interactive,
                    true,
                    false,
                    content.ContentId,
                    requiredDoors,
                    "mandatory_interactive"));
                graph.AddEdge(previousNodeId, interactiveNodeId, true, "main");
                mainPathNodeIds.Add(interactiveNodeId);
                interactiveNodeIds.Add(interactiveNodeId);

                // Reserve EVERY wrong-answer door as a dead-end node in the graph (even for dense chains).
                // Requirement: in a maze branch, all non-entry/non-correct doors MUST be real dead-end rooms.
                // Reserving them up front makes the backtracking placer responsible for fitting them (and reject
                // a seed that can't), which guarantees the rooms exist — instead of the post-pass that silently
                // sealed doors when the single cap template didn't fit a dense layout. Costs more placement work,
                // mitigated by the 30s runtime budget + the placer's GC undo-log.
                previousNodeId = quizBranchGraphExpander.Expand(
                    graph,
                    interactiveNodeId,
                    content,
                    true,
                    true);
                if (previousNodeId != interactiveNodeId)
                {
                    mainPathNodeIds.Add(previousNodeId);
                }
            }

            previousNodeId = AddGap(graph, config.MainPathGapRooms, random, previousNodeId, mainPathNodeIds,
                "n_main_final_gap_");

            graph.AddNode(new PacingGraphNode("n_exit", RoomKind.Exit, true, false, null, 1, "exit"));
            graph.AddEdge(previousNodeId, "n_exit", true, "main");
            mainPathNodeIds.Add("n_exit");

            return new MainPathPlanResult(mainPathNodeIds, interactiveNodeIds);
        }

        private static string AddGap(
            PacingGraphBuilder graph,
            PacingIntRange gapRange,
            IDeterministicRandom random,
            string previousNodeId,
            ICollection<string> mainPathNodeIds,
            string prefix)
        {
            int gapCount = gapRange.Sample(random);
            for (int gap = 0; gap < gapCount; gap++)
            {
                string nodeId = prefix + gap;
                graph.AddNode(new PacingGraphNode(nodeId, RoomKind.Passage, true, false, null, 2, "main_gap"));
                graph.AddEdge(previousNodeId, nodeId, true, "main");
                mainPathNodeIds.Add(nodeId);
                previousNodeId = nodeId;
            }

            return previousNodeId;
        }
    }

    public sealed class MainPathPlanResult
    {
        public MainPathPlanResult(IReadOnlyList<string> mainPathNodeIds, IReadOnlyList<string> interactiveNodeIds)
        {
            MainPathNodeIds = mainPathNodeIds;
            InteractiveNodeIds = interactiveNodeIds;
        }

        public IReadOnlyList<string> MainPathNodeIds { get; }
        public IReadOnlyList<string> InteractiveNodeIds { get; }
    }
}
