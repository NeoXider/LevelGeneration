using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
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
    /// Стражи цены кадра мини-карты (WebGL, один поток, чувствительный к мусору GC): работа, которая раньше шла
    /// на каждый кадр или каждое событие IMGUI, теперь идёт один раз на смену состояния. Считаем вызовы, а не
    /// время: счётчик падает предсказуемо, тайминг — нет.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelMinimapRuntimeCostTests
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
        public void OnGui_DrawsOnRepaintOnly()
        {
            Assert.IsTrue(LevelMinimapController.IsDrawEvent(new Event { type = EventType.Repaint }));
            Assert.IsFalse(LevelMinimapController.IsDrawEvent(new Event { type = EventType.Layout }),
                "The map has no IMGUI controls: Layout is wasted work.");
            Assert.IsFalse(LevelMinimapController.IsDrawEvent(new Event { type = EventType.MouseDown }));
            Assert.IsFalse(LevelMinimapController.IsDrawEvent(new Event { type = EventType.KeyDown }));
            Assert.IsFalse(LevelMinimapController.IsDrawEvent(null));
        }

        [Test]
        public void LookYaw_FindsThePlayerCameraOnce_AndFollowsItsRotation()
        {
            LevelMinimapController controller = CreateController(out _);
            GameObject player = Create("Player");
            GameObject look = new("Look");
            look.transform.SetParent(player.transform, false);
            look.AddComponent<Camera>();
            controller.PlayerTransform = player.transform;

            for (int frame = 0; frame < 50; frame++)
            {
                look.transform.localRotation = Quaternion.Euler(0f, frame * 7f, 0f);
                Assert.AreEqual(frame * 7f % 360f, controller.ResolveLookYaw(), 0.01f);
            }

            Assert.AreEqual(1, controller.LookCameraSearchCount,
                "The look camera is found once, not by a hierarchy walk on every yaw request.");
        }

        [Test]
        public void RoomGeometry_IsComputedOncePerRoomPerBind()
        {
            LevelMinimapController controller = CreateController(out _);
            controller.Bind(CreateLayout());
            IReadOnlyList<MinimapRoomSnapshot> rooms = controller.Session.GetAllRooms();
            Rect rect = new(0f, 0f, 900f, 520f);

            for (int frame = 0; frame < 30; frame++)
            {
                Assert.IsTrue(controller.TryBuildProjection(rooms, rect, true, out _));
                Assert.IsTrue(controller.TryBuildProjection(rooms, rect, false, out _));
            }

            Assert.AreEqual(rooms.Count, controller.RoomGeometryBuildCount,
                "Corners, centre and merged walls of a room do not change: one computation per room.");
        }

        [Test]
        public void FragmentCapsules_ChildNamesAreReadOnlyWhenARoomChanges()
        {
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            UnityEditor.SerializedObject serialized = new(settings);
            serialized.FindProperty("collectibleMarkerNamePrefix").stringValue = "Collectible_";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            GameObject roomGo = Create("Room_n_start");
            new GameObject("Decor_A").transform.SetParent(roomGo.transform, false);
            new GameObject("Decor_B").transform.SetParent(roomGo.transform, false);
            new GameObject("Collectible_a").transform.SetParent(roomGo.transform, false);
            RoomInstance room = roomGo.AddComponent<RoomInstance>();
            room.Configure("n_start", RoomKind.Start, roomGo.AddComponent<RoomTemplate>());
            controller.Bind(CreateLayout(), null, new Dictionary<string, RoomInstance> { ["n_start"] = room });

            controller.RefreshFragmentCapsuleCache(0f);
            Assert.AreEqual(1, controller.FragmentCapsuleCount);
            int reads = controller.FragmentCapsuleNameReadCount;
            controller.RefreshFragmentCapsuleCache(1.5f);
            controller.RefreshFragmentCapsuleCache(3f);

            Assert.AreEqual(1, controller.FragmentCapsuleCount);
            Assert.AreEqual(reads, controller.FragmentCapsuleNameReadCount,
                "The room did not change: its children's names (a new string each) are not read again.");

            GameObject spawned = new("Collectible_b");
            spawned.transform.SetParent(roomGo.transform, false);
            controller.RefreshFragmentCapsuleCache(4.5f);
            Assert.AreEqual(2, controller.FragmentCapsuleCount, "A spawned capsule is picked up on the next scan.");

            spawned.SetActive(false);
            int afterSpawn = controller.FragmentCapsuleNameReadCount;
            controller.RefreshFragmentCapsuleCache(6f);
            Assert.AreEqual(2, controller.FragmentCapsuleCount, "A collected capsule only goes dark: still on the map.");
            Assert.AreEqual(afterSpawn, controller.FragmentCapsuleNameReadCount);
        }

        [Test]
        public void RouteHintDiagnosticText_IsBuiltOnlyWhenLoggedOrRead_WithTheSameTextAsTheLog()
        {
            LevelMinimapController controller = CreateController(out _);
            controller.Bind(CreateLayout());
            List<string> logged = new();
            LevelGenerationRuntimeLog.Sink previous = LevelGenerationRuntimeLog.InfoSink;
            LevelGenerationRuntimeLog.InfoSink = (message, _, _) => logged.Add(message);
            try
            {
                for (int frame = 0; frame < 100; frame++)
                {
                    controller.UpdateRouteHintDiagnostics(MinimapGameplay.Context, RouteHintPresence.Exploring, 0.1f,
                        0.1f, frame * 0.1f);
                }

                Assert.AreEqual(1, logged.Count, "Precondition: one state line, nothing else changed.");
                Assert.AreEqual(1, controller.RouteHintDiagnosticBuildCount,
                    "Ten seconds without a reader and without a state change: the line was formatted once, for the log.");

                string text = controller.RouteHintDiagnosticText;
                string again = controller.RouteHintDiagnosticText;

                Assert.AreSame(text, again, "A read within the refresh interval reuses the line.");
                Assert.AreEqual(2, controller.RouteHintDiagnosticBuildCount);
                Assert.AreEqual("Route hint state changed: " + text, logged[0],
                    "The log on a state change keeps exactly the same text.");
            }
            finally
            {
                LevelGenerationRuntimeLog.InfoSink = previous;
            }
        }

        [Test]
        public void FragmentMarkerSprites_AreBakedOnce_AndReadAsTheMarker()
        {
            LevelMinimapController controller = CreateController(out _);
            Texture2D uncollected = controller.FragmentMarkerTextureForTests(true, true);
            Texture2D collected = controller.FragmentMarkerTextureForTests(true, false);

            Assert.AreSame(uncollected, controller.FragmentMarkerTextureForTests(true, true),
                "One baked sprite per map, drawn with one DrawTexture per marker.");
            Assert.AreNotSame(uncollected, collected);
            Assert.IsFalse(uncollected.isReadable,
                "The map only draws the sprite: its CPU copy is released after the upload.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("Reading the baked sprite back needs a graphics device (-nographics run).");
            }

            Texture2D drawnUncollected = ReadBackFromGpu(uncollected);
            Texture2D drawnCollected = ReadBackFromGpu(collected);
            Color fill = drawnUncollected.GetPixel(drawnUncollected.width / 2, drawnUncollected.height / 2);
            Color hollow = drawnCollected.GetPixel(drawnCollected.width / 2, drawnCollected.height / 2);
            Assert.Greater(fill.g, 0.9f, "An uncollected capsule is a filled green disc.");
            Assert.Less(hollow.g, 0.2f, "A collected one is dark inside (only its rim is light).");
            Assert.AreEqual(0f, drawnUncollected.GetPixel(0, 0).a, 0.01f, "Outside the disc the sprite is transparent.");
        }

        /// <summary>
        /// Пиксели спрайта такими, какими их рисует карта. Сам спрайт нечитаем (<c>Apply(false, true)</c>: копия в
        /// памяти процессора после загрузки на GPU освобождается — карте она не нужна), поэтому он копируется через
        /// RenderTexture в читаемую текстуру теста.
        /// </summary>
        private Texture2D ReadBackFromGpu(Texture2D source)
        {
            RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                Texture2D copy = new(source.width, source.height, TextureFormat.RGBA32, false);
                _created.Add(copy);
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                copy.Apply(false, false);
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        /// <summary>
        /// Пунктир подгоняется только о стены рядом с линией (отбор в мире раз на смену линии), а не о все стены
        /// показанных комнат на каждый кадр и каждую карту, — и подогнанные радиусы точек те же, что прежде.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void RouteHintDots_NearWallSelection_FitsTheSameRadiiAsEveryWall(bool fullMap)
        {
            LevelMinimapController controller = CreateHallController(out LevelMinimapSettings settings, out _, out _);
            Tick(controller, settings.RouteHintIdleSeconds);
            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            controller.Session.ScheduleReveal("n_side", 0f);
            controller.Session.RevealDueRooms(1f);
            IReadOnlyList<MinimapRoomSnapshot> rooms = controller.Session.GetVisibleRooms();
            Rect rect = fullMap ? new Rect(0f, 0f, 1136f, 680f) : new Rect(23.14f, 23.14f, 183.72f, 131.72f);
            Assert.IsTrue(controller.TryBuildProjection(rooms, rect, fullMap,
                out LevelMinimapController.MapProjection projection));
            float sizeScale = fullMap ? settings.RouteHintFullMapSizeMultiplier : 1f;
            float maxRequested = LevelMinimapController.MaxRouteHintRequestedRadiusPixels(settings, sizeScale);

            controller.BuildRouteHintScreenWalls(rooms, projection, maxRequested);
            List<RouteHintScreenWall> near = new(controller.RouteHintScreenWalls);
            List<RouteHintScreenWall> all = new();
            for (int i = 0; i < rooms.Count; i++)
            {
                Assert.IsTrue(controller.RouteHintGeometryForTests.TryGetRoom(rooms[i].NodeId, out RouteHintRoom room));
                foreach (RouteHintWall wall in room.Walls)
                {
                    all.Add(new RouteHintScreenWall(controller.ProjectRouteHintPoint(wall.A, projection),
                        controller.ProjectRouteHintPoint(wall.B, projection),
                        RouteHintScreenClearance.WallStrokeRadiusPixels(wall.WidthMeters, projection.Scale)));
                }
            }

            Assert.Less(near.Count, all.Count, "Precondition: the far walls (side room, hall ends) are skipped.");
            Assert.Greater(controller.BuildRouteHintScreenPath(projection, fullMap), 1f);
            IReadOnlyList<Vector2> path = controller.RouteHintScreenPath;
            int compared = 0;
            for (int i = 1; i < path.Count; i++)
            {
                float length = Vector2.Distance(path[i - 1], path[i]);
                for (float along = 0f; along <= length; along += 0.5f)
                {
                    Vector2 point = Vector2.Lerp(path[i - 1], path[i], length > 0f ? along / length : 0f);
                    foreach (float radius in new[] { maxRequested, maxRequested * 0.6f, 1f })
                    {
                        Assert.AreEqual(RouteHintScreenClearance.FitDotRadius(point, radius, all),
                            RouteHintScreenClearance.FitDotRadius(point, radius, near), $"dot at {point} r={radius}");
                        compared++;
                    }
                }

                Assert.AreEqual(RouteHintScreenClearance.FitLineThickness(path[i - 1], path[i], maxRequested * 2f, all),
                    RouteHintScreenClearance.FitLineThickness(path[i - 1], path[i], maxRequested * 2f, near),
                    $"line segment {i}");
            }

            Assert.Greater(compared, 100, "Precondition: the comparison covered the drawn trail.");
        }

        /// <summary>
        /// Смешивание старой линии с новой меняет точки каждый кадр, но лучи к полу — один раз на перепланирование:
        /// кадры смешивания сдвигают звёзды по XZ на высотах плана. Прежде каждый кадр смешивания прощупывал пол
        /// под каждой точкой линии.
        /// </summary>
        [Test]
        public void FloorRibbon_ProbesTheFloorOncePerReplan_NotPerBlendFrame()
        {
            LevelMinimapController controller = CreateHallController(out LevelMinimapSettings settings,
                out Transform player, out _);
            RouteHintFloorView view = CreateView();
            controller.SetRouteHintFloorView(view);
            Tick(controller, settings.RouteHintIdleSeconds);
            Tick(controller, settings.RouteHintFloorRevealSeconds + 0.05f);
            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, controller.RouteHintRevealPhase,
                "Precondition: the stars are shown.");
            int probes = controller.RouteHintFloorProbeBuildCount;
            int plans = controller.RouteHintPlanCount;

            player.position = new Vector3(3f, 0f, 4.3f);
            Tick(controller, 0.01f);
            Assert.AreEqual(plans + 1, controller.RouteHintPlanCount, "Precondition: he left the trail — one re-plan.");
            Assert.IsTrue(controller.RouteHintTrail.IsBlending, "Precondition: the new line melts into the old one.");
            int revision = controller.RouteHintTrail.Revision;
            for (int frame = 0; frame < 10; frame++)
            {
                Tick(controller, 0.05f);
            }

            Assert.IsFalse(controller.RouteHintTrail.IsBlending, "Precondition: the blend finished.");
            Assert.Greater(controller.RouteHintTrail.Revision, revision + 3, "Precondition: the points moved per frame.");
            Assert.AreEqual(probes + 1, controller.RouteHintFloorProbeBuildCount,
                "One floor probe for the re-plan, none for the blend frames.");
            RouteHintTrail trail = controller.RouteHintTrail;
            Assert.AreEqual(trail.Points.Count, view.Centerline.Count);
            for (int i = 0; i < trail.Points.Count; i++)
            {
                Assert.AreEqual(trail.Points[i].x, view.Centerline[i].x, 0.0001f, $"point {i} x");
                Assert.AreEqual(trail.Points[i].y, view.Centerline[i].z, 0.0001f, $"point {i} z");
            }
        }

        /// <summary>
        /// Пакет не читает устройство ввода сам — ни напрямую, ни через reflection (раньше клавиша `M` искалась
        /// через <c>Type.GetType("UnityEngine.InputSystem.Keyboard…")</c> каждый кадр). Ищем вызовы, а не упоминания:
        /// строки комментариев пропускаются.
        /// </summary>
        [Test]
        public void PackageRuntime_UsesNoReflection()
        {
            GameObject host = Create("ReflectionGuardHost");
            MonoScript script = MonoScript.FromMonoBehaviour(host.AddComponent<LevelMinimapController>());
            string assetPath = AssetDatabase.GetAssetPath(script);
            Assert.IsFalse(string.IsNullOrEmpty(assetPath), "The controller script must be found.");
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            string fullPath = package == null
                ? Path.GetFullPath(assetPath)
                : Path.Combine(package.resolvedPath, assetPath.Substring(package.assetPath.Length).TrimStart('/'));
            string runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(fullPath));
            List<string> offenders = new();
            foreach (string file in Directory.GetFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.Contains("Type.GetType(") || line.Contains("System.Reflection")
                                                       || line.Contains("BindingFlags"))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line}");
                    }
                }
            }

            Assert.IsEmpty(offenders, "Reflection in the package runtime:\n" + string.Join("\n", offenders));
        }

        private void Tick(LevelMinimapController controller, float deltaSeconds)
        {
            _now += deltaSeconds;
            controller.TickFrame(RouteHintPresence.Exploring, false, deltaSeconds, deltaSeconds, _now);
        }

        private GameObject Create(string name)
        {
            GameObject go = new(name);
            _created.Add(go);
            return go;
        }

        private LevelMinimapController CreateController(out LevelMinimapSettings settings)
        {
            GameObject go = Create("MinimapCostTest");
            settings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
            _created.Add(settings);
            LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
            controller.SetSettings(settings);
            return controller;
        }

        /// <summary>Тот же зал 20 м с боковой комнатой, что у тестов общей линии подсказки (реальные пропорции).</summary>
        private LevelMinimapController CreateHallController(out LevelMinimapSettings settings, out Transform player,
            out SimpleRoomEventBus bus)
        {
            LevelMinimapController controller = CreateController(out settings);
            GameObject playerObject = Create("MinimapCostPlayer");
            playerObject.transform.position = new Vector3(2.5f, 0f, 2.5f);
            controller.PlayerTransform = playerObject.transform;
            player = playerObject.transform;
            bus = new SimpleRoomEventBus();
            controller.Bind(CreateHallLayout(), bus, null);
            RouteHintGeometry geometry = controller.RouteHintGeometryForTests;
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
            geometry.AddDoor("n_start", "east", "n_objective", "west", new Vector2(20f, 2.5f),
                RouteHintTestLevels.DoorFrame);
            geometry.AddDoor("n_objective", "east", "n_exit", "west", new Vector2(25f, 2.5f),
                RouteHintTestLevels.DoorFrame);
            geometry.AddDoor("n_start", "north", "n_side", "south", new Vector2(7.5f, 5f),
                RouteHintTestLevels.DoorFrame);
            return controller;
        }

        private RouteHintFloorView CreateView()
        {
            GameObject go = Create("RouteHintFloorView");
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

        private static LevelLayout CreateLayout() => new(
            new[] { Room("n_start", 0f, 0f), Room("n_objective", 2f, 0f), Room("n_exit", 4f, 0f) },
            new[]
            {
                new DoorConnection("n_start", "east", "n_objective", "west", true),
                new DoorConnection("n_objective", "east", "n_exit", "west", true)
            },
            "n_start",
            new[] { "n_exit" });

        private static LevelLayout CreateHallLayout() => new(
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
    }

    /// <summary>Адаптивная загрузка комнат: посещённые комнаты — состояние одной миссии.</summary>
    [Category("LevelGeneration")]
    public sealed class AdaptiveLevelLoadingRebindTests
    {
        [Test]
        public void Bind_NewMission_ForgetsTheRoomsVisitedInThePreviousOne()
        {
            GameObject root = new("adaptive_rebind_test");
            try
            {
                Dictionary<string, RoomInstance> rooms = new(StringComparer.Ordinal);
                foreach (string id in new[] { "a", "b", "c", "d" })
                {
                    GameObject go = new($"Room_{id}");
                    go.transform.SetParent(root.transform, false);
                    RoomInstance room = go.AddComponent<RoomInstance>();
                    room.Configure(id, RoomKind.Passage, null);
                    rooms[id] = room;
                }

                LevelLayout layout = new(
                    new[]
                    {
                        new PlacedRoom("a", "t", new GridPose(0f, 0f, 0), null),
                        new PlacedRoom("b", "t", new GridPose(1f, 0f, 0), null),
                        new PlacedRoom("c", "t", new GridPose(2f, 0f, 0), null),
                        new PlacedRoom("d", "t", new GridPose(3f, 0f, 0), null)
                    },
                    new[]
                    {
                        new DoorConnection("a", "e", "b", "w", true), new DoorConnection("b", "e", "c", "w", true),
                        new DoorConnection("c", "e", "d", "w", true)
                    },
                    "a",
                    new[] { "d" });
                AdaptiveLevelLoadingController loader = root.AddComponent<AdaptiveLevelLoadingController>();
                loader.SetAdaptiveLoadingEnabled(true);
                SimpleRoomEventBus bus = new();
                loader.Bind(layout, rooms, bus);
                bus.Publish(new RoomEnteredEvent("d", RoomKind.Passage));
                Assert.IsTrue(rooms["d"].gameObject.activeSelf, "Precondition: d is visited and active.");

                loader.Bind(layout, rooms, new SimpleRoomEventBus());

                Assert.IsTrue(rooms["a"].gameObject.activeSelf);
                Assert.IsTrue(rooms["b"].gameObject.activeSelf);
                Assert.IsFalse(rooms["d"].gameObject.activeSelf,
                    "A new mission starts with no visited rooms: d is far from the start and must stay unloaded.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
