using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Generation.Application.V2.Planning
{
    public sealed class QuizBranchGraphExpander
    {
        private const int DefaultCorrectTunnelLength = 1;
        private const int FourOptionCorrectTunnelLength = 2;

        public string Expand(
            PacingGraphBuilder graph,
            string quizNodeId,
            PlannedInteractiveContent content,
            bool isMainPath,
            bool includeWrongAnswerDeadEnds = true)
        {
            if (content == null || !content.IsSingleCorrectDoorBranch)
            {
                return quizNodeId;
            }

            string previousCorrectNodeId = quizNodeId;
            int correctTunnelLength = CorrectTunnelLengthFor(content);
            for (int i = 0; i < correctTunnelLength; i++)
            {
                string correctNodeId = i == 0
                    ? quizNodeId + "_answer_correct"
                    : quizNodeId + "_answer_correct_buffer_" + i;
                graph.AddNode(new PacingGraphNode(correctNodeId, RoomKind.Passage, isMainPath, false, null, 2,
                    i == 0 ? "quiz_correct_tunnel" : "quiz_correct_buffer", BranchPurpose.Filler));
                graph.AddEdge(previousCorrectNodeId, correctNodeId, isMainPath, i == 0 ? "correct" : "correct_buffer");
                previousCorrectNodeId = correctNodeId;
            }

            if (includeWrongAnswerDeadEnds)
            {
                int wrongCount = System.Math.Max(0, content.OptionCount - 1);
                for (int i = 0; i < wrongCount; i++)
                {
                    string wrongNodeId = quizNodeId + "_answer_wrong_" + (i + 1);
                    graph.AddNode(new PacingGraphNode(wrongNodeId, RoomKind.Passage, false, true, null, 1,
                        "quiz_wrong_terminal", BranchPurpose.WrongAnswerDeadEnd));
                    graph.AddEdge(quizNodeId, wrongNodeId, false, "wrong");
                }
            }

            return previousCorrectNodeId;
        }

        private static int CorrectTunnelLengthFor(PlannedInteractiveContent content) => content.OptionCount >= 4 ? FourOptionCorrectTunnelLength : DefaultCorrectTunnelLength;
    }
}
