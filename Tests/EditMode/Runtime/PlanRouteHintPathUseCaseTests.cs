using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Runtime.Application;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Линия подсказки: от игрока через раскрытые комнаты к двери следующей комнаты пути, по полу
    /// комнат, а не сквозь стены и не через нераскрытое.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class PlanRouteHintPathUseCaseTests
    {
        private const float Cell = 4f;

        [Test]
        public void Plan_LeadsFromPlayerThroughRevealedDoorsToTargetDoor()
        {
            RouteHintGeometry geometry = CreateCorridor();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(
                geometry,
                Revealed("a", "b"),
                new Vector2(1f, 2f),
                "a",
                new CriticalPathDoor("b", "b_e", "c", "c_w"),
                path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            AssertPoint(new Vector2(1f, 2f), path[0]);
            AssertPoint(new Vector2(8f, 2f), path[path.Count - 1]);
            Assert.IsTrue(ContainsPoint(path, new Vector2(4f, 2f)), "путь обязан пройти через дверь a|b");
        }

        [Test]
        public void Plan_TargetBehindUnrevealedRoom_IsNotDrawn()
        {
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(
                CreateCorridor(),
                Revealed("a"),
                new Vector2(1f, 2f),
                "a",
                new CriticalPathDoor("b", "b_e", "c", "c_w"),
                path);

            Assert.AreEqual(RouteHintPathOutcome.TargetRoomHidden, outcome);
            Assert.AreEqual(0, path.Count);
        }

        [Test]
        public void Plan_NeverTransitsThroughUnrevealedRoom()
        {
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(
                CreateCorridor(),
                Revealed("a", "c"),
                new Vector2(1f, 2f),
                "a",
                new CriticalPathDoor("c", "c_e", "d", "d_w"),
                path);

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, outcome);
        }

        [Test]
        public void Plan_PlayerOutsideRevealedRooms_WithoutCurrentRoom_IsUnknown()
        {
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(
                CreateCorridor(),
                Revealed("a", "b"),
                new Vector2(100f, 100f),
                null,
                new CriticalPathDoor("b", "b_e", "c", "c_w"),
                path);

            Assert.AreEqual(RouteHintPathOutcome.PlayerRoomUnknown, outcome);
        }

        [Test]
        public void Plan_UnknownTargetDoor_IsGeometryDefect()
        {
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(
                CreateCorridor(),
                Revealed("a", "b"),
                new Vector2(1f, 2f),
                "a",
                new CriticalPathDoor("b", "nope", "zzz", "nope"),
                path);

            Assert.AreEqual(RouteHintPathOutcome.MissingGeometry, outcome);
        }

        [Test]
        public void Plan_InsideLShapedRoom_BendsAroundTheCorner_InsteadOfCuttingThroughTheWall()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("l", Vector2.zero, 0f, new[]
            {
                new MinimapCellSnapshot(0f, 0f, Cell, Cell),
                new MinimapCellSnapshot(Cell, 0f, Cell, Cell),
                new MinimapCellSnapshot(0f, Cell, Cell, Cell)
            });
            geometry.AddRoom("n", new Vector2(0f, 2f * Cell), 0f, new[] { new MinimapCellSnapshot(0f, 0f, Cell, Cell) });
            geometry.AddDoor("l", "l_n", "n", "n_s", new Vector2(2f, 8f));
            geometry.TryGetRoom("l", out RouteHintRoom room);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(
                geometry,
                Revealed("l"),
                new Vector2(7f, 3f),
                "l",
                new CriticalPathDoor("l", "l_n", "n", "n_s"),
                path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Assert.IsFalse(PlanRouteHintPathUseCase.IsSegmentInside(room, new Vector2(7f, 3f), new Vector2(2f, 8f)),
                "исходная прямая режет стену — иначе тест ничего не проверяет");
            Assert.GreaterOrEqual(path.Count, 3);
            for (int i = 0; i + 1 < path.Count; i++)
            {
                Assert.IsTrue(PlanRouteHintPathUseCase.IsSegmentInside(room, path[i], path[i + 1]),
                    $"отрезок {path[i]} → {path[i + 1]} выходит за пол комнаты");
            }

            AssertPoint(new Vector2(2f, 8f), path[path.Count - 1]);
        }

        [Test]
        public void Plan_InternalWallInsideOneFloorCell_UsesOpeningAtWallEnd()
        {
            RouteHintGeometry geometry = new();
            RouteHintWall wall = new(new Vector2(4f, 0f), new Vector2(4f, 6f), 0.2f);
            geometry.AddRoom("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) }, new[] { wall });
            geometry.AddRoom("next", new Vector2(8f, 0f), 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) });
            geometry.AddDoor("room", "east", "next", "west", new Vector2(8f, 2f));
            geometry.TryGetRoom("room", out RouteHintRoom room);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("room"),
                new Vector2(1f, 2f), "room", new CriticalPathDoor("room", "east", "next", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Assert.Greater(path.Count, 2, "внутренняя стена требует обхода, хотя footprint цельный");
            Assert.IsTrue(path.Exists(point => point.y > wall.B.y + wall.WidthMeters * 0.5f),
                "маршрут обязан пройти над концом перегородки");
            for (int i = 0; i + 1 < path.Count; i++)
            {
                Assert.IsTrue(PlanRouteHintPathUseCase.IsSegmentInside(room, path[i], path[i + 1]),
                    $"отрезок {path[i]} → {path[i + 1]} пересекает стену");
            }
        }

        [Test]
        public void IsSegmentInside_RequiresVisibleClearanceFromWall()
        {
            RouteHintWall wall = new(new Vector2(4f, 1f), new Vector2(4f, 7f), 0.2f);
            RouteHintRoom room = new("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) }, new[] { wall });

            bool touching = PlanRouteHintPathUseCase.IsSegmentInside(room,
                new Vector2(4.25f, 2f), new Vector2(4.25f, 6f));

            Assert.IsFalse(touching, "A hint route close to a wall would make its dots overlap the wall.");
        }

        [Test]
        public void Plan_PlayerBesideWall_ReachesDoorAfterMovingIntoClearance()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) },
                new[] { new RouteHintWall(new Vector2(0f, 0f), new Vector2(0f, 8f), 0.2f) });
            geometry.AddDoor("room", "east", "next", "west", new Vector2(8f, 4f));
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("room"),
                new Vector2(0.32f, 4f), "room", new CriticalPathDoor("room", "east", "next", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "A player beside a wall must still receive a route toward the next door.");
            AssertPoint(new Vector2(8f, 4f), path[path.Count - 1]);
        }

        [Test]
        public void Plan_PlayerBesideImpassableWall_DoesNotCrossIt()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) },
                new[] { new RouteHintWall(new Vector2(4f, 0f), new Vector2(4f, 8f), 0.2f) });
            geometry.AddDoor("room", "east", "next", "west", new Vector2(8f, 4f));
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("room"),
                new Vector2(3.72f, 4f), "room", new CriticalPathDoor("room", "east", "next", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, outcome,
                "Start clearance recovery must not turn a solid wall into a passage.");
            Assert.IsEmpty(path);
        }

        [Test]
        public void IsSegmentInsideFromPlayer_ShortParallelSegmentMustReachSafeClearance()
        {
            RouteHintRoom room = new("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) },
                new[] { new RouteHintWall(new Vector2(0f, 0f), new Vector2(0f, 8f), 0.2f) });

            bool traversable = PlanRouteHintPathUseCase.IsSegmentInsideFromPlayer(room,
                new Vector2(0.32f, 4f), new Vector2(0.32f, 4.5f));

            Assert.IsFalse(traversable,
                "A short route segment must not remain alongside the wall outside safe clearance.");
        }

        [Test]
        public void Plan_ImpassableInternalWall_ReturnsUnreachableWithoutLine()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) },
                new[] { new RouteHintWall(new Vector2(4f, 0f), new Vector2(4f, 8f), 0.2f) });
            geometry.AddRoom("next", new Vector2(8f, 0f), 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) });
            geometry.AddDoor("room", "east", "next", "west", new Vector2(8f, 2f));
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("room"),
                new Vector2(1f, 2f), "room", new CriticalPathDoor("room", "east", "next", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, outcome);
            Assert.IsEmpty(path, "недостижимую цель нельзя соединять прямой линией");
        }

        [Test]
        public void FloorCoverage_RejectsShortCutAcrossNarrowMissingCorner()
        {
            RouteHintRoom room = new("corner", Vector2.zero, 0f, new[]
            {
                new MinimapCellSnapshot(0f, 0f, 1f, 1f),
                new MinimapCellSnapshot(1f, 0f, 1f, 0.1f),
                new MinimapCellSnapshot(0f, 1f, 0.1f, 1f)
            });

            bool inside = PlanRouteHintPathUseCase.IsSegmentInside(room,
                new Vector2(1.5f, 0.05f), new Vector2(0.05f, 1.5f));

            Assert.IsFalse(inside, "даже узкий срез вне пола нельзя скрыть между шагами выборки");
        }

        [Test]
        public void Plan_LockedTransitDoor_DoesNotLeadThroughIt()
        {
            RouteHintGeometry geometry = CreateCorridor();
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("a", "b", "c"),
                new Vector2(1f, 2f), "a", new CriticalPathDoor("c", "c_e", "d", "d_w"), path,
                door => door.NodeA == "b" && door.NodeB == "c");

            Assert.AreEqual(RouteHintPathOutcome.Unreachable, outcome);
            Assert.IsEmpty(path);
        }

        [Test]
        public void Plan_ConnectedDoorAtWallRenderer_HasOnlyItsPortalPassable()
        {
            RouteHintGeometry geometry = new();
            RouteHintWall wall = new(new Vector2(4f, 0f), new Vector2(4f, 8f), 0.2f);
            geometry.AddRoom("a", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 4f, 8f) }, new[] { wall });
            geometry.AddRoom("b", new Vector2(4f, 0f), 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 4f, 8f) }, new[] { wall });
            Vector2 doorPoint = new(4f, 4f);
            geometry.AddDoor("a", "east", "b", "west", doorPoint);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("a"),
                new Vector2(2f, 4f), "a", new CriticalPathDoor("a", "east", "b", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            AssertPoint(doorPoint, path[path.Count - 1]);
        }

        [Test]
        public void Plan_DoorwayInThickWall_RemainsReachableWithWallInset()
        {
            RouteHintGeometry geometry = new();
            RouteHintWall wall = new(new Vector2(4f, 0f), new Vector2(4f, 8f), 0.8f);
            geometry.AddRoom("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 4f, 8f) }, new[] { wall });
            Vector2 doorPoint = new(4f, 4f);
            geometry.AddDoor("room", "east", "next", "west", doorPoint,
                DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(DoorSize.Medium));
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("room"),
                new Vector2(2f, 4f), "room", new CriticalPathDoor("room", "east", "next", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "The confirmed door opening must stay passable even when the wall renderer is thick.");
            AssertPoint(doorPoint, path[path.Count - 1]);
        }

        [Test]
        public void Plan_SmallDoorwayInThickWall_RemainsReachable()
        {
            RouteHintGeometry geometry = new();
            RouteHintWall wall = new(new Vector2(4f, 0f), new Vector2(4f, 8f), 0.8f);
            geometry.AddRoom("room", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 4f, 8f) }, new[] { wall });
            Vector2 doorPoint = new(4f, 4f);
            geometry.AddDoor("room", "east", "next", "west", doorPoint,
                DoorSocketDescriptorMetrics.DefaultFrameWidthMeters(DoorSize.Small));
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry, Revealed("room"),
                new Vector2(2f, 4f), "room", new CriticalPathDoor("room", "east", "next", "west"), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome,
                "A confirmed small doorway must remain reachable with a thick wall renderer.");
            AssertPoint(doorPoint, path[path.Count - 1]);
        }

        [Test]
        public void IsSegmentInside_OutsideConfirmedDoorPortal_StillBlocksWallCrossing()
        {
            RouteHintGeometry geometry = new();
            RouteHintWall wall = new(new Vector2(4f, 0f), new Vector2(4f, 8f), 0.2f);
            geometry.AddRoom("a", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) }, new[] { wall });
            geometry.AddDoor("a", "east", "b", "west", new Vector2(4f, 4f));
            geometry.TryGetRoom("a", out RouteHintRoom room);

            bool passable = PlanRouteHintPathUseCase.IsSegmentInside(room,
                new Vector2(3f, 1f), new Vector2(5f, 1f));

            Assert.IsFalse(passable, "The doorway must not open the rest of the wall.");
        }

        [Test]
        public void Plan_ConfirmedDoorJustBeyondFloorEdge_RemainsReachable()
        {
            RouteHintGeometry geometry = new();
            geometry.AddRoom("inside", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) });
            Vector2 doorCenter = new(5.18f, 2.5f);
            geometry.AddDoor("inside", "exit", "outside", "entry", doorCenter);
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.Execute(geometry,
                Revealed("inside"), new Vector2(2.5f, 2.5f), "inside",
                new CriticalPathDoor("inside", "exit", "outside", "entry"), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            AssertPoint(doorCenter, path[path.Count - 1]);
        }

        [Test]
        public void IsSegmentInside_OutsideFloorWithoutPortal_IsRejected()
        {
            RouteHintRoom room = new("inside", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 5f, 5f) });

            bool passable = PlanRouteHintPathUseCase.IsSegmentInside(room,
                new Vector2(2.5f, 2.5f), new Vector2(5.18f, 2.5f));

            Assert.IsFalse(passable, "допуск за краем пола разрешён только у подтверждённой двери");
        }

        [Test]
        public void PlanToRoomTask_WallMountedPanel_EndsOnReachableSide()
        {
            RouteHintGeometry geometry = new();
            RouteHintWall wall = new(new Vector2(4f, 0f), new Vector2(4f, 8f), 0.2f);
            geometry.AddRoom("task", Vector2.zero, 0f,
                new[] { new MinimapCellSnapshot(0f, 0f, 8f, 8f) }, new[] { wall });
            List<Vector2> path = new();

            RouteHintPathOutcome outcome = PlanRouteHintPathUseCase.ExecuteToRoomTask(geometry,
                Revealed("task"), new Vector2(1f, 4f), "task", "task", new Vector2(4f, 4f), path);

            Assert.AreEqual(RouteHintPathOutcome.Planned, outcome);
            Vector2 end = path[path.Count - 1];
            Assert.Less(end.x, 3.9f, "цель должна остаться на доступной стороне перегородки");
            Assert.Less(Vector2.Distance(end, new Vector2(4f, 4f)), 1f);
        }

        [Test]
        public void Room_WorldToLocal_InvertsLocalToWorld_ForRotatedRoom()
        {
            RouteHintRoom room = new("r", new Vector2(10f, -3f), 90f,
                new[] { new MinimapCellSnapshot(0f, 0f, Cell, Cell) });
            Vector2 local = new(1.5f, 2.5f);

            Vector2 roundTrip = room.WorldToLocal(room.LocalToWorld(local));

            AssertPoint(local, roundTrip);
            Assert.IsTrue(room.Contains(room.LocalToWorld(local), 0f));
        }

        private static RouteHintGeometry CreateCorridor()
        {
            RouteHintGeometry geometry = new();
            string[] ids = { "a", "b", "c", "d" };
            for (int i = 0; i < ids.Length; i++)
            {
                geometry.AddRoom(ids[i], new Vector2(i * Cell, 0f), 0f, new[] { new MinimapCellSnapshot(0f, 0f, Cell, Cell) });
            }

            geometry.AddDoor("a", "a_e", "b", "b_w", new Vector2(4f, 2f));
            geometry.AddDoor("b", "b_e", "c", "c_w", new Vector2(8f, 2f));
            geometry.AddDoor("c", "c_e", "d", "d_w", new Vector2(12f, 2f));
            return geometry;
        }

        private static System.Func<string, bool> Revealed(params string[] nodeIds)
        {
            HashSet<string> set = new(nodeIds);
            return set.Contains;
        }

        private static bool ContainsPoint(List<Vector2> path, Vector2 point)
        {
            for (int i = 0; i < path.Count; i++)
            {
                if (Vector2.Distance(path[i], point) < 0.01f)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AssertPoint(Vector2 expected, Vector2 actual)
        {
            Assert.AreEqual(expected.x, actual.x, 0.01f);
            Assert.AreEqual(expected.y, actual.y, 0.01f);
        }
    }
}
