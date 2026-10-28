using System;
using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Features.LevelGeneration.Generation.Application.V2.Placement
{
    public sealed class PlacementCandidateMemory
    {
        private readonly HashSet<PlacementCandidateKey> rejected = new();

        public bool WasRejected(PlacementCandidateKey key) => rejected.Contains(key);

        public void RememberRejected(PlacementCandidateKey key) => rejected.Add(key);

        public void Clear() => rejected.Clear();
    }

    public readonly struct PlacementCandidateKey : IEquatable<PlacementCandidateKey>
    {
        public PlacementCandidateKey(
            string nodeId,
            string roomTemplateId,
            string parentNodeId,
            string parentSocketId,
            string candidateSocketId,
            int yawDegrees,
            GridPose snappedCell)
        {
            NodeId = nodeId ?? string.Empty;
            RoomTemplateId = roomTemplateId ?? string.Empty;
            ParentNodeId = parentNodeId ?? string.Empty;
            ParentSocketId = parentSocketId ?? string.Empty;
            CandidateSocketId = candidateSocketId ?? string.Empty;
            YawDegrees = yawDegrees;
            SnappedCell = snappedCell;
        }

        public string NodeId { get; }
        public string RoomTemplateId { get; }
        public string ParentNodeId { get; }
        public string ParentSocketId { get; }
        public string CandidateSocketId { get; }
        public int YawDegrees { get; }
        public GridPose SnappedCell { get; }

        public bool Equals(PlacementCandidateKey other)
        {
            return NodeId == other.NodeId &&
                   RoomTemplateId == other.RoomTemplateId &&
                   ParentNodeId == other.ParentNodeId &&
                   ParentSocketId == other.ParentSocketId &&
                   CandidateSocketId == other.CandidateSocketId &&
                   YawDegrees == other.YawDegrees &&
                   SnappedCell.Equals(other.SnappedCell);
        }

        public override bool Equals(object obj) => obj is PlacementCandidateKey other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(NodeId, RoomTemplateId, ParentNodeId, ParentSocketId, CandidateSocketId, YawDegrees,
                SnappedCell);
        }
    }
}
