using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime
{
    /// <summary>
    /// Scene marker for one door socket on a room prefab. The room placer / connector references this by
    /// <see cref="socketId"/>; runtime door visuals query <see cref="State"/>.
    /// </summary>
    public sealed class DoorSocket : MonoBehaviour
    {
        [SerializeField] [Tooltip("Stable socket id used by mission generation and runtime door logic.")]
        private string socketId = "door";

        [SerializeField] [Tooltip("Door size used when matching sockets.")]
        private DoorSize size = DoorSize.Medium;

        [SerializeField] [HideInInspector] private DoorDirection direction = DoorDirection.East;

        [SerializeField] [Tooltip("Door distance from the room pivot in snap-grid steps.")]
        private int gridOffsetSteps = 1;

        [SerializeField] [Tooltip("Door local X position in snap-grid steps.")]
        private int localXSteps = 2;

        [SerializeField] [Tooltip("Door local Z position in snap-grid steps.")]
        private int localZSteps = 1;

        [SerializeField] [Tooltip("Room grid origin offset on local X in meters.")] [HideInInspector]
        private float gridOriginXMeters;

        [SerializeField] [Tooltip("Room grid origin offset on local Z in meters.")] [HideInInspector]
        private float gridOriginZMeters;

        [SerializeField] [Tooltip("Door outward yaw in local room space.")]
        private float localYawDegrees = 0f;

        [SerializeField] [Tooltip("Exact socket-to-socket snap point. If empty, this DoorSocket transform is used.")]
        private Transform snapAnchor;

        [SerializeField] [HideInInspector] private bool hasCachedAuthoringCenter;
        [SerializeField] [HideInInspector] private Vector3 cachedAuthoringLocalCenter;

        [SerializeField] [Tooltip("How generation may use this socket: both directions, only as room entrance, or only as room exit.")]
        private DoorSocketFlowDirection flowDirection = DoorSocketFlowDirection.Both;

        [SerializeField] [Tooltip("This socket can be used by the required main mission path.")]
        private bool canBeMainPath = true;

        [SerializeField] [Tooltip("This socket can be used by optional side paths.")]
        private bool canBeSidePath = true;

        [SerializeField] [Tooltip("Room logic may lock this socket.")]
        private bool canBeLocked = true;

        [SerializeField] [Tooltip("Initial door state. Use Closed for normal doors, Disabled for sockets that must not connect.")]
        private DoorState initialState = DoorState.Closed;

        [SerializeField] [Tooltip("If enabled, trigger volumes can open and close this door visual.")]
        private bool autoOpen = true;

        [SerializeField] [Tooltip("Visual enabled when the door blocks passage. Open doors hide it; Closed/Locked/Disabled show it.")]
        private GameObject closedVisual;

        [Header("Animator")] [SerializeField] [Tooltip("Animator controlling the door, if this door has animated visuals.")]
        private Animator doorAnimator;

        [SerializeField] [Tooltip("Animator bool set to true while the door is open.")]
        private string animatorOpenBool = "IsOpen";

        [SerializeField] [Tooltip("Animator trigger fired when the door opens.")]
        private string animatorOpenTrigger;

        [SerializeField] [Tooltip("Animator trigger fired when the door closes.")]
        private string animatorCloseTrigger;

        private DoorState _state;
        private bool _visualOpen;
        private bool _runtimeVisualHidden;
        private Animator _resolvedDoorAnimator;
        private bool _doorAnimatorResolved;

        public string SocketId => socketId;
        public DoorSize Size => size;
        public DoorDirection Direction => direction;
        public int GridOffsetSteps => gridOffsetSteps;
        public int LocalXSteps => localXSteps;
        public int LocalZSteps => localZSteps;
        public float GridOriginXMeters => gridOriginXMeters;
        public float GridOriginZMeters => gridOriginZMeters;
        public float LocalXMeters => gridOriginXMeters + localXSteps * LevelGenerationGridMetrics.DoorSnapMeters;
        public float LocalZMeters => gridOriginZMeters + localZSteps * LevelGenerationGridMetrics.DoorSnapMeters;
        public float LocalYawDegrees => localYawDegrees;
        public Transform SnapAnchor => snapAnchor;
        public DoorSocketFlowDirection FlowDirection => flowDirection;
        public bool CanBeMainPath => canBeMainPath;
        public bool CanBeSidePath => canBeSidePath;
        public bool CanBeLocked => canBeLocked;
        public DoorState InitialState => initialState;
        public DoorState State => _state;
        public bool AutoOpen => autoOpen;
        public bool IsAvailableForAutoOpen => autoOpen && IsDoorUsableState(_state);
        public Transform Anchor => transform;

        /// <summary>
        /// CONTRACT: door docking is intentionally aligned by the <b>visible door center</b>, never by the
        /// socket/room pivot. Rooms snap door-center to door-center (so a door mates with another at any yaw,
        /// including 0/180), and the room pivot/Y is free to differ. Resolution priority:
        /// explicit <see cref="snapAnchor"/> -> visible door geometry bounds -> socket transform (last resort).
        /// Do not reorder this to prefer the raw transform: authoring pivots are routinely offset from the
        /// opening, which makes opposite corridor doors resolve a full grid cell apart and breaks docking.
        /// </summary>
        public Vector3 ResolveAuthoringWorldCenter()
        {
            if (snapAnchor != null)
            {
                return snapAnchor.position;
            }

            if (hasCachedAuthoringCenter && transform != null)
            {
                return transform.TransformPoint(cachedAuthoringLocalCenter);
            }

            return ResolveLiveAuthoringWorldCenter();
        }

        private Vector3 ResolveLiveAuthoringWorldCenter()
        {
            if (TryResolveLiveAuthoringWorldCenter(out Vector3 center))
            {
                return center;
            }

            return transform != null ? transform.position : Vector3.zero;
        }

        private bool TryResolveLiveAuthoringWorldCenter(out Vector3 center)
        {
            if (TryCollectRendererBounds(out Bounds bounds) || TryCollectColliderBounds(out bounds))
            {
                center = bounds.center;
                return true;
            }

            center = default;
            return false;
        }

        public Vector3 ResolveAuthoringWorldOutward(Transform roomRoot) => YawToWorld(localYawDegrees, roomRoot);

        public float ResolveAuthoringLocalYawDegrees(Transform roomRoot) => NormalizeYaw(localYawDegrees);

        private void Awake() => ApplyState(initialState, true, false);

        public void ApplyGeneratedState(DoorState newState) => ApplyState(newState, true, false);

        /// <summary>
        /// Level logic changes the door during play (lock released, room solved). A visible open or close
        /// that follows raises <see cref="VisualOpenChanged"/>.
        /// </summary>
        public void ApplyState(DoorState newState) => ApplyState(newState, false, true);

        /// <summary>
        /// Same as <see cref="ApplyState(DoorState)"/> for checkpoint restore: the door takes its saved state
        /// silently. A restore is not a game event; announcing it would "open" every passed door of the level
        /// at once on resume.
        /// </summary>
        public void RestoreState(DoorState newState) => ApplyState(newState, false, false);

        private void ApplyState(DoorState newState, bool startClosedForAutoOpen, bool announceMotion)
        {
            if (newState != DoorState.Disabled && !gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }

            DoorState previousState = _state;
            _state = newState;
            SetAutoOpenTriggersEnabled(newState != DoorState.Disabled);
            bool startsClosedForAuto =
                startClosedForAutoOpen && autoOpen && IsDoorUsableState(newState);
            SetVisualOpen(
                IsDoorUsableState(newState) && !startsClosedForAuto && newState != DoorState.Closed,
                announceMotion);

            // Disabled means "not connectable / not interactive", not "delete the blocker visual".
            // Runtime duplicate seam doors are hidden explicitly through SetRuntimeVisualHidden.

            // A runtime unlock (e.g. password terminal: Locked -> Open) can happen while the player already
            // stands inside the auto-open trigger. No OnTriggerEnter fires in that case, so re-evaluate the
            // trigger here to open the door without forcing the player to step out and back in.
            if (!startClosedForAutoOpen
                && !IsDoorUsableState(previousState)
                && autoOpen
                && IsAvailableForAutoOpen)
            {
                RefreshActiveAutoOpenTriggers();
            }
        }

        private void RefreshActiveAutoOpenTriggers()
        {
            DoorAutoOpenTrigger[] triggers = GetComponentsInChildren<DoorAutoOpenTrigger>(true);
            for (int i = 0; i < triggers.Length; i++)
            {
                DoorAutoOpenTrigger trigger = triggers[i];
                if (trigger != null && trigger.enabled)
                {
                    trigger.RefreshTriggeredOpen();
                }
            }
        }

        /// <summary>
        /// Дверь открылась перед игроком: сработал её auto-open триггер, и состояние позволило открыть.
        /// Поднимается на каждое такое открытие, а не только на переход «закрыта → открыта»: дверь,
        /// которую логика комнаты уже распахнула издалека, всё равно сообщает о подошедшем игроке.
        /// Смена состояния логикой (<see cref="ApplyState(DoorState)"/>) и восстановление чекпоинта
        /// (<see cref="RestoreState"/>) событие НЕ поднимают — иначе одно восстановление «открыло» бы разом
        /// все двери уровня.
        /// </summary>
        public event System.Action<DoorSocket> OpenedForPlayer;

        /// <summary>
        /// The door visibly opened (<c>true</c>) or closed (<c>false</c>) during play: the player walked into
        /// or out of its auto-open trigger, or level logic changed its state (<see cref="ApplyState(DoorState)"/>).
        /// Raised only on a real flip of the visual; re-applying the same position stays silent. Generation
        /// (<see cref="ApplyGeneratedState"/>, <c>Awake</c>) and checkpoint restore (<see cref="RestoreState"/>)
        /// never raise it.
        /// </summary>
        public event System.Action<DoorSocket, bool> VisualOpenChanged;

        /// <summary>The door is visibly open right now (the passage is clear).</summary>
        public bool IsVisuallyOpen => _visualOpen;

        public bool TrySetTriggeredOpen(bool isOpen)
        {
            if (!IsAvailableForAutoOpen)
            {
                SetVisualOpen(false, true);
                return false;
            }

            SetVisualOpen(isOpen, true);
            if (isOpen)
            {
                OpenedForPlayer?.Invoke(this);
            }

            return true;
        }

        public void SetRuntimeVisualHidden(bool hidden)
        {
            _runtimeVisualHidden = hidden;
            SetVisualOpen(_visualOpen, false);
        }

        private void SetVisualOpen(bool isOpen, bool announceMotion)
        {
            bool wasOpen = _visualOpen;
            _visualOpen = isOpen && IsDoorUsableState(_state);
            ApplyClosedVisualFallback();
            ApplyAnimator(_visualOpen);
            if (announceMotion && wasOpen != _visualOpen)
            {
                VisualOpenChanged?.Invoke(this, _visualOpen);
            }
        }

        private void ApplyClosedVisualFallback()
        {
            if (HasAnimatorVisual())
            {
                return;
            }

            if (closedVisual == null)
            {
                return;
            }

            bool shouldShowClosedVisual =
                !_runtimeVisualHidden
                && !_visualOpen
                && (IsDoorUsableState(_state) || _state == DoorState.Locked || _state == DoorState.Disabled);

            closedVisual.SetActive(shouldShowClosedVisual);
        }

        private void ApplyAnimator(bool isOpen)
        {
            Animator animator = ResolveDoorAnimator();
            if (!CanUseAnimatorVisual(animator))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(animatorOpenBool) &&
                HasAnimatorParameter(animator, animatorOpenBool, AnimatorControllerParameterType.Bool))
            {
                animator.SetBool(animatorOpenBool, isOpen);
            }
            else if (TryResolveFallbackBoolParameter(animator, out string fallbackBool))
            {
                animator.SetBool(fallbackBool, isOpen);
            }

            string trigger = isOpen ? animatorOpenTrigger : animatorCloseTrigger;
            if (!string.IsNullOrWhiteSpace(trigger) &&
                HasAnimatorParameter(animator, trigger, AnimatorControllerParameterType.Trigger))
            {
                animator.SetTrigger(trigger);
            }
        }

        private bool HasAnimatorVisual()
        {
            Animator animator = ResolveDoorAnimator();
            return CanUseAnimatorVisual(animator);
        }

        private Animator ResolveDoorAnimator()
        {
            if (_doorAnimatorResolved)
            {
                return _resolvedDoorAnimator;
            }

            _resolvedDoorAnimator = doorAnimator != null
                ? doorAnimator
                : GetComponentInChildren<Animator>(true);
            _doorAnimatorResolved = true;
            return _resolvedDoorAnimator;
        }

        private static bool HasAnimatorParameter(
            Animator animator,
            string parameterName,
            AnimatorControllerParameterType type)
        {
            if (!CanUseAnimatorVisual(animator))
            {
                return false;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter.type == type && parameter.name == parameterName)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveFallbackBoolParameter(Animator animator, out string parameterName)
        {
            parameterName = null;
            if (!CanUseAnimatorVisual(animator))
            {
                return false;
            }

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter.type != AnimatorControllerParameterType.Bool)
                {
                    continue;
                }

                if (parameterName != null)
                {
                    parameterName = null;
                    return false;
                }

                parameterName = parameter.name;
            }

            return parameterName != null;
        }

        private static bool CanUseAnimatorVisual(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return false;
            }

            if (!animator.isActiveAndEnabled || !animator.gameObject.activeInHierarchy)
            {
                return false;
            }

            return !UnityEngine.Application.isPlaying || animator.isInitialized;
        }

        private bool HasAutoOpenTrigger() => GetComponentInChildren<DoorAutoOpenTrigger>(true) != null;

        private void SetAutoOpenTriggersEnabled(bool enabled)
        {
            DoorAutoOpenTrigger[] triggers = GetComponentsInChildren<DoorAutoOpenTrigger>(true);
            for (int i = 0; i < triggers.Length; i++)
            {
                DoorAutoOpenTrigger trigger = triggers[i];
                if (trigger == null)
                {
                    continue;
                }

                trigger.enabled = enabled;
                Collider[] colliders = trigger.GetComponents<Collider>();
                for (int j = 0; j < colliders.Length; j++)
                {
                    if (colliders[j] != null)
                    {
                        colliders[j].enabled = enabled;
                    }
                }
            }
        }

        private static bool IsDoorUsableState(DoorState state) => state == DoorState.Open || state == DoorState.Closed || state == DoorState.ExitGate;

#if UNITY_EDITOR
        private void OnValidate() => RefreshAuthoringCenterCache();

        public void RefreshAuthoringCenterCache()
        {
            if (transform == null || snapAnchor != null)
            {
                hasCachedAuthoringCenter = false;
                cachedAuthoringLocalCenter = Vector3.zero;
                return;
            }

            if (!TryResolveLiveAuthoringWorldCenter(out Vector3 worldCenter))
            {
                hasCachedAuthoringCenter = false;
                cachedAuthoringLocalCenter = Vector3.zero;
                return;
            }

            cachedAuthoringLocalCenter = transform.InverseTransformPoint(worldCenter);
            hasCachedAuthoringCenter = true;
        }
#endif

        private bool TryCollectRendererBounds(out Bounds bounds)
        {
            bounds = default;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !IsAuthoringCenterContributor(renderer.transform))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        private bool TryCollectColliderBounds(out Bounds bounds)
        {
            bounds = default;
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            bool hasBounds = false;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger || !IsAuthoringCenterContributor(collider.transform))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return hasBounds;
        }

        private bool IsAuthoringCenterContributor(Transform candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            if (candidate.GetComponentInParent<DoorAutoOpenTrigger>(true) != null)
            {
                return false;
            }

            Transform current = candidate;
            while (current != null && current != transform)
            {
                if (current == snapAnchor)
                {
                    return true;
                }

                string objectName = current.name;
                if (ContainsIgnoreCase(objectName, "trigger")
                    || ContainsIgnoreCase(objectName, "interaction"))
                {
                    return false;
                }

                current = current.parent;
            }

            return true;
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                   && value.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Vector3 YawToWorld(float yawDegrees, Transform roomRoot)
        {
            Vector3 local = Quaternion.Euler(0f, yawDegrees, 0f) * Vector3.right;
            return roomRoot != null ? roomRoot.TransformDirection(local) : local;
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Vector3 center = ResolveAuthoringWorldCenter();

            Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.95f);
            Gizmos.DrawSphere(center, 0.1f);
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 center = ResolveAuthoringWorldCenter();

            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(center, 0.16f);
        }

#endif
    }
}
