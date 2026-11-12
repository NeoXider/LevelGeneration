using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using Neoxider.LevelGeneration.Runtime.Application;
using Neoxider.LevelGeneration.Runtime.Infrastructure;
using Neoxider.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Путь подсказки и сплошные предметы на полу. Прежде планировщик знал только стены миникарты: путь резал стул
    /// насквозь и шёл впритык к столам, а с игрока, стоящего на столе, лента поднималась на крышку и «перелезала»
    /// через стулья. Здесь: путь держит запас от стен и предметов и не пересекает контур предмета; с предмета
    /// игрок сначала сходит на ближайшее свободное место пола; лента после схода опускается на пол.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class RouteHintObstacleTests
    {
        private const float WantedClearance = 0.9f;
        private const float Tolerance = 0.02f;

        /// <summary>Как в настройках: запас стены + допуск скругления углов.</summary>
        private static readonly RouteHintPathOptions Clear = new(WantedClearance + 0.12f, 1.4f);

        private static readonly Vector2 WestDoor = new(0f, 5f);

        /// <summary>Стул на прямой между игроком и дверью (сиденье с подлокотниками ~0.8 × 0.86 м).</summary>
        private static readonly RouteHintObstacle Chair = RouteHintObstacle.FromRect(new Rect(4.6f, 4.57f, 0.8f, 0.86f));

        /// <summary>Стол, на котором стоит игрок.</summary>
        private static readonly RouteHintObstacle Table = RouteHintObstacle.FromRect(new Rect(6f, 4f, 2.5f, 2f));

        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
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
        public void Plan_ChairOnTheStraightLine_PathGoesAroundIt_KeepingTheClearanceFromChairAndWalls()
        {
            RouteHintGeometry geometry = HallWith(Chair);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, new Vector2(8f, 5f),
                "hall", RouteHintTestLevels.HallWestDoor(), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            for (int i = 1; i < path.Count; i++)
            {
                float toChair = SegmentToObstacleDistance(path[i - 1], path[i], Chair);
                Assert.GreaterOrEqual(toChair, Clear.ClearanceMeters - Tolerance,
                    $"Segment {i} {path[i - 1]}→{path[i]} keeps the clearance from the chair (and never crosses it).");
                if (Vector2.Distance(path[i - 1], WestDoor) > 1.6f && Vector2.Distance(path[i], WestDoor) > 1.6f)
                {
                    Assert.GreaterOrEqual(SegmentToWallSurface(path[i - 1], path[i]), Clear.ClearanceMeters - Tolerance,
                        $"Segment {i} keeps the clearance from the walls away from the doorway.");
                }
            }

            Assert.AreEqual(WestDoor, path[path.Count - 1]);
        }

        [Test]
        public void Plan_LegacyNarrowSearch_NeverCrossesTheChairFootprint()
        {
            RouteHintGeometry geometry = HallWith(Chair);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, new Vector2(8f, 5f),
                "hall", RouteHintTestLevels.HallWestDoor(), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            for (int i = 1; i < path.Count; i++)
            {
                Assert.Greater(SegmentToObstacleDistance(path[i - 1], path[i], Chair), 0f,
                    $"Even the last-resort search never crosses the chair: segment {i} {path[i - 1]}→{path[i]}.");
            }
        }

        [Test]
        public void Plan_PlayerStandingOnATable_StepsDownToTheNearestFreeFloor_ThenGoesAroundTheProps()
        {
            RouteHintGeometry geometry = HallWith(Table, Chair);
            Vector2 student = new(7.2f, 5f);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, student, "hall",
                RouteHintTestLevels.HallWestDoor(), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "A student standing on a table still gets a route — from the floor next to the table.");
            Assert.AreEqual(student, path[0], "The route starts where the student really stands.");
            Assert.IsFalse(Table.Contains(path[1]), "The first step leaves the table.");
            Assert.AreEqual(1f + 0.35f, Vector2.Distance(student, path[1]), 0.05f,
                "It steps down at the NEAREST free spot: just past the closest edge of the table (1 m away).");
            for (int i = 2; i < path.Count; i++)
            {
                Assert.Greater(SegmentToObstacleDistance(path[i - 1], path[i], Table), 0f,
                    $"Once down, the route never crosses the table again (segment {i}).");
                Assert.GreaterOrEqual(SegmentToObstacleDistance(path[i - 1], path[i], Chair),
                    Clear.ClearanceMeters - Tolerance, $"And it keeps the clearance from the chair (segment {i}).");
            }

            for (int i = 3; i < path.Count; i++)
            {
                Assert.GreaterOrEqual(SegmentToObstacleDistance(path[i - 1], path[i], Table),
                    Clear.ClearanceMeters - Tolerance,
                    $"After stepping clear of the table the route keeps the clearance from it too (segment {i}).");
            }
        }

        /// <summary>
        /// Скамья придвинута к столу, игрок стоит на столе у её края. Ближайший свободный угол — за скамьёй, и прежде
        /// побеждал он: первый отрезок от игрока до точки схода проверялся только по концу и шёл сквозь скамью
        /// (лента опускалась на её сиденье). Теперь сход проверяется всем отрезком: пересечь можно только край
        /// своего стола.
        /// </summary>
        [Test]
        public void Plan_PlayerOnATableWithABenchAgainstIt_FirstStepDoesNotCrossTheBench()
        {
            RouteHintObstacle bench = RouteHintObstacle.FromRect(new Rect(6.6f, 3.2f, 1.8f, 0.8f));
            RouteHintGeometry geometry = HallWith(Table, bench);
            Vector2 student = new(7.5f, 4.05f);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, student, "hall",
                RouteHintTestLevels.HallWestDoor(), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Assert.AreEqual(student, path[0]);
            Assert.IsFalse(Table.Contains(path[1]) || bench.Contains(path[1]), "The first step lands on free floor.");
            for (int i = 1; i < path.Count; i++)
            {
                Assert.Greater(SegmentToObstacleDistance(path[i - 1], path[i], bench), 0f,
                    $"No segment crosses the bench, the first step off the table included (segment {i} " +
                    $"{path[i - 1]}→{path[i]}).");
            }
        }

        [Test]
        public void StepOff_RingFallback_DoesNotStepThroughAnotherProp()
        {
            // Стол в углу у западной стены, вплотную к нему с востока — шкаф: краевые и угловые точки схода
            // заняты, работает запасной круг. Его кандидаты за шкафом ближе прочих, но отрезок к ним идёт сквозь
            // шкаф — сход выбирает свободную сторону.
            RouteHintObstacle cornerTable = RouteHintObstacle.FromRect(new Rect(0.15f, 0.15f, 1.6f, 1.6f));
            RouteHintObstacle cabinet = RouteHintObstacle.FromRect(new Rect(1.75f, 0.15f, 0.6f, 2.6f));
            RouteHintGeometry geometry = HallWith(cornerTable, cabinet);
            Assert.IsTrue(geometry.TryGetRoom("hall", out RouteHintRoom hall));
            Vector2 student = new(1.6f, 1f);

            Assert.IsTrue(PlanRouteHintPathUseCase.TryStepOffObstacle(hall, student, out Vector2 floor));

            Assert.IsFalse(cornerTable.Contains(floor) || cabinet.Contains(floor));
            Assert.Greater(SegmentToObstacleDistance(student, floor, cabinet), 0f,
                $"The step {student}→{floor} must not pass through the cabinet.");
        }

        /// <summary>
        /// Обставленная комната: 40 предметов. Каждый добавляет в граф видимости не больше четырёх точек обхода —
        /// по одной на угол. Прежде предмет получал по четыре точки на сторону (шестнадцать на коробку), а граф
        /// с попарной проверкой отрезков рос квадратично: первая подсказка в такой комнате подвешивала WebGL.
        /// </summary>
        [Test]
        public void FurnishedRoom_EachPropAddsAtMostFourDetourPoints()
        {
            RouteHintObstacle[] props = new RouteHintObstacle[40];
            for (int i = 0; i < props.Length; i++)
            {
                props[i] = RouteHintObstacle.FromRect(new Rect(1.6f + i % 8 * 1f, 0.9f + i / 8 * 0.9f, 0.3f, 0.3f));
            }

            RouteHintGeometry empty = HallWith();
            RouteHintGeometry furnished = HallWith(props);
            Vector2 student = new(9f, 8.5f);

            Assert.AreEqual(RouteHintPathOutcome.Planned, PlanRouteHintPathUseCase.Execute(empty, _ => true, student,
                "hall", RouteHintTestLevels.HallWestDoor(), new List<Vector2>()));
            List<Vector2> path = new();
            Assert.AreEqual(RouteHintPathOutcome.Planned, PlanRouteHintPathUseCase.Execute(furnished, _ => true,
                student, "hall", RouteHintTestLevels.HallWestDoor(), path),
                "The free upper half of the hall leads to the west door.");
            Assert.IsTrue(empty.TryGetRoom("hall", out RouteHintRoom emptyHall));
            Assert.IsTrue(furnished.TryGetRoom("hall", out RouteHintRoom furnishedHall));
            // В пустом зале путь идёт по прямой и граф не нужен — строим оба графа явно, чтобы сравнить размеры.
            PlanRouteHintPathUseCase.EnsureNavigationGraph(emptyHall);
            PlanRouteHintPathUseCase.EnsureNavigationGraph(furnishedHall);

            int added = furnishedHall.NavigationPoints.Count - emptyHall.NavigationPoints.Count;
            Assert.LessOrEqual(added, 4 * props.Length, "One detour point per prop corner, not four per side.");
            Assert.Greater(added, 0, "Props still get detour points: the planner needs them to go around.");
            for (int i = 1; i < path.Count; i++)
            {
                for (int p = 0; p < props.Length; p++)
                {
                    Assert.Greater(SegmentToObstacleDistance(path[i - 1], path[i], props[p]), 0f,
                        $"Segment {i} must not cross prop {p}.");
                }
            }
        }

        [Test]
        public void CornerDetour_KeepsTheOffsetFromBothSidesOfTheCorner()
        {
            RouteHintObstacle box = RouteHintObstacle.FromRect(new Rect(2f, 3f, 1f, 0.5f));
            for (int corner = 0; corner < 4; corner++)
            {
                Assert.IsTrue(PlanRouteHintPathUseCase.TryObstacleCornerDetour(box, corner, 0.35f, out Vector2 detour));
                Assert.IsFalse(box.Contains(detour), $"Corner {corner}: the detour point lies outside the prop.");
                Vector2 point = box.Corner(corner);
                Assert.AreEqual(0.35f, Mathf.Abs(detour.x - point.x), 0.001f, $"Corner {corner}: offset from the side.");
                Assert.AreEqual(0.35f, Mathf.Abs(detour.y - point.y), 0.001f, $"Corner {corner}: offset from the end.");
            }
        }

        /// <summary>
        /// Стол обставлен скамьями со всех сторон: чистого шага нет ни через край, ни запасным кругом. Подсказка не
        /// пропадает — берётся ближайший свободный пол, пусть шаг и пересекает скамью.
        /// </summary>
        [Test]
        public void StepOff_TableSurroundedOnAllSides_StillFindsFloor()
        {
            RouteHintObstacle table = RouteHintObstacle.FromRect(new Rect(4f, 4f, 2f, 2f));
            RouteHintObstacle north = RouteHintObstacle.FromRect(new Rect(3.4f, 6f, 3.2f, 0.6f));
            RouteHintObstacle south = RouteHintObstacle.FromRect(new Rect(3.4f, 3.4f, 3.2f, 0.6f));
            RouteHintObstacle west = RouteHintObstacle.FromRect(new Rect(3.4f, 4f, 0.6f, 2f));
            RouteHintObstacle east = RouteHintObstacle.FromRect(new Rect(6f, 4f, 0.6f, 2f));
            RouteHintGeometry geometry = HallWith(table, north, south, west, east);
            Assert.IsTrue(geometry.TryGetRoom("hall", out RouteHintRoom hall));

            Assert.IsTrue(PlanRouteHintPathUseCase.TryStepOffObstacle(hall, new Vector2(5f, 5f), out Vector2 floor),
                "With no clear step the nearest free floor is still taken.");
            Assert.IsFalse(hall.IsInsideObstacle(floor), $"The step lands on free floor, not on a prop: {floor}.");
        }

        [Test]
        public void StepOff_TableAgainstAWall_PicksAFreeSideInsteadOfTheGapAtTheWall()
        {
            RouteHintObstacle tableAtWall = RouteHintObstacle.FromRect(new Rect(3f, 0.15f, 3f, 1.6f));
            RouteHintGeometry geometry = HallWith(tableAtWall);
            Assert.IsTrue(geometry.TryGetRoom("hall", out RouteHintRoom hall));

            Assert.IsTrue(PlanRouteHintPathUseCase.TryStepOffObstacle(hall, new Vector2(4.5f, 0.4f), out Vector2 floor));

            Assert.IsFalse(tableAtWall.Contains(floor));
            Assert.Greater(floor.y, 1.75f - 0.01f,
                "Between the table and the wall there is no floor to step on: the student steps off the free side.");
        }

        [Test]
        public void Obstacle_StandingInADoorway_IsNotABlocker()
        {
            RouteHintObstacle doorLeaf = RouteHintObstacle.FromRect(new Rect(-0.1f, 4.5f, 0.2f, 1f));
            RouteHintGeometry geometry = HallWith(doorLeaf);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, _ => true, new Vector2(8f, 5f),
                "hall", RouteHintTestLevels.HallWestDoor(), path, null, Clear);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "A collider in the doorway belongs to the door: the layout says the doorway is a passage.");
            Assert.IsTrue(geometry.TryGetRoom("hall", out RouteHintRoom hall));
            Assert.IsEmpty(hall.Obstacles);
        }

        [TestCase(true, 0f, 1.1f, 0.7f, 100f, RouteHintColliderRole.Obstacle)]
        [TestCase(true, -0.2f, 0.02f, 90f, 100f, RouteHintColliderRole.Walkable)]
        [TestCase(true, 2.4f, 2.6f, 0.5f, 100f, RouteHintColliderRole.Overhead)]
        [TestCase(true, 0f, 3f, 80f, 100f, RouteHintColliderRole.TooLarge)]
        [TestCase(false, 0f, 1.1f, 0.7f, 100f, RouteHintColliderRole.Ignored)]
        public void Classify_ColliderRole_ByHeightsAndArea(bool solid, float bottom, float top, float area,
            float roomArea, RouteHintColliderRole expected)
        {
            Assert.AreEqual(expected, ClassifyRouteHintColliderUseCase.Execute(solid, bottom, top, area, roomArea, 0.35f));
        }

        [Test]
        public void Collector_TakesSolidPropsOnly_WithTheirPlanOutlines()
        {
            GameObject room = Create("Room");
            CreateBox(room.transform, "Floor", new Vector3(5f, -0.1f, 5f), new Vector3(10f, 0.2f, 10f), 0f);
            BoxCollider chair = CreateBox(room.transform, "Chair", new Vector3(5f, 0.55f, 5f),
                new Vector3(0.8f, 1.1f, 0.86f), 0f);
            CreateBox(room.transform, "RotatedChair", new Vector3(2f, 0.55f, 2f), new Vector3(1f, 1.1f, 1f), 45f);
            CreateBox(room.transform, "Lamp", new Vector3(5f, 2.6f, 5f), new Vector3(0.5f, 0.3f, 0.5f), 0f);
            CreateBox(room.transform, "Trigger", new Vector3(8f, 1f, 8f), new Vector3(1f, 2f, 1f), 0f).isTrigger = true;
            CreateBox(room.transform, "Shell", new Vector3(5f, 1.5f, 5f), new Vector3(10f, 3f, 10f), 0f);
            GameObject walls = new("Walls");
            walls.transform.SetParent(room.transform, false);
            CreateBox(walls.transform, "WallNorth", new Vector3(5f, 1.5f, 10f), new Vector3(10f, 3f, 0.3f), 0f);
            GameObject door = new("Door");
            door.transform.SetParent(room.transform, false);
            door.AddComponent<DoorSocket>();
            CreateBox(door.transform, "Leaf", new Vector3(0f, 1f, 5f), new Vector3(0.1f, 2f, 1f), 0f);
            GameObject hidden = new("HiddenProp");
            hidden.transform.SetParent(room.transform, false);
            CreateBox(hidden.transform, "HiddenBox", new Vector3(8f, 0.5f, 2f), new Vector3(1f, 1f, 1f), 0f);
            hidden.SetActive(false);
            room.SetActive(false);
            List<RouteHintObstacle> obstacles = new();

            RouteHintObstacleCollector.Collect(room.transform, 0f, 100f, 0.35f, new[] { walls.transform }, obstacles);

            Assert.AreEqual(2, obstacles.Count,
                "Only the two chairs: not the floor, the lamp overhead, the trigger, the room shell, the wall, the door "
                + "or a switched-off prop. A room switched off by adaptive loading still gives its props "
                + "(shape data, not Collider.bounds).");
            RouteHintObstacle straight = obstacles[0].Contains(new Vector2(5f, 5f)) ? obstacles[0] : obstacles[1];
            RouteHintObstacle rotated = obstacles[0].Contains(new Vector2(5f, 5f)) ? obstacles[1] : obstacles[0];
            Assert.AreEqual(0.8f * 0.86f, straight.Area, 0.001f);
            Assert.IsTrue(straight.Contains(new Vector2(5.39f, 5.42f)));
            Assert.IsFalse(straight.Contains(new Vector2(5.41f, 5f)));
            Assert.AreEqual(1f, rotated.Area, 0.001f, "A rotated chair keeps its own outline, not its world bounds.");
            Assert.IsTrue(rotated.Contains(new Vector2(2f, 2f + 0.69f)), "The rotated square reaches √½ m along z.");
            Assert.IsFalse(rotated.Contains(new Vector2(2.49f, 2.49f)), "…but not the corner of its world bounds.");
            Assert.NotNull(chair);
        }

        [Test]
        public void Controller_ChairInTheRoom_TheTrailNeverCrossesIt()
        {
            GameObject hallGo = Create("Room_hall");
            CreateBox(hallGo.transform, "Chair", new Vector3(5f, 0.55f, 5f), new Vector3(0.8f, 1.1f, 0.86f), 0f);
            GameObject doorGo = new("Door_east");
            doorGo.transform.SetParent(hallGo.transform, false);
            doorGo.transform.position = new Vector3(10f, 0f, 5f);
            DoorSocket east = doorGo.AddComponent<DoorSocket>();
            SerializedObject doorSo = new(east);
            doorSo.FindProperty("socketId").stringValue = "east";
            doorSo.ApplyModifiedPropertiesWithoutUndo();
            east.ApplyGeneratedState(DoorState.Closed);
            RoomTemplate template = hallGo.AddComponent<RoomTemplate>();
            RoomInstance hall = hallGo.AddComponent<RoomInstance>();
            hall.Configure("hall", RoomKind.Start, template);
            LevelLayout layout = new(new[]
                {
                    WorldRoom("hall", 0f, 2), WorldRoom("next", 10f, 1)
                },
                new[] { new DoorConnection("hall", "east", "next", "west", true) }, "hall", new[] { "next" });
            LevelMinimapController controller = CreateController(out LevelMinimapSettings settings);
            GameObject player = Create("Player");
            player.transform.position = new Vector3(2f, 0f, 5f);
            controller.PlayerTransform = player.transform;
            controller.Bind(layout, null, new Dictionary<string, RoomInstance> { ["hall"] = hall });

            controller.UpdateRouteHint(settings.RouteHintIdleSeconds, 0f, true);

            Assert.AreEqual(RouteHintPathOutcome.Planned, controller.LastRouteHintPathOutcome);
            RouteHintObstacle chair = RouteHintObstacle.FromRect(new Rect(4.6f, 4.57f, 0.8f, 0.86f));
            IReadOnlyList<Vector2> trail = controller.RouteHintWorldPath;
            for (int i = 1; i < trail.Count; i++)
            {
                Assert.GreaterOrEqual(SegmentToObstacleDistance(trail[i - 1], trail[i], chair),
                    settings.RouteHintPathWallClearanceMeters - 0.05f,
                    $"Trail segment {i} {trail[i - 1]}→{trail[i]} goes around the chair, not through it.");
            }
        }

        [TestCase(true, 0f, 0.75f, 0.35f, 0f)]
        [TestCase(true, 0.4f, 1.2f, 0.35f, 0.4f)]
        public void ResolveFloorHeight_ADropBelowThePreviousPoint_IsTheFloorSteppedOnto(bool hit, float hitHeight,
            float previous, float maxStep, float expected)
        {
            Assert.AreEqual(expected, ResolveRouteHintFloorHeightUseCase.Execute(hit, hitHeight, previous, maxStep),
                0.0001f, "Stepping off a table or a box lands on the floor below: a drop is always accepted.");
        }

        [Test]
        public void Ribbon_StartedOnATable_DropsToTheFloorAndNeverClimbsAChair()
        {
            List<Vector2> axis = new();
            for (float x = 0f; x <= 6.001f; x += 0.25f)
            {
                axis.Add(new Vector2(x, 0f));
            }

            RouteHintRibbonShape shape = new(0.5f, 1f, 0.12f, 0.25f, 0f, 0.35f);
            RouteHintRibbonMeshData ribbon = new();

            Assert.IsTrue(RouteHintRibbonMeshBuilder.BuildFromAxis(axis, shape, 0.75f, TableThenChair, ribbon,
                out RouteHintRibbonFailure _));

            foreach (Vector3 point in ribbon.Centerline)
            {
                float expected = point.x < 1f ? 0.75f : 0f;
                Assert.AreEqual(expected, point.y, 0.001f,
                    $"At x={point.x:0.00}: on the table at the start, then on the floor — under the chair too.");
            }
        }

        /// <summary>Стол 0.75 м до x = 1, стул 0.45 м на x ∈ [3, 3.8], иначе пол.</summary>
        private static bool TableThenChair(Vector2 point, float fromFloorHeight, out float hitHeight)
        {
            hitHeight = point.x < 1f ? 0.75f : point.x >= 3f && point.x <= 3.8f ? 0.45f : 0f;
            return true;
        }

        private static RouteHintGeometry HallWith(params RouteHintObstacle[] obstacles)
        {
            RouteHintGeometry geometry = RouteHintTestLevels.EmptyHall();
            for (int i = 0; i < obstacles.Length; i++)
            {
                geometry.AddObstacle("hall", obstacles[i]);
            }

            return geometry;
        }

        private static float SegmentToWallSurface(Vector2 a, Vector2 b)
        {
            float best = float.MaxValue;
            RouteHintWall[] walls = RouteHintTestLevels.Box(0f, 0f, 10f, 10f);
            for (int i = 0; i < walls.Length; i++)
            {
                best = Mathf.Min(best, Mathf.Sqrt(SegmentsDistanceSquared(a, b, walls[i].A, walls[i].B))
                                       - walls[i].WidthMeters * 0.5f);
            }

            return best;
        }

        /// <summary>Расстояние от отрезка до контура предмета: 0, если отрезок заходит внутрь или пересекает его.</summary>
        private static float SegmentToObstacleDistance(Vector2 a, Vector2 b, RouteHintObstacle obstacle)
        {
            if (obstacle.Contains(a) || obstacle.Contains(b))
            {
                return 0f;
            }

            float best = float.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                best = Mathf.Min(best, SegmentsDistanceSquared(a, b, obstacle.Corner(i), obstacle.Corner(i + 1)));
            }

            return Mathf.Sqrt(best);
        }

        private static float SegmentsDistanceSquared(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 ab = b - a;
            Vector2 cd = d - c;
            float denominator = ab.x * cd.y - ab.y * cd.x;
            if (Mathf.Abs(denominator) > 0.000001f)
            {
                Vector2 ca = c - a;
                float t = (ca.x * cd.y - ca.y * cd.x) / denominator;
                float u = (ca.x * ab.y - ca.y * ab.x) / denominator;
                if (t >= 0f && t <= 1f && u >= 0f && u <= 1f)
                {
                    return 0f;
                }
            }

            return Mathf.Min(Mathf.Min(PointSegment(a, c, d), PointSegment(b, c, d)),
                Mathf.Min(PointSegment(c, a, b), PointSegment(d, a, b)));
        }

        private static float PointSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 span = b - a;
            float t = span.sqrMagnitude > 0.000001f ? Mathf.Clamp01(Vector2.Dot(point - a, span) / span.sqrMagnitude) : 0f;
            return (point - a - span * t).sqrMagnitude;
        }

        private GameObject Create(string name)
        {
            GameObject go = new(name);
            _created.Add(go);
            return go;
        }

        private static BoxCollider CreateBox(Transform parent, string name, Vector3 position, Vector3 size, float yaw)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = size;
            return box;
        }

        private LevelMinimapController CreateController(out LevelMinimapSettings settings)
        {
            GameObject go = Create("MinimapObstacleTest");
            settings = ScriptableObject.CreateInstance<LevelMinimapSettings>();
            _created.Add(settings);
            LevelMinimapController controller = go.AddComponent<LevelMinimapController>();
            controller.SetSettings(settings);
            return controller;
        }

        /// <summary>Комната мировой позы: клетки по 5 м, квадрат <paramref name="cells"/> × <paramref name="cells"/>.</summary>
        private static PlacedRoom WorldRoom(string nodeId, float x, int cells) => new(nodeId, $"template_{nodeId}",
            new GridPose(0f, 0f, 0), new Dictionary<string, DoorState>(), new RoomWorldPose(x, 0f, 0f, 0f, 0f, 0f, 1f),
            new RoomFootprint(cells, cells));
    }
}
