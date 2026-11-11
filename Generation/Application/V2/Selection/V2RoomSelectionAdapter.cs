using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.V2.Selection
{
    public sealed class V2RoomSelectionAdapter
    {
        private readonly IRoomSelector roomSelector;

        public V2RoomSelectionAdapter(IRoomSelector roomSelector)
        {
            this.roomSelector = roomSelector ?? throw new ArgumentNullException(nameof(roomSelector));
        }

        public IReadOnlyDictionary<string, IRoomTemplateDescriptor> SelectRooms(
            PacingGraph graph,
            IMissionConfigView config,
            ITilesetView tileset,
            IRoomDatabase database,
            IDeterministicRng rng)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            Dictionary<string, IRoomTemplateDescriptor> selections = new(StringComparer.Ordinal);
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                RoomNode node = ToRoomNode(graph.Nodes[i]);
                IRoomTemplateDescriptor selected = roomSelector.Select(node, config, tileset, database, rng);
                if (selected == null)
                {
                    return null;
                }

                selections[node.NodeId] = selected;
            }

            return selections;
        }

        public static RoomNode ToRoomNode(PacingGraphNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            return new RoomNode(
                node.NodeId,
                node.Kind,
                node.RequiredDoorCount,
                null,
                node.IsOnMainPath,
                node.IsTerminal,
                ResolvePreferredRoomTemplateId(node),
                node.ContentId);
        }

        /// <summary>
        /// MazeBranch answer tunnels must use the Toonel door family so their doors match the maze room's
        /// answer sockets — otherwise the connector can't pair them and the layout fails validation with
        /// "answer door not connected". The V1 graph builder pinned these templates explicitly; the V2
        /// expander only tags nodes by <see cref="PacingGraphNode.Source"/>, so we restore the pin here:
        /// correct path continues through a 2-door Toonel, wrong answers terminate in a 1-door blind cap.
        /// </summary>
        private static string ResolvePreferredRoomTemplateId(PacingGraphNode node)
        {
            switch (node.Source)
            {
                case "quiz_wrong_terminal":
                    return "Room_DeadEnd_ToonelBlind1";
                case "quiz_correct_tunnel":
                case "quiz_correct_buffer":
                    return "Toonel";
                default:
                    return null;
            }
        }
    }
}
