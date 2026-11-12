using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Application.V2.Domain;
using Neoxider.LevelGeneration.Generation.Domain.Models;

namespace Neoxider.LevelGeneration.Generation.Application.V2.Selection
{
    public sealed class V2RoomSelectionAdapter
    {
        private readonly IRoomSelector roomSelector;
        private readonly RoomTemplateRoles roles;

        public V2RoomSelectionAdapter(IRoomSelector roomSelector, RoomTemplateRoles roles = null)
        {
            this.roomSelector = roomSelector ?? throw new ArgumentNullException(nameof(roomSelector));
            this.roles = roles ?? RoomTemplateRoles.None;
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
                RoomNode node = ToRoomNode(graph.Nodes[i], roles);
                IRoomTemplateDescriptor selected = roomSelector.Select(node, config, tileset, database, rng);
                if (selected == null)
                {
                    return null;
                }

                selections[node.NodeId] = selected;
            }

            return selections;
        }

        public static RoomNode ToRoomNode(PacingGraphNode node, RoomTemplateRoles roles = null)
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
                ResolvePreferredRoomTemplateId(node, roles ?? RoomTemplateRoles.None),
                node.ContentId);
        }

        /// <summary>
        /// Maze branch answer tunnels must share the door family of the maze room's answer sockets — otherwise the
        /// connector can't pair them and the layout fails validation with "answer door not connected". The V1
        /// graph builder pins these templates through <see cref="RoomTemplateRoles"/>; the V2 expander only tags
        /// nodes by <see cref="PacingGraphNode.Source"/>, so the pin is restored here: the correct path continues
        /// through the passage template, a wrong answer terminates in the answer dead end.
        /// </summary>
        private static string ResolvePreferredRoomTemplateId(PacingGraphNode node, RoomTemplateRoles roles)
        {
            switch (node.Source)
            {
                case "quiz_wrong_terminal":
                    return roles.AnswerDeadEndTemplateId;
                case "quiz_correct_tunnel":
                case "quiz_correct_buffer":
                    return roles.PassageTemplateId;
                default:
                    return null;
            }
        }
    }
}
