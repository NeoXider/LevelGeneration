using System;
using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Runtime.Application;
using Neoxider.LevelGeneration.Runtime.Domain.Events;
using Neoxider.LevelGeneration.Runtime.Infrastructure;
using Neoxider.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Route hint wiring on a level with real proportions: one shared trail for the floor stars and the minimap
    /// dashes, a visible start ahead of the player, throttled re-plans, a hint that stays while the player walks
    /// along it and hides only on real progress (a room on the hinted path), never for a side room.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelMinimapRouteHintTrailWiringTests
    {
        private readonly List<Object> _created = new();
        private float _now;

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created.ToArray())
            {
                if (created is GameObject owner)
                {
                    foreach (ParticleSystemRenderer renderer in owner.GetComponentsInChildren<ParticleSystemRenderer>(true))
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
            _now = 0f;
        }

        [Test]
        public void FloorStarsAndMinimapDashes_DrawTheSameTrailPoints()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings, out _, out _);
            RouteHintFloorView view = CreateView();
            controller.SetRouteHintFloorView(view);
            Tick(controller, settings.RouteHintIdleSeconds);
            Tick(controller, settings.RouteHintFloorRevealSeconds + 0.05f);
            RouteHintTrail trail = controller.RouteHintTrail;
            Assert.IsTrue(view.HasPath, "Precondition: the floor stars are shown.");
            Assert.Greater(controller.RouteHintAlpha, 0f, "Precondition: the minimap dashes are shown.");
            Assert.Greater(trail.Points.Count, 10, "Precondition: a real, smoothed trail across the hall.");

            Assert.AreEqual(trail.Points.Count, view.Centerline.Count,
                "The floor view got the shared points: no second smoothing, no resampling.");
            for (int i = 0; i < trail.Points.Count; i++)
            {
                Assert.AreEqual(trail.Points[i].x, view.Centerline[i].x, 0.0001f, $"point {i} x");
                Assert.AreEqual(trail.Points[i].y, view.Centerline[i].z, 0.0001f, $"point {i} z");
            }

            Assert.AreEqual(trail.HeadDistance, view.HeadDistance, 0.0001f, "Same visible start for both.");
            Assert.AreSame(trail.Points, controller.RouteHintFloorPolyline);
            Assert.AreSame(trail.Points, controller.RouteHintWorldPath);

            Rect rect = new(0f, 0f, 800f, 480f);
            Assert.IsTrue(controller.TryBuildProjection(controller.Session.GetAllRooms(), rect, false,
                out LevelMinimapController.MapProjection projection));
            controller.BuildRouteHintScreenPath(projection, false);
            List<Vector2> visible = new();
            trail.FillVisible(visible);
            Assert.AreEqual(trail.HeadPoint, visible[0]);
            Assert.AreEqual(visible.Count, controller.RouteHintScreenPath.Count,
                "The dashes are drawn along the same visible points as the stars.");
            for (int i = 0; i < visible.Count; i++)
            {
                Assert.Less(Vector2.Distance(controller.ProjectRouteHintPoint(visible[i], projection),
                    controller.RouteHintScreenPath[i]), 0.001f, $"dash point {i}");
            }

            Assert.AreEqual(trail.Points[trail.Points.Count - 1], visible[visible.Count - 1]);
        }

        [Test]
        public void StartOffset_StarsAndDashesBeginAheadOfThePlayer_NotAtHisFeet()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                out Transform player, out _);
            Tick(controller, settings.RouteHintIdleSeconds);
            RouteHintTrail trail = controller.RouteHintTrail;
            Vector2 student = new(player.position.x, player.position.z);

            Assert.AreEqual(settings.RouteHintPathStartOffsetMeters, trail.HeadDistance - trail.ProgressMeters, 0.05f);
            Assert.AreEqual(settings.RouteHintPathStartOffsetMeters, Vector2.Distance(trail.HeadPoint, student), 0.1f,
                "The visible trail starts about 0.75 m in front of the student, along the path.");
        }

        [Test]
        public void StartOffset_FollowsTheSettingsAsset()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                out Transform player, out _);
            SerializedObject serialized = new(settings);
            serialized.FindProperty("routeHintPathStartOffsetMeters").floatValue = 1.6f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Tick(controller, settings.RouteHintIdleSeconds);

            RouteHintTrail trail = controller.RouteHintTrail;
            Assert.AreEqual(1.6f, trail.HeadDistance - trail.ProgressMeters, 0.05f);
            Assert.AreEqual(1.6f, Vector2.Distance(trail.HeadPoint,
                new Vector2(player.position.x, player.position.z)), 0.1f);
        }

        [Test]
        public void Replan_WalkingAlongTheTrailNeverPlansAgain_LeavingItDoes_ButNotEveryFrame()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                out Transform player, out _);
            Tick(controller, settings.RouteHintIdleSeconds);
            Assert.AreEqual(1, controller.RouteHintPlanCount, "Precondition: planned once when the hint appeared.");
            int revision = controller.RouteHintTrail.Revision;

            for (int step = 1; step <= 150; step++)
            {
                player.position = new Vector3(2.5f + step * 0.1f, 0f, 2.5f);
                Tick(controller, 0.1f);
            }

            Assert.AreEqual(1, controller.RouteHintPlanCount, "150 frames along the trail: not one re-plan.");
            Assert.AreEqual(revision, controller.RouteHintTrail.Revision);
            Assert.AreEqual(controller.RouteHintTrail.ProgressMeters + settings.RouteHintPathStartOffsetMeters,
                controller.RouteHintTrail.HeadDistance, 0.05f, "The visible start followed him.");

            player.position = new Vector3(17.5f, 0f, 4.2f);
            Tick(controller, 0.01f);
            Assert.AreEqual(2, controller.RouteHintPlanCount, "Walked off the trail: planned again from where he stands.");
            Assert.AreNotEqual(revision, controller.RouteHintTrail.Revision);
            Assert.Less(Vector2.Distance(controller.RouteHintTrail.Points[0], new Vector2(17.5f, 4.2f)), 0.05f);

            player.position = new Vector3(10f, 0f, 0.6f);
            Tick(controller, 0.01f);
            Assert.AreEqual(2, controller.RouteHintPlanCount,
                "Off the trail again inside the throttle interval: no re-plan this frame.");
            Tick(controller, settings.RouteHintReplanIntervalSeconds);
            Assert.AreEqual(3, controller.RouteHintPlanCount, "The throttle opened: one re-plan, not one per frame.");
        }

        [Test]
        public void Hint_StaysWhileThePlayerWalksTheTrail_AndHidesWhenHeEntersTheNextRoomOnTheRoute()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                out Transform player, out SimpleRoomEventBus bus);
            RouteHintFloorView view = CreateView();
            controller.SetRouteHintFloorView(view);
            Tick(controller, settings.RouteHintIdleSeconds);
            Tick(controller, settings.RouteHintFloorRevealSeconds + 0.05f);
            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, controller.RouteHintRevealPhase,
                "Precondition: the stars finished revealing.");

            for (int step = 1; step <= 170; step++)
            {
                player.position = new Vector3(2.5f + step * 0.1f, 0f, 2.5f);
                Tick(controller, 0.1f);
                if (step % 17 == 0)
                {
                    Assert.AreEqual(RouteHintRevealPhase.FloorHolding, controller.RouteHintRevealPhase,
                        $"Walking along the hint does not put it out (step {step}).");
                    Assert.Greater(controller.RouteHintAlpha, 0.99f, "The minimap dashes stay too.");
                    Assert.IsTrue(view.HasPath);
                }
            }

            Assert.Greater(view.Sparkles.particleCount, 0, "The last metres before the door are still lit.");
            float idleBefore = controller.RouteHint.IdleSeconds;
            Assert.Greater(idleBefore, 0f);

            bus.Publish(new RoomEnteredEvent("n_objective", RoomKind.Interactive));

            Assert.AreEqual(0f, controller.RouteHint.IdleSeconds, "A room on the route is progress: the idle timer resets.");
            Assert.AreEqual(RouteHintOutcome.Waiting, controller.RouteHint.Decision.Outcome);
            Tick(controller, 0.01f);
            Tick(controller, settings.RouteHintFloorHideFadeSeconds + 0.05f);
            Assert.AreEqual(0f, controller.RouteHintAlpha, "The dashes are gone.");
            Assert.AreEqual(RouteHintRevealPhase.Hidden, controller.RouteHintRevealPhase, "The stars are gone.");
            Assert.IsFalse(view.HasPath);
        }

        [Test]
        public void Hint_EnteringASideRoom_DoesNotHideIt_TheTrailReplansFromThePlayer()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings,
                out Transform player, out SimpleRoomEventBus bus);
            RouteHintFloorView view = CreateView();
            controller.SetRouteHintFloorView(view);
            Tick(controller, settings.RouteHintIdleSeconds);
            Tick(controller, settings.RouteHintFloorRevealSeconds + 0.05f);
            float idle = controller.RouteHint.IdleSeconds;

            bus.Publish(new RoomEnteredEvent("n_side", RoomKind.Interactive));
            player.position = new Vector3(7.5f, 0f, 8f);

            Assert.AreEqual(idle, controller.RouteHint.IdleSeconds, 0.001f, "A side room is not progress: no reset.");
            Assert.AreEqual(RouteHintOutcome.ShowHint, controller.RouteHint.Decision.Outcome);
            Tick(controller, settings.RouteHintReplanIntervalSeconds + 0.01f);
            Tick(controller, settings.RouteHintReplanIntervalSeconds + 0.01f);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            Assert.Greater(controller.RouteHintAlpha, 0f, "The hint is still shown.");
            Assert.AreNotEqual(RouteHintRevealPhase.Hidden, controller.RouteHintRevealPhase);
            RouteHintTrail trail = controller.RouteHintTrail;
            Assert.Less(Vector2.Distance(trail.Points[0], new Vector2(7.5f, 8f)), 0.05f,
                "The path now starts in the side room, where the student is.");
            Vector2 door = new(20f, 2.5f);
            Assert.Less(DistanceToTrail(trail, door), 0.05f, "It still leads through the door of the next room on the route.");
            // The route carries on past the target door (routeHintPathDoorLeadOutMeters), straight into that room.
            Vector2 leadOutEnd = door + Vector2.right * settings.RouteHintPathDoorLeadOutMeters;
            Assert.Less(Vector2.Distance(trail.Points[trail.Points.Count - 1], leadOutEnd), 0.05f,
                "It ends inside the next room on the route, past its door.");
        }

        private static float DistanceToTrail(RouteHintTrail trail, Vector2 point)
        {
            float best = float.MaxValue;
            for (int i = 1; i < trail.Points.Count; i++)
            {
                Vector2 from = trail.Points[i - 1];
                Vector2 segment = trail.Points[i] - from;
                float along = segment.sqrMagnitude > 0f
                    ? Mathf.Clamp01(Vector2.Dot(point - from, segment) / segment.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(point, from + segment * along));
            }

            return best;
        }

        private void Tick(LevelMinimapController controller, float deltaSeconds)
        {
            _now += deltaSeconds;
            controller.TickFrame(RouteHintPresence.Exploring, false, deltaSeconds, deltaSeconds, _now);
        }

        private LevelMinimapController CreateController(out LevelMinimapSettings settings, out Transform player,
            out SimpleRoomEventBus bus)
        {
            GameObject go = new("MinimapTrailWiringTest");
            _created.Add(go);
            settings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
            _created.Add(settings);
            GameObject playerObject = new("MinimapTrailWiringPlayer");
            _created.Add(playerObject);
            playerObject.transform.position = new Vector3(2.5f, 0f, 2.5f);
            LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
            SerializedObject serialized = new(controller);
            serialized.FindProperty("settings").objectReferenceValue = settings;
            serialized.FindProperty("playerTransform").objectReferenceValue = playerObject.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            player = playerObject.transform;
            bus = new SimpleRoomEventBus();
            controller.Bind(CreateLayout(), bus, null);
            InstallHallGeometry(controller.RouteHintGeometryForTests);
            return controller;
        }

        /// <summary>
        /// A 20 m hall (start) with a door east into the next room, and a side room north of it. Real proportions:
        /// 5 m cells, 0.3 m walls, 2.1 m doorways.
        /// </summary>
        private static void InstallHallGeometry(RouteHintGeometry geometry)
        {
            geometry.Clear();
            geometry.AddRoom("n_start", Vector2.zero, 0f, new[]
            {
                new MinimapCellSnapshot(0f, 0f, 5f, 5f), new MinimapCellSnapshot(5f, 0f, 5f, 5f),
                new MinimapCellSnapshot(10f, 0f, 5f, 5f), new MinimapCellSnapshot(15f, 0f, 5f, 5f)
            }, RouteHintTestLevels.Box(0f, 0f, 20f, 5f));
            geometry.AddRoom("n_objective", Vector2.zero, 0f, new[] { new MinimapCellSnapshot(20f, 0f, 5f, 5f) },
                RouteHintTestLevels.Box(20f, 0f, 25f, 5f));
            geometry.AddRoom("n_exit", Vector2.zero, 0f, new[] { new MinimapCellSnapshot(25f, 0f, 5f, 5f) },
                RouteHintTestLevels.Box(25f, 0f, 30f, 5f));
            geometry.AddRoom("n_side", Vector2.zero, 0f, new[] { new MinimapCellSnapshot(5f, 5f, 5f, 5f) },
                RouteHintTestLevels.Box(5f, 5f, 10f, 10f));
            geometry.AddDoor("n_start", "east", "n_objective", "west", new Vector2(20f, 2.5f), RouteHintTestLevels.DoorFrame);
            geometry.AddDoor("n_objective", "east", "n_exit", "west", new Vector2(25f, 2.5f), RouteHintTestLevels.DoorFrame);
            geometry.AddDoor("n_start", "north", "n_side", "south", new Vector2(7.5f, 5f), RouteHintTestLevels.DoorFrame);
        }

        private static LevelLayout CreateLayout() => new(
            new[]
            {
                Room("n_start", 0f, 0f), Room("n_objective", 2f, 0f), Room("n_exit", 4f, 0f), Room("n_side", 0f, 2f)
            },
            new[]
            {
                new DoorConnection("n_start", "east", "n_objective", "west", true),
                new DoorConnection("n_objective", "east", "n_exit", "west", true),
                new DoorConnection("n_start", "north", "n_side", "south", false)
            },
            "n_start",
            new[] { "n_exit" });

        private static PlacedRoom Room(string nodeId, float x, float y) =>
            new(nodeId, $"template_{nodeId}", new GridPose(x, y, 0),
                new Dictionary<string, DoorState>(StringComparer.Ordinal));

        private RouteHintFloorView CreateView()
        {
            GameObject go = new("RouteHintFloorView");
            _created.Add(go);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            ParticleSystem sparkles = new GameObject("Stars").AddComponent<ParticleSystem>();
            sparkles.transform.SetParent(go.transform, false);
            Shader shader = Shader.Find(RouteHintFloorView.StarsShaderName);
            Assert.NotNull(shader, "The star shader must be imported.");
            Material material = new(shader);
            _created.Add(material);
            sparkles.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            RouteHintFloorView view = go.AddComponent<RouteHintFloorView>();
            view.SetSparklesForTests(sparkles);
            return view;
        }
    }
}
