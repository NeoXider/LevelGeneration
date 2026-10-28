using System.Collections.Generic;
using RedoSchool.Features.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace RedoSchool.Features.LevelGeneration.Runtime.Presentation
{
    /// <summary>
    /// Golden stars following the floor-probed shared trail. No ribbon or floor overlay. The view owns only
    /// the particle system: where each star is, how big and how bright is decided by
    /// <see cref="RouteHintStarField"/> from the trail and the settings.
    /// </summary>
    public sealed class RouteHintFloorView : MonoBehaviour
    {
        public const string StarsShaderName = "RedoSchool/LevelGeneration/RouteHintFloorStars";
        public const int HardParticleCap = 512;
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        [SerializeField]
        [Tooltip("World-space Shuriken with an additive four-point star material; this view owns emission.")]
        private ParticleSystem sparkles;

        private readonly List<Vector3> _centerline = new();
        private readonly List<float> _centerlineDistances = new();
        private readonly List<float> _centerlineClearances = new();
        private readonly ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[HardParticleCap];
        private readonly RouteHintStarField _stars = new(HardParticleCap);
        private ParticleSystemRenderer _particleRenderer;
        private Material _material;
        private float _lengthMeters;
        private float _firstRenderTime;
        private bool _clockStarted;
        private bool _hasPath;
        private bool _hasClearances;

        /// <summary>
        /// Звёзды уже погашены и с тех пор ничего не показывалось: повторный <see cref="Hide"/> ничего не делает.
        /// Контроллер карты зовёт <see cref="Hide"/> каждый кадр, пока подсказка скрыта, а остановка системы частиц
        /// обходит всю её иерархию.
        /// </summary>
        private bool _sparklesCleared;

        public bool HasPath => _hasPath;

        /// <summary>Сколько раз звёзды действительно гасились (страж идемпотентного <see cref="Hide"/>).</summary>
        internal int SparkleStopCount { get; private set; }
        // Compatibility with callers using the old floor-view contract; no ribbon is drawn.
        public bool HasRibbon => _hasPath;
        public bool IsRibbonVisible => false;
        public ParticleSystem Sparkles => sparkles;
        public float LengthMeters => _lengthMeters;

        /// <summary>Metres along the path where the visible stream starts (ahead of the student).</summary>
        public float HeadDistance { get; private set; }

        public float RevealProgress { get; private set; }
        public float AnimationTime { get; private set; }

        /// <summary>The floor-probed points the stars are placed along (the shared trail, with heights).</summary>
        internal IReadOnlyList<Vector3> Centerline => _centerline;

        public bool HasMaterial
        {
            get
            {
                EnsureParticleRenderer();
                return _particleRenderer != null && _particleRenderer.sharedMaterial != null;
            }
        }

        internal void SetSparklesForTests(ParticleSystem value)
        {
            sparkles = value;
            _particleRenderer = null;
            _sparklesCleared = false;
        }

        private void Awake()
        {
            DisableLegacyRibbon();
            Hide();
        }

        private void OnDestroy()
        {
            if (_material == null)
            {
                return;
            }
#if UNITY_EDITOR
            if (!UnityEngine.Application.isPlaying)
            {
                DestroyImmediate(_material);
                return;
            }
#endif
            Destroy(_material);
        }

        /// <summary>Uses only the trail's floor-probed centerline, never a mesh; free width limits the star scatter.</summary>
        public bool Show(RouteHintRibbonMeshData path, LevelMinimapSettings settings) =>
            Show(path, null, settings);

        public bool Show(RouteHintRibbonMeshData path, IReadOnlyList<float> clearances, LevelMinimapSettings settings)
        {
            Hide();
            DisableLegacyRibbon();
            if (path == null || settings == null || path.Centerline.Count < 2 || path.LengthMeters <= 0f
                || settings.RouteHintFloorMaxSparkles == 0 || !HasMaterial)
            {
                return false;
            }
            if (_material == null)
            {
                Material shared = _particleRenderer.sharedMaterial;
                _material = new Material(shared) { name = shared.name + " (RouteHint)", hideFlags = HideFlags.DontSave };
                _particleRenderer.sharedMaterial = _material;
            }
            CopyPath(path, clearances);
            _clockStarted = false;
            HeadDistance = 0f;
            RevealProgress = 0f;
            ConfigureSparkles(settings);
            _hasPath = true;
            return true;
        }

        /// <summary>Refreshes a shown route after a re-plan without restarting reveal, drift or the scaled clock.</summary>
        public bool UpdatePath(RouteHintRibbonMeshData path, IReadOnlyList<float> clearances)
        {
            if (!_hasPath || path == null || path.Centerline.Count < 2 || path.LengthMeters <= 0f
                || path.CenterlineDistances.Count != path.Centerline.Count)
            {
                return false;
            }
            CopyPath(path, clearances);
            HeadDistance = Mathf.Clamp(HeadDistance, 0f, _lengthMeters);
            return true;
        }

        /// <summary>Moves the start of the visible stream along the path; stars before it are not drawn.</summary>
        public void SetHead(float distanceMeters)
        {
            HeadDistance = Mathf.Clamp(distanceMeters, 0f, _lengthMeters);
        }

        /// <summary>
        /// Allocation-free sampling: the stream is a lattice of stars anchored to distance along the path,
        /// drifting toward the target; the part before the head is not drawn and the rest fades in at the head
        /// and out at the target. Reveal masks the stream without changing its drift.
        /// </summary>
        public void Render(float reveal, float alpha, float time, LevelMinimapSettings settings)
        {
            if (!_hasPath || settings == null || sparkles == null || _material == null)
            {
                return;
            }
            if (!_clockStarted)
            {
                _firstRenderTime = time;
                _clockStarted = true;
            }
            AnimationTime = time;
            float clampedReveal = Mathf.Clamp01(reveal);
            RevealProgress = 1f - (1f - clampedReveal) * (1f - clampedReveal);
            if (alpha <= 0.001f || settings.RouteHintFloorMaxSparkles == 0)
            {
                StopSparkles();
                return;
            }
            _sparklesCleared = false;
            if (!sparkles.isPlaying)
            {
                sparkles.Play(false);
            }
            _particleRenderer.enabled = true;
            _material.SetColor(TintId, settings.RouteHintFloorSparkleColor);
            RouteHintStarStyle style = settings.CreateRouteHintStarStyle();
            float elapsed = Mathf.Max(0f, time - _firstRenderTime);
            int count = _stars.Sample(_centerline, _centerlineDistances, _hasClearances ? _centerlineClearances : null,
                _lengthMeters, HeadDistance, RevealProgress, alpha, elapsed, style);
            float speed = settings.RouteHintFloorSparkleSpeedMetersPerSecond;
            for (int i = 0; i < count; i++)
            {
                RouteHintStar star = _stars.Stars[i];
                _particles[i] = new ParticleSystem.Particle
                {
                    position = star.Position,
                    velocity = star.Direction * speed,
                    startColor = new Color(1f, 1f, 1f, star.Brightness),
                    startSize = star.Size,
                    startLifetime = star.LifetimeSeconds,
                    remainingLifetime = star.LifetimeSeconds,
                    randomSeed = star.Seed
                };
            }
            sparkles.SetParticles(_particles, count);
        }

        /// <summary>
        /// Clears frozen stars too when the unscaled UI fade completes during pause. Idempotent: hiding a view that
        /// is already hidden and cleared does not touch the particle system again.
        /// </summary>
        public void Hide()
        {
            if (!_hasPath && _sparklesCleared)
            {
                return;
            }

            _hasPath = false;
            HeadDistance = 0f;
            RevealProgress = 0f;
            StopSparkles();
        }

        private void CopyPath(RouteHintRibbonMeshData path, IReadOnlyList<float> clearances)
        {
            _centerline.Clear();
            _centerline.AddRange(path.Centerline);
            _centerlineDistances.Clear();
            _centerlineDistances.AddRange(path.CenterlineDistances);
            _lengthMeters = path.LengthMeters;
            _centerlineClearances.Clear();
            _hasClearances = clearances != null && clearances.Count > 0;
            if (!_hasClearances)
            {
                return;
            }
            // The centerline may repeat a point at a hairpin turn; repeated points share their clearance.
            int source = 0;
            for (int i = 0; i < _centerline.Count; i++)
            {
                if (i > 0 && _centerlineDistances[i] > _centerlineDistances[i - 1] + 0.00001f)
                {
                    source++;
                }
                _centerlineClearances.Add(clearances[Mathf.Min(source, clearances.Count - 1)]);
            }
        }

        private void StopSparkles()
        {
            if (sparkles != null)
            {
                sparkles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            if (_particleRenderer != null)
            {
                _particleRenderer.enabled = false;
            }

            _sparklesCleared = true;
            SparkleStopCount++;
        }

        private void EnsureParticleRenderer()
        {
            if (_particleRenderer == null && sparkles != null)
            {
                _particleRenderer = sparkles.GetComponent<ParticleSystemRenderer>();
            }
        }

        private void DisableLegacyRibbon()
        {
            if (TryGetComponent(out MeshRenderer legacy))
            {
                legacy.enabled = false;
            }
            if (TryGetComponent(out MeshFilter filter))
            {
                filter.sharedMesh = null;
            }
        }

        private void ConfigureSparkles(LevelMinimapSettings settings)
        {
            EnsureParticleRenderer();
            ParticleSystem[] systems = sparkles.GetComponentsInChildren<ParticleSystem>(true);
            foreach (ParticleSystem system in systems)
            {
                ParticleSystem.MainModule main = system.main;
                main.useUnscaledTime = false;
                main.simulationSpeed = 0f;
                main.playOnAwake = false;
            }
            ParticleSystem.MainModule root = sparkles.main;
            root.simulationSpace = ParticleSystemSimulationSpace.World;
            root.maxParticles = settings.RouteHintFloorMaxSparkles;
            root.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            ParticleSystem.EmissionModule emission = sparkles.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = sparkles.shape;
            shape.enabled = false;
            ParticleSystem.ColorOverLifetimeModule color = sparkles.colorOverLifetime;
            color.enabled = false;
            ParticleSystem.SizeOverLifetimeModule size = sparkles.sizeOverLifetime;
            size.enabled = false;
            ParticleSystem.VelocityOverLifetimeModule velocity = sparkles.velocityOverLifetime;
            velocity.enabled = false;
            ParticleSystem.NoiseModule noise = sparkles.noise;
            noise.enabled = false;
            ParticleSystem.TrailModule trails = sparkles.trails;
            trails.enabled = false;
            _particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        }
    }
}
