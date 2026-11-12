using System;
using System.Collections.Generic;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Runtime.Application;
using Neoxider.LevelGeneration.Runtime.Domain.Events;
using Neoxider.LevelGeneration.Runtime.Presentation;
using UnityEngine;

namespace Neoxider.LevelGeneration.Runtime.Infrastructure
{
    /// <summary>
    /// Keeps only the current room and nearby connected rooms active.
    /// Minimap discovery remains separate and is driven by the same RoomEnteredEvent stream.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AdaptiveLevelLoadingController : MonoBehaviour
    {
        [SerializeField] [Tooltip("Настройка параметра \"adaptiveLoading\".")]
        private bool adaptiveLoading;

        [SerializeField] [Tooltip("Keep rooms active after the player has visited them, so backtracking never reveals unloaded space.")]
        private bool keepVisitedRoomsActive = true;

        [SerializeField] [Tooltip("Настройка параметра \"neighborDepth\".")] [Min(0)]
        private int neighborDepth = 1;

        private readonly Dictionary<string, List<string>> _adjacency = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activeRoomIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visitedRoomIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _queue = new();
        private readonly Dictionary<string, int> _depthByNodeId = new(StringComparer.Ordinal);

        private IReadOnlyDictionary<string, RoomInstance> _rooms;
        private IDisposable _subscription;
        private string _currentNodeId;

        public void Bind(LevelLayout layout, IReadOnlyDictionary<string, RoomInstance> rooms, IRoomEventBus bus)
        {
            DisposeSubscription();
            // Посещённые комнаты — состояние ОДНОЙ миссии. Без сброса новая миссия с теми же идентификаторами
            // узлов держала бы включёнными комнаты, в которых игрок был в прошлой.
            _visitedRoomIds.Clear();
            _rooms = rooms;
            _currentNodeId = layout != null ? layout.StartNodeId : null;
            BuildAdjacency(layout);

            if (!CanUseAdaptiveLoading(layout, rooms, bus))
            {
                SetAllRoomsActive(true);
                return;
            }

            _subscription = bus.Subscribe<RoomEnteredEvent>(OnRoomEntered);
            ApplyActiveSet();
        }

        public void SetAdaptiveLoadingEnabled(bool enabled)
        {
            adaptiveLoading = enabled;
            if (!adaptiveLoading)
            {
                SetAllRoomsActive(true);
                return;
            }

            ApplyActiveSet();
        }

        /// <summary>
        /// Restores the active window around a checkpoint room before the player is teleported there.
        /// This is deliberately separate from <see cref="RoomEnteredEvent"/>: on resume the target
        /// room can still be inactive, so its trigger cannot publish that event first.
        /// </summary>
        public bool RestoreCurrentRoom(string nodeId)
        {
            if (_rooms == null || string.IsNullOrWhiteSpace(nodeId) || !_rooms.ContainsKey(nodeId))
            {
                return false;
            }

            _currentNodeId = nodeId;
            if (keepVisitedRoomsActive)
            {
                _visitedRoomIds.Add(nodeId);
            }

            ApplyActiveSet();
            return _rooms.TryGetValue(nodeId, out RoomInstance room)
                   && room != null
                   && room.gameObject.activeInHierarchy;
        }

        private void OnDestroy() => DisposeSubscription();

        private void OnRoomEntered(RoomEnteredEvent evt)
        {
            if (_rooms == null || !_rooms.ContainsKey(evt.NodeId))
            {
                return;
            }

            _currentNodeId = evt.NodeId;
            if (keepVisitedRoomsActive)
            {
                _visitedRoomIds.Add(evt.NodeId);
            }

            ApplyActiveSet();
        }

        private bool CanUseAdaptiveLoading(
            LevelLayout layout,
            IReadOnlyDictionary<string, RoomInstance> rooms,
            IRoomEventBus bus)
        {
            return adaptiveLoading
                   && layout != null
                   && rooms != null
                   && bus != null
                   && layout.Connections.Count > 0
                   && !string.IsNullOrWhiteSpace(layout.StartNodeId)
                   && rooms.ContainsKey(layout.StartNodeId);
        }

        private void ApplyActiveSet()
        {
            if (_rooms == null || string.IsNullOrWhiteSpace(_currentNodeId) || !_rooms.ContainsKey(_currentNodeId))
            {
                SetAllRoomsActive(true);
                return;
            }

            _activeRoomIds.Clear();
            _depthByNodeId.Clear();
            _queue.Clear();

            if (keepVisitedRoomsActive)
            {
                foreach (string visitedRoomId in _visitedRoomIds)
                {
                    if (_rooms.ContainsKey(visitedRoomId))
                    {
                        _activeRoomIds.Add(visitedRoomId);
                    }
                }
            }

            _activeRoomIds.Add(_currentNodeId);
            _depthByNodeId[_currentNodeId] = 0;
            _queue.Enqueue(_currentNodeId);

            int maxDepth = Mathf.Max(0, neighborDepth);
            while (_queue.Count > 0)
            {
                string nodeId = _queue.Dequeue();
                int depth = _depthByNodeId[nodeId];
                if (depth >= maxDepth)
                {
                    continue;
                }

                if (!_adjacency.TryGetValue(nodeId, out List<string> neighbors))
                {
                    continue;
                }

                for (int i = 0; i < neighbors.Count; i++)
                {
                    string neighborId = neighbors[i];
                    if (!_rooms.ContainsKey(neighborId) || _activeRoomIds.Contains(neighborId))
                    {
                        continue;
                    }

                    _activeRoomIds.Add(neighborId);
                    _depthByNodeId[neighborId] = depth + 1;
                    _queue.Enqueue(neighborId);
                }
            }

            foreach (KeyValuePair<string, RoomInstance> room in _rooms)
            {
                if (room.Value == null)
                {
                    continue;
                }

                room.Value.gameObject.SetActive(_activeRoomIds.Contains(room.Key));
            }
        }

        private void SetAllRoomsActive(bool active)
        {
            if (_rooms == null)
            {
                return;
            }

            foreach (KeyValuePair<string, RoomInstance> room in _rooms)
            {
                if (room.Value == null)
                {
                    continue;
                }

                room.Value.gameObject.SetActive(active);
            }
        }

        private void BuildAdjacency(LevelLayout layout)
        {
            _adjacency.Clear();
            if (layout == null)
            {
                return;
            }

            for (int i = 0; i < layout.Connections.Count; i++)
            {
                DoorConnection connection = layout.Connections[i];
                AddEdge(connection.FromNodeId, connection.ToNodeId);
                AddEdge(connection.ToNodeId, connection.FromNodeId);
            }
        }

        private void AddEdge(string fromNodeId, string toNodeId)
        {
            if (string.IsNullOrWhiteSpace(fromNodeId) || string.IsNullOrWhiteSpace(toNodeId))
            {
                return;
            }

            if (!_adjacency.TryGetValue(fromNodeId, out List<string> neighbors))
            {
                neighbors = new List<string>(2);
                _adjacency[fromNodeId] = neighbors;
            }

            if (!neighbors.Contains(toNodeId))
            {
                neighbors.Add(toNodeId);
            }
        }

        private void DisposeSubscription()
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}
