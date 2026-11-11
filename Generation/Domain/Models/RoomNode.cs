using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;

namespace RedoSchool.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Logical node in the generated <see cref="LevelGraph"/>. One node = one future placed room.
    /// </summary>
    public sealed class RoomNode
    {
        public RoomNode(
            string nodeId,
            RoomKind kind,
            int requiredDoorCount = 0,
            IReadOnlyList<RoomTag> tags = null,
            bool isOnMainPath = true,
            bool isDeadEnd = false,
            string preferredRoomTemplateId = null,
            string contentId = null)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                throw new ArgumentException("Node id must be non-empty.", nameof(nodeId));
            }

            NodeId = nodeId;
            Kind = kind;
            RequiredDoorCount = Math.Max(0, requiredDoorCount);
            Tags = tags ?? Array.Empty<RoomTag>();
            IsOnMainPath = isOnMainPath;
            IsDeadEnd = isDeadEnd;
            PreferredRoomTemplateId = preferredRoomTemplateId;
            ContentId = contentId;
        }

        public string NodeId { get; }
        public RoomKind Kind { get; }
        public int RequiredDoorCount { get; }
        public IReadOnlyList<RoomTag> Tags { get; }
        public bool IsOnMainPath { get; }
        public bool IsDeadEnd { get; }
        public string PreferredRoomTemplateId { get; }
        public string ContentId { get; }

        public RoomNode WithKind(RoomKind kind, string contentId = null)
        {
            return new RoomNode(NodeId, kind, RequiredDoorCount, Tags, IsOnMainPath, IsDeadEnd, PreferredRoomTemplateId,
                contentId ?? ContentId);
        }

        public RoomNode WithRequiredDoorCount(int requiredDoorCount)
        {
            return new RoomNode(NodeId, Kind, requiredDoorCount, Tags, IsOnMainPath, IsDeadEnd, PreferredRoomTemplateId,
                ContentId);
        }
    }
}
