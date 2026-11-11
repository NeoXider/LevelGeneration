using UnityEngine;

namespace RedoSchool.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>Scene marker for waypoint navigation (NPCs / hints / camera). Inert in MVP.</summary>
    public sealed class WaypointNode : MonoBehaviour
    {
        [SerializeField] [Tooltip("Настройка параметра \"waypointId\".")]
        private string waypointId;

        public string WaypointId => waypointId;
    }
}
