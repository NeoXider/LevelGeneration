using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// Scene marker for a place inside an Objective room where the actual objective interactive should appear.
    /// Types are designer-driven strings (Terminal / Display / Table / Artifact / ProjectStand / ExitZone).
    /// </summary>
    public sealed class ObjectiveAnchor : MonoBehaviour
    {
        [SerializeField] [Tooltip("Настройка параметра \"anchorKind\".")]
        private string anchorKind = "Terminal";

        public string AnchorKind => anchorKind;
    }
}
