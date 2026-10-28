using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Runtime.Application;
using RedoSchool.Features.LevelGeneration.Runtime.Infrastructure;
using RedoSchool.Features.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    [Category("LevelGeneration")]
    public sealed class RouteHintFloorViewTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created.ToArray())
            {
                if (created is GameObject owner)
                {
                    ParticleSystemRenderer[] renderers = owner.GetComponentsInChildren<ParticleSystemRenderer>(true);
                    foreach (ParticleSystemRenderer renderer in renderers)
                    {
                        Material material = renderer.sharedMaterial;
                        if (material != null && (material.hideFlags & HideFlags.DontSave) != 0
                            && !_created.Contains(material))
                        {
                            _created.Add(material);
                        }
                    }
                }
            }

            for (int i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null)
                {
                    Object.DestroyImmediate(_created[i]);
                }
            }
            _created.Clear();
        }

        [Test]
        public void Show_CenterlineWithoutRibbonGeometry_AcceptsPathWithoutUploadingMesh()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            RouteHintRibbonMeshData path = BuildRibbon(settings);
            path.Vertices.Clear();
            path.Triangles.Clear();

            bool shown = view.Show(path, settings);

            Assert.IsTrue(shown, "Stars need the floor centerline, not strip triangles.");
            Assert.IsTrue(view.HasPath);
            Assert.IsTrue(view.HasRibbon, "The compatibility alias represents the same active path.");
            Assert.AreEqual(path.LengthMeters, view.LengthMeters, 0.001f);
            Assert.IsFalse(view.IsRibbonVisible, "The golden trail never draws a solid ribbon.");
            Assert.IsNull(view.GetComponent<MeshFilter>().sharedMesh, "No ribbon mesh may be uploaded.");
            Assert.IsFalse(view.GetComponent<MeshRenderer>().enabled);
        }

        [Test]
        public void Show_WithoutParticleMaterial_RefusesEvenWithRibbonMaterial()
        {
            RouteHintFloorView view = CreateView(false);
            LevelMinimapSettings settings = CreateSettings();
            Material unrelated = CreateMaterial();
            view.GetComponent<MeshRenderer>().sharedMaterial = unrelated;

            bool shown = view.Show(BuildRibbon(settings), settings);

            Assert.IsFalse(view.HasMaterial, "A root mesh material cannot substitute for the star material.");
            Assert.IsFalse(shown);
            Assert.IsFalse(view.HasPath);
            Assert.IsFalse(view.IsRibbonVisible);
        }

        [Test]
        public void Render_RevealedPath_DrawsStarsWithoutRibbon()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            view.Show(BuildRibbon(settings), settings);

            view.Render(1f, 1f, 0f, settings);

            Assert.Greater(view.Sparkles.particleCount, 0, "The floor path is visible as a star chain.");
            Assert.IsFalse(view.IsRibbonVisible);
            Assert.IsFalse(view.GetComponent<MeshRenderer>().enabled);
        }

        [Test]
        public void Render_PartialReveal_KeepsTheTargetHalfUnlit()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings(withoutJitter: true);
            RouteHintRibbonMeshData path = BuildStraight(settings, 8f);
            view.Show(path, settings);

            view.Render(0.25f, 1f, 0f, settings);

            ParticleSystem.Particle[] stars = Snapshot(view.Sparkles);
            Assert.Greater(stars.Length, 0, "Reveal starts lighting the path near the player.");
            foreach (ParticleSystem.Particle star in stars)
            {
                Assert.Less(star.position.x, path.LengthMeters * 0.5f,
                    "An early reveal must leave the target half of the route unlit.");
            }
        }

        [Test]
        public void Render_DefaultStream_IsDenseWithLateralVerticalAndSkewedSizeVariation()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            SerializedObject serialized = new(settings);
            serialized.FindProperty("routeHintFloorSparkleTwinkleSize").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            RouteHintRibbonMeshData path = BuildStraight(settings, 8f);
            Assert.Less(path.LengthMeters / settings.RouteHintFloorSparkleSpacingMeters,
                settings.RouteHintFloorMaxSparkles, "This fixture must exercise density before the budget engages.");
            view.Show(path, settings);

            view.Render(1f, 1f, 0f, settings);

            ParticleSystem.Particle[] stars = Snapshot(view.Sparkles);
            float density = stars.Length / path.LengthMeters;
            Assert.GreaterOrEqual(density, 8f, "The floor hint is a dense stream rather than a sparse dotted line.");
            Assert.LessOrEqual(density, 15f, "Density stays inside the owner's particles-per-metre contract.");
            float minLateral = float.MaxValue;
            float maxLateral = float.MinValue;
            float minHeight = float.MaxValue;
            float maxHeight = float.MinValue;
            float minSize = float.MaxValue;
            float maxSize = float.MinValue;
            float lateral = settings.RouteHintFloorSparkleLateralJitterMeters
                            + settings.RouteHintFloorSparkleLateralWobbleMeters;
            float vertical = settings.RouteHintFloorSparkleVerticalJitterMeters;
            float height = settings.RouteHintFloorSparkleHeightMeters;
            float size = settings.RouteHintFloorSparkleSizeMeters;
            foreach (ParticleSystem.Particle star in stars)
            {
                Assert.LessOrEqual(Mathf.Abs(star.position.z), lateral + 0.001f,
                    "Lateral scatter (jitter plus sway) stays inside the configured route width.");
                Assert.That(star.position.y, Is.InRange(height - vertical - 0.001f, height + vertical + 0.001f),
                    "Vertical scatter is measured above the actual floor.");
                Assert.That(star.startSize, Is.InRange(size * settings.RouteHintFloorSparkleSizeMinScale - 0.001f,
                    size * settings.RouteHintFloorSparkleSizeMaxScale + 0.001f),
                    "Size variation stays within its SO bounds.");
                minLateral = Mathf.Min(minLateral, star.position.z);
                maxLateral = Mathf.Max(maxLateral, star.position.z);
                minHeight = Mathf.Min(minHeight, star.position.y);
                maxHeight = Mathf.Max(maxHeight, star.position.y);
                minSize = Mathf.Min(minSize, star.startSize);
                maxSize = Mathf.Max(maxSize, star.startSize);
            }
            Assert.Greater(maxLateral - minLateral, lateral * 1.5f,
                "The stream visibly fills its authored width rather than collapsing onto the centerline.");
            Assert.Greater(maxHeight - minHeight, vertical,
                "The stream has noticeable height variation.");
            Assert.Greater(maxSize - minSize, size * 0.5f,
                "Stars have clearly different sizes even when size twinkle is disabled.");
        }

        [Test]
        public void Render_CurvedPath_StarsMoveAlongCenterlineInsteadOfEscapingItsTangent()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            RouteHintRibbonMeshData path = BuildRibbon(settings);
            view.Show(path, settings);
            view.Render(1f, 1f, 0f, settings);
            ParticleSystem.Particle[] before = Snapshot(view.Sparkles);

            view.Render(1f, 1f, 0.07f, settings);

            ParticleSystem.Particle[] after = Snapshot(view.Sparkles);
            Assert.Greater(after.Length, 0);
            bool moved = false;
            bool passedCorner = false;
            for (int i = 0; i < after.Length; i++)
            {
                Vector3 position = after[i].position;
                float planarError = DistanceToPathXZ(position, path.Centerline);
                Assert.LessOrEqual(planarError, settings.RouteHintFloorSparkleLateralJitterMeters
                                                + settings.RouteHintFloorSparkleLateralWobbleMeters + 0.01f,
                    "Every star follows the curved route rather than continuing on a launch tangent.");
                Assert.AreEqual(settings.RouteHintFloorSparkleHeightMeters,
                    position.y, settings.RouteHintFloorSparkleVerticalJitterMeters + 0.01f);
                passedCorner |= position.z > 1f;
                moved |= i < before.Length && Vector3.Distance(before[i].position, position) > 0.01f;
            }
            Assert.IsTrue(moved, "A running clock must move the star chain toward the target.");
            Assert.IsTrue(passedCorner, "The fixture exercises the segment after the turn.");
        }

        [Test]
        public void Render_StraightPath_KeepsConfiguredSpacingAndMovesAtConfiguredSpeed()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings(withoutJitter: true);
            RouteHintRibbonMeshData path = BuildStraight(settings, 8f);
            view.Show(path, settings);
            view.Render(1f, 1f, 0f, settings);
            ParticleSystem.Particle[] before = Snapshot(view.Sparkles);
            float elapsed = settings.RouteHintFloorSparkleSpacingMeters
                / settings.RouteHintFloorSparkleSpeedMetersPerSecond * 0.3f;

            view.Render(1f, 1f, elapsed, settings);

            ParticleSystem.Particle[] after = Snapshot(view.Sparkles);
            System.Array.Sort(after, (a, b) => a.position.x.CompareTo(b.position.x));
            Assert.Greater(after.Length, 3);
            for (int i = 1; i < after.Length; i++)
            {
                Assert.AreEqual(settings.RouteHintFloorSparkleSpacingMeters,
                    after[i].position.x - after[i - 1].position.x, 0.01f,
                    "Adjacent stars keep the authored spacing.");
            }
            bool observedTravel = false;
            foreach (ParticleSystem.Particle particle in before)
            {
                if (particle.position.x < 1f || particle.position.x > path.LengthMeters - 1f)
                {
                    continue;
                }
                float expectedX = particle.position.x + settings.RouteHintFloorSparkleSpeedMetersPerSecond * elapsed;
                foreach (ParticleSystem.Particle candidate in after)
                {
                    observedTravel |= Mathf.Abs(candidate.position.x - expectedX) < 0.01f;
                }
            }
            Assert.IsTrue(observedTravel, "Interior stars advance by the configured metres per scaled second.");
        }

        [Test]
        public void Render_TargetEnd_StarsFadeBeforeLeavingTheRoute()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings(withoutJitter: true);
            RouteHintRibbonMeshData path = BuildStraight(settings, 8f);
            view.Show(path, settings);
            view.Render(1f, 1f, 0f, settings);
            float elapsed = settings.RouteHintFloorSparkleSpacingMeters
                / settings.RouteHintFloorSparkleSpeedMetersPerSecond * 0.25f;

            view.Render(1f, 1f, elapsed, settings);

            ParticleSystem.Particle[] stars = Snapshot(view.Sparkles);
            float endAlpha = 1f;
            float interiorAlpha = 0f;
            bool endFound = false;
            foreach (ParticleSystem.Particle star in stars)
            {
                float remaining = path.LengthMeters - star.position.x;
                Assert.GreaterOrEqual(remaining, -0.001f, "Stars never overshoot the target.");
                if (remaining < settings.RouteHintFloorSparkleEndFadeMeters * 0.5f)
                {
                    endFound = true;
                    endAlpha = Mathf.Min(endAlpha, star.startColor.a / 255f);
                }
                else if (star.position.x > settings.RouteHintFloorStartFadeMeters
                         && remaining > settings.RouteHintFloorSparkleEndFadeMeters)
                {
                    interiorAlpha = Mathf.Max(interiorAlpha, star.startColor.a / 255f);
                }
            }
            Assert.IsTrue(endFound, "The fixture samples stars inside the target fade zone.");
            Assert.Greater(interiorAlpha, endAlpha, "Stars fade out near the target instead of popping away.");
        }

        [Test]
        public void Show_ExcessiveParticleBudget_ClampsToWebGlHardCapAndDisablesNativeSimulation()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            SerializedObject serialized = new(settings);
            serialized.FindProperty("routeHintFloorMaxSparkles").intValue = RouteHintFloorView.HardParticleCap * 4;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            ParticleSystem.MainModule main = view.Sparkles.main;
            main.useUnscaledTime = true;
            main.simulationSpeed = 1f;

            view.Show(BuildStraight(settings, 500f), settings);

            Assert.LessOrEqual(view.Sparkles.main.maxParticles, settings.RouteHintFloorMaxSparkles,
                "The native particle capacity must respect the configured limit too.");
            Assert.LessOrEqual(view.Sparkles.main.maxParticles, RouteHintFloorView.HardParticleCap);
            Assert.AreEqual(0f, view.Sparkles.main.simulationSpeed,
                "Only Render places particles; Shuriken cannot move them away from the route.");
            Assert.IsFalse(view.Sparkles.main.useUnscaledTime);
            Assert.AreEqual(ParticleSystemSimulationSpace.World, view.Sparkles.main.simulationSpace);
            Assert.IsFalse(view.Sparkles.emission.enabled);
        }

        [Test]
        public void Render_LongPath_NeverExceedsHardParticleCap()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            view.Show(BuildStraight(settings, 500f), settings);

            view.Render(1f, 1f, 0f, settings);

            Assert.Greater(view.Sparkles.particleCount, 0);
            Assert.LessOrEqual(view.Sparkles.particleCount, settings.RouteHintFloorMaxSparkles,
                "A long route must respect the configured budget before the hard cap.");
            Assert.LessOrEqual(view.Sparkles.particleCount, RouteHintFloorView.HardParticleCap);
        }

        [Test]
        public void Render_RepeatedScaledTime_PreservesParticleStateAndRevealClock()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            view.Show(BuildRibbon(settings), settings);
            view.Render(0.5f, 1f, 1f, settings);
            ParticleSystem.Particle[] before = Snapshot(view.Sparkles);
            float animationTime = view.AnimationTime;
            float reveal = view.RevealProgress;

            view.Render(0.5f, 1f, 1f, settings);

            Assert.AreEqual(animationTime, view.AnimationTime);
            Assert.AreEqual(reveal, view.RevealProgress);
            AssertParticleStatesEqual(before, Snapshot(view.Sparkles));
        }

        [Test]
        public void Hide_LiveStars_ClearsTheWholeParticleHierarchyAndReleasesPath()
        {
            RouteHintFloorView view = CreateView(true);
            ParticleSystem child = new GameObject("ChildStars").AddComponent<ParticleSystem>();
            child.transform.SetParent(view.Sparkles.transform, false);
            LevelMinimapSettings settings = CreateSettings();
            view.Show(BuildRibbon(settings), settings);
            view.Render(1f, 1f, 0f, settings);
            child.Emit(1);
            Assert.Greater(view.Sparkles.particleCount, 0);

            view.Hide();

            Assert.IsFalse(view.HasPath);
            Assert.IsFalse(view.IsRibbonVisible);
            Assert.AreEqual(0, view.Sparkles.particleCount);
            Assert.AreEqual(0, child.particleCount, "Hide also clears particles frozen under child systems.");
            Assert.IsFalse(view.Sparkles.IsAlive(true));
        }

        /// <summary>
        /// Контроллер карты зовёт <c>Hide</c> каждый кадр, пока подсказка скрыта; остановка системы частиц обходит
        /// всю её иерархию, поэтому повтор на уже погашенной ленте обязан ничего не делать.
        /// </summary>
        [Test]
        public void Hide_AlreadyHidden_DoesNotStopTheParticlesAgain()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            view.Show(BuildRibbon(settings), settings);
            view.Render(1f, 1f, 0f, settings);
            view.Hide();
            int stops = view.SparkleStopCount;

            for (int frame = 0; frame < 30; frame++)
            {
                view.Hide();
            }

            Assert.AreEqual(stops, view.SparkleStopCount, "Hiding a hidden view is free.");
            Assert.IsFalse(view.HasPath);

            view.Show(BuildRibbon(settings), settings);
            view.Render(1f, 1f, 0f, settings);
            Assert.Greater(view.Sparkles.particleCount, 0, "Precondition: shown again.");
            view.Hide();

            Assert.AreEqual(0, view.Sparkles.particleCount, "A really shown view is still cleared by the next Hide.");
        }

        [Test]
        public void Render_CompletedFade_ClearsStarsWithoutWaitingForSimulation()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            view.Show(BuildRibbon(settings), settings);
            view.Render(1f, 1f, 0f, settings);
            Assert.Greater(view.Sparkles.particleCount, 0);

            view.Render(1f, 0f, 0f, settings);

            Assert.IsFalse(view.Sparkles.IsAlive(true));
            Assert.IsFalse(view.IsRibbonVisible);
        }

        [Test]
        public void Render_StarTint_ComesFromTheSettingsAsset()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            SerializedObject serialized = new(settings);
            serialized.FindProperty("routeHintFloorSparkleColor").colorValue = new Color(0.2f, 1.8f, 0.4f, 1f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            view.Show(BuildRibbon(settings), settings);

            view.Render(1f, 1f, 0f, settings);

            Color tint = view.Sparkles.GetComponent<ParticleSystemRenderer>().sharedMaterial.GetColor("_Tint");
            Assert.AreEqual(0.2f, tint.r, 0.001f);
            Assert.AreEqual(1.8f, tint.g, 0.001f);
            Assert.AreEqual(0.4f, tint.b, 0.001f);
        }

        [Test]
        public void Render_Drift_FollowsTheSpeedInTheSettingsAsset()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings(withoutJitter: true);
            SerializedObject serialized = new(settings);
            serialized.FindProperty("routeHintFloorSparkleSpeedMetersPerSecond").floatValue = 2f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            view.Show(BuildStraight(settings, 12f), settings);
            view.Render(1f, 1f, 0f, settings);
            ParticleSystem.Particle[] before = Snapshot(view.Sparkles);

            view.Render(1f, 1f, 0.5f, settings);

            ParticleSystem.Particle[] after = Snapshot(view.Sparkles);
            bool movedByTheConfiguredSpeed = false;
            foreach (ParticleSystem.Particle star in before)
            {
                if (star.position.x < 1f || star.position.x > 8f)
                {
                    continue;
                }

                foreach (ParticleSystem.Particle candidate in after)
                {
                    movedByTheConfiguredSpeed |= Mathf.Abs(candidate.position.x - (star.position.x + 1f)) < 0.01f;
                }
            }

            Assert.IsTrue(movedByTheConfiguredSpeed, "2 m/s from the asset moves a star 1 m in half a second.");
        }

        [Test]
        public void Render_HeadAheadOfTheStudent_DrawsNoStarsBehindIt()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings(withoutJitter: true);
            view.Show(BuildStraight(settings, 12f), settings);
            view.SetHead(5f);

            view.Render(1f, 1f, 1f, settings);

            ParticleSystem.Particle[] stars = Snapshot(view.Sparkles);
            Assert.Greater(stars.Length, 0);
            foreach (ParticleSystem.Particle star in stars)
            {
                Assert.Greater(star.position.x, 5f, "The path behind the visible start is not drawn.");
            }
            Assert.AreEqual(5f, view.HeadDistance, 0.001f);
        }

        [Test]
        public void Render_TightCorridorClearances_PullTheScatterOffTheWalls()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            RouteHintRibbonMeshData path = BuildStraight(settings, 8f);
            float[] tight = new float[path.Centerline.Count];
            for (int i = 0; i < tight.Length; i++)
            {
                tight[i] = 0.35f;
            }
            view.Show(path, tight, settings);

            view.Render(1f, 1f, 1f, settings);

            ParticleSystem.Particle[] stars = Snapshot(view.Sparkles);
            Assert.Greater(stars.Length, 0);
            foreach (ParticleSystem.Particle star in stars)
            {
                Assert.LessOrEqual(Mathf.Abs(star.position.z), 0.35f - settings.RouteHintFloorSparkleWallMarginMeters + 0.001f,
                    "With 0.35 m of free width the stars may stray only the part not used up by the wall margin.");
            }
        }

        [Test]
        public void UpdatePath_AfterAReplan_KeepsTheRevealAndTheClock()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();
            view.Show(BuildStraight(settings, 8f), settings);
            view.Render(0.5f, 1f, 2f, settings);
            float reveal = view.RevealProgress;

            bool updated = view.UpdatePath(BuildStraight(settings, 12f), null);
            view.Render(0.5f, 1f, 2f, settings);

            Assert.IsTrue(updated);
            Assert.AreEqual(12f, view.LengthMeters, 0.001f, "The stars now follow the re-planned route.");
            Assert.AreEqual(reveal, view.RevealProgress, "A re-plan does not restart the reveal.");
            Assert.AreEqual(2f, view.AnimationTime, "A re-plan does not restart the scaled clock.");
        }

        [Test]
        public void UpdatePath_WithoutAShownPath_IsRefused()
        {
            RouteHintFloorView view = CreateView(true);
            LevelMinimapSettings settings = CreateSettings();

            Assert.IsFalse(view.UpdatePath(BuildStraight(settings, 8f), null));
        }

        [Test]
        public void BuildFromAxis_UsesTheSharedPointsExactly_WithoutResmoothing()
        {
            List<Vector2> axis = new() { new Vector2(0f, 0f), new Vector2(3f, 0f), new Vector2(3f, 3f) };
            RouteHintRibbonMeshData ribbon = new();

            bool built = RouteHintRibbonMeshBuilder.BuildFromAxis(axis, new RouteHintRibbonShape(0.5f, 0.9f, 0.12f, 0.3f,
                0.03f), 0f, null, ribbon, out RouteHintRibbonFailure failure);

            Assert.IsTrue(built);
            Assert.AreEqual(RouteHintRibbonFailure.None, failure);
            Assert.AreEqual(axis.Count, ribbon.Centerline.Count, "No rounding and no resampling: the same points.");
            for (int i = 0; i < axis.Count; i++)
            {
                Assert.AreEqual(axis[i].x, ribbon.Centerline[i].x, 0.0001f);
                Assert.AreEqual(axis[i].y, ribbon.Centerline[i].z, 0.0001f);
            }
        }

        [Test]
        public void Probe_FloorBelow_ReturnsItsHeight()
        {
            CreateBox("Floor", new Vector3(0f, -0.1f, 0f), new Vector3(10f, 0.2f, 10f), false);
            Physics.SyncTransforms();
            RouteHintFloorRaycastProbe probe = new(~0, 1.2f, null);

            bool hit = probe.Probe(new Vector2(1f, 1f), 0f, out float height);

            Assert.IsTrue(hit);
            Assert.AreEqual(0f, height, 0.001f);
        }

        [Test]
        public void Probe_TriggersAndThePlayer_AreNotFloor()
        {
            CreateBox("Floor", new Vector3(0f, -0.1f, 0f), new Vector3(10f, 0.2f, 10f), false);
            CreateBox("RoomTrigger", new Vector3(0f, 0.5f, 0f), new Vector3(10f, 0.2f, 10f), true);
            GameObject player = CreateBox("Player", new Vector3(1f, 0.9f, 1f), new Vector3(0.6f, 0.2f, 0.6f), false);
            Physics.SyncTransforms();
            RouteHintFloorRaycastProbe probe = new(~0, 1.2f, player.transform);

            bool hit = probe.Probe(new Vector2(1f, 1f), 0f, out float height);

            Assert.IsTrue(hit);
            Assert.AreEqual(0f, height, 0.001f, "Room triggers and the player's own collider are skipped.");
        }

        [Test]
        public void Probe_NoFloor_ReportsAMiss()
        {
            Physics.SyncTransforms();
            RouteHintFloorRaycastProbe probe = new(~0, 1.2f, null);

            bool hit = probe.Probe(new Vector2(500f, 500f), 0f, out float height);

            Assert.IsFalse(hit);
            Assert.AreEqual(0f, height, "A miss keeps the previous height.");
        }

        /// <summary>
        /// Три ступеньки по 0,2 м поднимают пол на 0,6 м — выше допуска от пола ПОД ИГРОКОМ, но каждая в
        /// допуске от предыдущей точки: лента обязана подняться по всем. Стол на верхней ступеньке — декор.
        /// </summary>
        [Test]
        public void Probe_WithMaxStep_RibbonClimbsStairsButNotATable()
        {
            CreateBox("Floor", new Vector3(5f, -0.1f, 0f), new Vector3(20f, 0.2f, 4f), false);
            CreateBox("Step1", new Vector3(3.85f, 0.1f, 0f), new Vector3(1.5f, 0.2f, 4f), false);
            CreateBox("Step2", new Vector3(5.35f, 0.2f, 0f), new Vector3(1.5f, 0.4f, 4f), false);
            CreateBox("Step3", new Vector3(8.3f, 0.3f, 0f), new Vector3(4.4f, 0.6f, 4f), false);
            CreateBox("Table", new Vector3(7.35f, 1.3f, 0f), new Vector3(0.5f, 0.1f, 4f), false);
            Physics.SyncTransforms();
            RouteHintFloorRaycastProbe probe = new(~0, 1.2f, null);
            List<Vector2> polyline = new() { new Vector2(0f, 0f), new Vector2(10f, 0f) };
            RouteHintRibbonMeshData ribbon = new();

            RouteHintRibbonMeshBuilder.Build(polyline, new RouteHintRibbonShape(0.5f, 1f, 0.12f, 0.25f, 0f, 0.35f),
                0f, probe.Probe, ribbon);

            foreach (Vector3 point in ribbon.Centerline)
            {
                float expected = point.x < 3.1f ? 0f : point.x < 4.6f ? 0.2f : point.x < 6.1f ? 0.4f : 0.6f;
                Assert.AreEqual(expected, point.y, 0.01f,
                    $"At x={point.x:0.00} the ribbon follows the stairs step by step and never climbs the table.");
            }
        }

        private RouteHintFloorView CreateView(bool withMaterial)
        {
            GameObject go = new("RouteHintFloorView");
            _created.Add(go);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            ParticleSystem sparkles = new GameObject("Stars").AddComponent<ParticleSystem>();
            sparkles.transform.SetParent(go.transform, false);
            sparkles.GetComponent<ParticleSystemRenderer>().sharedMaterial = null;
            if (withMaterial)
            {
                sparkles.GetComponent<ParticleSystemRenderer>().sharedMaterial = CreateMaterial();
            }
            RouteHintFloorView view = go.AddComponent<RouteHintFloorView>();
            view.SetSparklesForTests(sparkles);
            return view;
        }

        private Material CreateMaterial()
        {
            Shader shader = Shader.Find(RouteHintFloorView.StarsShaderName);
            Assert.NotNull(shader, "The star shader must be imported.");
            Material material = new(shader);
            _created.Add(material);
            return material;
        }

        private LevelMinimapSettings CreateSettings(bool withoutJitter = false)
        {
            LevelMinimapSettings settings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
            _created.Add(settings);
            if (withoutJitter)
            {
                SerializedObject serialized = new(settings);
                serialized.FindProperty("routeHintFloorSparkleLateralJitterMeters").floatValue = 0f;
                serialized.FindProperty("routeHintFloorSparkleVerticalJitterMeters").floatValue = 0f;
                serialized.FindProperty("routeHintFloorSparkleTwinkleAlpha").floatValue = 0f;
                serialized.FindProperty("routeHintFloorSparkleAlongWobbleMeters").floatValue = 0f;
                serialized.FindProperty("routeHintFloorSparkleLateralWobbleMeters").floatValue = 0f;
                serialized.FindProperty("routeHintFloorSparkleLongTwinkleFraction").floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return settings;
        }

        private static RouteHintRibbonMeshData BuildRibbon(LevelMinimapSettings settings)
        {
            RouteHintRibbonMeshData ribbon = new();
            RouteHintRibbonMeshBuilder.Build(new List<Vector2> { new(0f, 0f), new(3f, 0f), new(3f, 3f) },
                settings.CreateRouteHintRibbonShape(), 0f, null, ribbon);
            return ribbon;
        }

        private static RouteHintRibbonMeshData BuildStraight(LevelMinimapSettings settings, float length)
        {
            RouteHintRibbonMeshData path = new();
            RouteHintRibbonMeshBuilder.Build(new List<Vector2> { Vector2.zero, new(length, 0f) },
                settings.CreateRouteHintRibbonShape(), 0f, null, path);
            return path;
        }

        private static ParticleSystem.Particle[] Snapshot(ParticleSystem sparkles)
        {
            ParticleSystem.Particle[] particles = new ParticleSystem.Particle[sparkles.main.maxParticles];
            int count = sparkles.GetParticles(particles);
            System.Array.Resize(ref particles, count);
            return particles;
        }

        private static void AssertParticleStatesEqual(ParticleSystem.Particle[] before, ParticleSystem.Particle[] after)
        {
            Assert.AreEqual(before.Length, after.Length, "A paused clock cannot change the number of stars.");
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i].position, after[i].position, "Star motion freezes with scaled time.");
                Assert.AreEqual(before[i].startColor, after[i].startColor, "Twinkle freezes with scaled time.");
                Assert.AreEqual(before[i].startSize, after[i].startSize, "Twinkle size freezes with scaled time.");
                Assert.AreEqual(before[i].remainingLifetime, after[i].remainingLifetime);
            }
        }

        private static float DistanceToPathXZ(Vector3 point, IReadOnlyList<Vector3> centerline)
        {
            Vector2 sample = new(point.x, point.z);
            float nearest = float.MaxValue;
            for (int i = 1; i < centerline.Count; i++)
            {
                Vector2 a = new(centerline[i - 1].x, centerline[i - 1].z);
                Vector2 b = new(centerline[i].x, centerline[i].z);
                Vector2 segment = b - a;
                float t = segment.sqrMagnitude > 0f
                    ? Mathf.Clamp01(Vector2.Dot(sample - a, segment) / segment.sqrMagnitude) : 0f;
                nearest = Mathf.Min(nearest, Vector2.Distance(sample, a + segment * t));
            }
            return nearest;
        }

        private GameObject CreateBox(string name, Vector3 center, Vector3 size, bool trigger)
        {
            GameObject go = new(name);
            _created.Add(go);
            go.transform.position = center;
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = size;
            box.isTrigger = trigger;
            return go;
        }
    }
}
