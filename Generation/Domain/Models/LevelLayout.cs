using System;
using System.Collections.Generic;

namespace Neoxider.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Final logical layout of one mission: every <see cref="PlacedRoom"/> with its pose and door states,
    /// plus the wiring between rooms. Produced by the generator, consumed by the spawner.
    /// </summary>
    public sealed class LevelLayout
    {
        public LevelLayout(
            IReadOnlyList<PlacedRoom> rooms,
            IReadOnlyList<DoorConnection> connections,
            string startNodeId,
            IReadOnlyList<string> exitNodeIds)
        {
            Rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
            Connections = connections ?? Array.Empty<DoorConnection>();
            StartNodeId = startNodeId ?? throw new ArgumentNullException(nameof(startNodeId));
            ExitNodeIds = exitNodeIds ?? Array.Empty<string>();
        }

        public IReadOnlyList<PlacedRoom> Rooms { get; }
        public IReadOnlyList<DoorConnection> Connections { get; }
        public string StartNodeId { get; }
        public IReadOnlyList<string> ExitNodeIds { get; }
    }

    /// <summary>
    /// Physical wiring of two door sockets across two placed rooms.
    /// </summary>
    public readonly struct DoorConnection
    {
        public readonly string FromNodeId;
        public readonly string FromSocketId;
        public readonly string ToNodeId;
        public readonly string ToSocketId;
        public readonly bool IsMainPath;

        public DoorConnection(
            string fromNodeId,
            string fromSocketId,
            string toNodeId,
            string toSocketId,
            bool isMainPath = false)
        {
            FromNodeId = fromNodeId;
            FromSocketId = fromSocketId;
            ToNodeId = toNodeId;
            ToSocketId = toSocketId;
            IsMainPath = isMainPath;
        }
    }
}
