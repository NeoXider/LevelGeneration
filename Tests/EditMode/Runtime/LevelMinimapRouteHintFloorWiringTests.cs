using System;
using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using RedoSchool.LevelGeneration.Runtime.Application;
using RedoSchool.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// Проводка контроллера миникарты через <c>TickFrame</c> — настоящий кадр карты без устройств ввода:
    /// большая карта прячется, пока ученика держит UI; пунктир ждёт ленту на полу и включается сразу, если
    /// ленту не построить; на время проявления путь не теряется; выключенная карта гасит ленту.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelMinimapRouteHintFloorWiringTests
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
        public void FullMap_WhileUiHoldsTheStudent_IsHiddenAndIgnoresM_ThenReturnsWithControl()
        {
            LevelMinimapController controller = CreateController(out _, new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout());
            controller.TickFrame(RouteHintPresence.Exploring, true, 0.01f, 0.01f, 0f);
            Assert.IsTrue(controller.IsFullMapVisible, "Precondition: M opened the full map in the world.");

            controller.TickFrame(RouteHintPresence.Occupied, true, 0.01f, 0.01f, 0.1f);
            bool hiddenWhileOccupied = !controller.IsFullMapVisible;
            bool stillOpen = controller.Session.IsFullMapOpen;
            controller.TickFrame(RouteHintPresence.Exploring, false, 0.01f, 0.01f, 0.2f);

            Assert.IsTrue(hiddenWhileOccupied, "An open panel must not sit under a blue frame of the full map.");
            Assert.IsTrue(stillOpen, "M is ignored while UI holds the student: it would toggle an invisible map.");
            Assert.IsTrue(controller.IsFullMapVisible, "The open map comes back together with control.");
        }

        [Test]
        public void HintDue_MinimapDashesWaitForTheFloorReveal()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f));
            RouteHintFloorView view = CreateView(true);
            controller.Bind(CreateLayout());
            controller.SetRouteHintFloorView(view);
            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f);
            Assert.AreEqual(RouteHintRevealPhase.FloorRevealing, controller.RouteHintRevealPhase,
                "Precondition: the hint is due and the floor ribbon is revealing.");
            float alphaDuringReveal = controller.RouteHintAlpha;
            bool pathReady = view.HasPath;

            float revealed = settings.RouteHintFloorRevealSeconds + 0.01f;
            controller.TickFrame(RouteHintPresence.Exploring, false, 0.01f, revealed, revealed);

            Assert.AreEqual(0f, alphaDuringReveal, "The minimap waits while the ribbon reveals on the floor.");
            Assert.IsTrue(pathReady, "The floor star path is prepared before the minimap dashes appear.");
            Assert.IsFalse(view.IsRibbonVisible, "The golden trail never draws a solid ribbon.");
            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, controller.RouteHintRevealPhase);
            Assert.Greater(controller.RouteHintAlpha, 0f, "The minimap dashes turn on when the reveal finishes.");
        }

        [Test]
        public void Occupied_WhilePaused_HidesHoldingRibbonAndMinimapUsingUnscaledTime()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f));
            RouteHintFloorView view = CreateView(true);
            ParticleSystem sparkles = view.Sparkles;
            controller.Bind(CreateLayout());
            controller.SetRouteHintFloorView(view);
            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f, 0f);
            float scaledNow = settings.RouteHintFloorRevealSeconds + settings.RouteHintFadeSeconds;
            controller.TickFrame(RouteHintPresence.Exploring, false, scaledNow, scaledNow, scaledNow, scaledNow);
            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, controller.RouteHintRevealPhase,
                "Precondition: the floor ribbon has finished revealing.");
            Assert.Greater(controller.RouteHintAlpha, 0f, "Precondition: minimap dashes are visible.");
            Assert.IsTrue(view.HasPath);
            Assert.IsTrue(sparkles.IsAlive(true), "Precondition: there are live sparkles to clear.");
            float previousTimeScale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;
                float hideStartedAt = scaledNow + settings.RouteHintFloorHideFadeSeconds;
                controller.TickFrame(RouteHintPresence.Occupied, false, 0f,
                    0f, hideStartedAt, scaledNow);
                Assert.AreEqual(RouteHintRevealPhase.FloorFading, controller.RouteHintRevealPhase,
                    "Precondition: opening a panel started the hide fade while scaled time is frozen.");
                float hideElapsed = settings.RouteHintFloorHideFadeSeconds + 0.01f;

                controller.TickFrame(RouteHintPresence.Occupied, false, 0f, hideElapsed,
                    hideStartedAt + hideElapsed, scaledNow);

                Assert.AreEqual(0f, controller.RouteHintAlpha,
                    "Minimap dashes disappear while the pupil is occupied, even with timeScale zero.");
                Assert.AreEqual(RouteHintRevealPhase.Hidden, controller.RouteHintRevealPhase,
                    "The configured hide duration uses unscaled time.");
                Assert.IsFalse(view.HasPath, "The hidden ribbon releases its active display state.");
                Assert.IsFalse(view.IsRibbonVisible, "No floor ribbon remains beneath the open panel.");
                Assert.IsFalse(sparkles.IsAlive(true), "The hide fade clears particles frozen by the pause.");
            }
            finally
            {
                Time.timeScale = previousTimeScale;
            }
        }

        [Test]
        public void HintDue_RibbonCannotBeBuilt_MinimapDashesTurnOnRightAway()
        {
            Vector2 door = PlannedTargetPoint();
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(door.x, 0f, door.y));
            RouteHintFloorView view = CreateView(true);
            controller.Bind(CreateLayout());
            controller.SetRouteHintFloorView(view);

            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome,
                "Precondition: the path is planned but has no length for a ribbon.");
            Assert.IsFalse(view.HasPath);
            Assert.Greater(controller.RouteHintAlpha, 0f, "A ribbon that cannot be built must not hold the minimap back.");
        }

        [Test]
        public void HintDue_WithoutRibbonMaterial_MinimapDashesTurnOnRightAway()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout());
            controller.SetRouteHintFloorView(CreateView(false));

            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f);

            Assert.AreEqual(RouteHintRevealPhase.FloorDone, controller.RouteHintRevealPhase);
            Assert.Greater(controller.RouteHintAlpha, 0f, "Without a material the minimap shows the route at once.");
        }

        [Test]
        public void FloorReveal_MinimapStillWaiting_PlannedPathIsKept()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f));
            controller.Bind(CreateLayout());
            controller.SetRouteHintFloorView(CreateView(true));
            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f);

            float midReveal = settings.RouteHintFloorRevealSeconds * 0.5f;
            controller.TickFrame(RouteHintPresence.Exploring, false, 0.01f, midReveal, midReveal);

            Assert.AreEqual(RouteHintRevealPhase.FloorRevealing, controller.RouteHintRevealPhase);
            Assert.AreEqual(0f, controller.RouteHintAlpha);
            Assert.Greater(controller.RouteHintWorldPath.Count, 1,
                "A minimap waiting for the floor is not a hidden hint: the planned path must survive.");
        }

        [Test]
        public void OverlayHidden_DuringReveal_RibbonLeavesTheFloor()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f));
            RouteHintFloorView view = CreateView(true);
            controller.Bind(CreateLayout());
            controller.SetRouteHintFloorView(view);
            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f);
            Assert.IsTrue(view.HasPath, "Precondition: the star path is prepared.");

            controller.SetOverlayVisible(false);

            Assert.IsFalse(view.HasPath, "A disabled minimap releases the active star path.");
            Assert.IsFalse(view.Sparkles.IsAlive(true), "A disabled minimap leaves no frozen stars on the floor.");
            Assert.AreEqual(RouteHintRevealPhase.Hidden, controller.RouteHintRevealPhase);
        }

        [Test]
        public void FloorReveal_StudentOffTheTrail_StarsStillDrawTheSharedTrailPoints()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f), out Transform player);
            controller.Bind(CreateLayout());
            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f);
            Vector2 plannedStart = controller.RouteHintWorldPath[0];
            Assert.AreEqual(RouteHintRevealPhase.FloorDone, controller.RouteHintRevealPhase,
                "Precondition: without a ribbon view the path is planned and the minimap shows it.");
            player.position = new Vector3(40f, 0f, 40f);
            controller.SetRouteHintFloorView(CreateView(true));

            controller.TickFrame(RouteHintPresence.Exploring, false, 0.01f, 0.01f, 0.05f);

            Assert.AreEqual(RouteHintRevealPhase.FloorRevealing, controller.RouteHintRevealPhase);
            Assert.AreEqual(plannedStart, controller.RouteHintFloorPolyline[0],
                "The stars draw the one shared trail; a student off it only triggers a throttled re-plan, "
                + "never a private line for the floor.");
        }

        [Test]
        public void FloorReveal_PathNotReady_RetriesInsteadOfDisablingTheRibbon()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f), out _);
            RouteHintFloorView view = CreateView(true);
            controller.Bind(CreateLayout());
            controller.SetRouteHintFloorView(view);
            bool minimapDuringRetry = controller.AdvanceRouteHintFloor(true, 0f);
            Assert.AreEqual(RouteHintRevealPhase.Hidden, controller.RouteHintRevealPhase,
                "Precondition: with no path yet the reveal is rolled back for a retry.");

            controller.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0.1f);

            Assert.IsFalse(minimapDuringRetry, "The minimap does not jump ahead of the floor while retrying.");
            Assert.AreEqual(RouteHintRevealPhase.FloorRevealing, controller.RouteHintRevealPhase,
                "Once the path exists, the ribbon reveals: a missing path is not a failure.");
            Assert.IsTrue(view.HasPath);
        }

        private Vector2 PlannedTargetPoint()
        {
            LevelMinimapController probe = CreateController(out LevelMinimapSettings settings,
                new Vector3(0.2f, 0f, 0.5f));
            probe.Bind(CreateLayout());
            probe.TickFrame(RouteHintPresence.Exploring, false, settings.RouteHintIdleSeconds, 0.1f, 0f);
            Assert.Greater(probe.RouteHintWorldPath.Count, 1, "Precondition: the reference path is planned.");
            return probe.RouteHintWorldPath[probe.RouteHintWorldPath.Count - 1];
        }

        private LevelMinimapController CreateController(out LevelMinimapSettings settings, Vector3 playerPosition) =>
            CreateController(out settings, playerPosition, out _);

        private LevelMinimapController CreateController(out LevelMinimapSettings settings, Vector3 playerPosition,
            out Transform playerTransform)
        {
            GameObject go = new("MinimapFloorWiringTest");
            _created.Add(go);
            settings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
            _created.Add(settings);
            GameObject player = new("MinimapFloorWiringPlayer");
            _created.Add(player);
            player.transform.position = playerPosition;
            LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
            SerializedObject serialized = new(controller);
            serialized.FindProperty("settings").objectReferenceValue = settings;
            serialized.FindProperty("playerTransform").objectReferenceValue = player.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            playerTransform = player.transform;
            return controller;
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
                Shader shader = Shader.Find(RouteHintFloorView.StarsShaderName);
                Assert.NotNull(shader, "The star shader must be imported.");
                Material material = new(shader);
                _created.Add(material);
                sparkles.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            }

            RouteHintFloorView view = go.AddComponent<RouteHintFloorView>();
            view.SetSparklesForTests(sparkles);
            return view;
        }

        private static LevelLayout CreateLayout() => new(
            new[]
            {
                Room("n_start", 0f), Room("n_objective", 2f), Room("n_exit", 4f)
            },
            new[]
            {
                new DoorConnection("n_start", "east", "n_objective", "west", true),
                new DoorConnection("n_objective", "east", "n_exit", "west", true)
            },
            "n_start",
            new[] { "n_exit" });

        private static PlacedRoom Room(string nodeId, float x) =>
            new(nodeId, $"template_{nodeId}", new GridPose(x, 0f, 0),
                new Dictionary<string, DoorState>(StringComparer.Ordinal));
    }
}
