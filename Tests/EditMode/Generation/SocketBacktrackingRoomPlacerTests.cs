using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Application.Pipeline;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Generation.Infrastructure;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    [Category("LevelGeneration")]
    public sealed class SocketBacktrackingRoomPlacerTests
    {
        [Test]
        public void Place_SupportsArbitraryDoorYawAndConnectsDoorCenters()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_room", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("n_start", "n_room", true) });
            MetricDoor startDoor = new("start_17", 0f, 0f, 17f);
            MetricDoor roomDoor = new("room_231", 2f, 0f, 231f);
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { startDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, -1f, 1f, -1f, 0.2f) }),
                ["n_room"] = new FakeRoomTemplate(
                    "room_any_angle",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { roomDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, 1f, 1f, 1f, 0.2f) })
            };

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, selections, new FirstRollRng());

            Assert.AreEqual(2, layout.Rooms.Count);
            Assert.AreEqual(1, layout.Connections.Count);
            PlacedRoom placedRoom = layout.Rooms.Single(room => room.NodeId == "n_room");
            Assert.IsTrue(placedRoom.WorldPose.HasValue);
            Assert.That(YawDegrees(placedRoom.WorldPose.Value), Is.EqualTo(326f).Within(0.01f));

            PlacedRoom start = layout.Rooms.Single(room => room.NodeId == "n_start");
            (float x, float z) startPoint = DoorWorldPoint(start, startDoor);
            (float x, float z) roomPoint = DoorWorldPoint(placedRoom, roomDoor);
            Assert.That(startPoint.x, Is.EqualTo(roomPoint.x).Within(0.001f));
            Assert.That(startPoint.z, Is.EqualTo(roomPoint.z).Within(0.001f));
        }

        [Test]
        public void Place_DoesNotConnectDisabledDoorSockets()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_room", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("n_start", "n_room", true) });
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("disabled", 0f, 0f, 0f, DoorState.Disabled) }),
                ["n_room"] = new FakeRoomTemplate(
                    "room_next",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("west", 0f, 0f, 180f) })
            };

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, selections, new FirstRollRng());

            Assert.AreEqual(1, layout.Rooms.Count);
            Assert.AreEqual(0, layout.Connections.Count);
        }

        [Test]
        public void Place_WithSnapshotDescriptors_KeepsDoorCentersAndYaw()
        {
            LevelGraph graph = new(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_room", RoomKind.Passage, 1) },
                new[] { new LevelGraphEdge("n_start", "n_room", true) });
            MetricDoor startDoor = new("start_45", 3f, -1f, 45f);
            MetricDoor roomDoor = new("room_225", -4f, 2f, 225f);
            Dictionary<string, IRoomTemplateDescriptor> selections = new()
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { startDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, -1f, 1f, -1f, 0.2f) }),
                ["n_room"] = new FakeRoomTemplate(
                    "room_45",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { roomDoor },
                    wallSegments: new[] { new RoomWallSegment2D(-1f, 1f, 1f, 1f, 0.2f) })
            };
            Dictionary<string, IRoomTemplateDescriptor> snapshots = RoomTemplateDescriptorSnapshot.Capture(selections);

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(graph, snapshots, new FirstRollRng());

            Assert.AreEqual(2, layout.Rooms.Count);
            Assert.AreEqual(1, layout.Connections.Count);
            PlacedRoom start = layout.Rooms.Single(room => room.NodeId == "n_start");
            PlacedRoom placedRoom = layout.Rooms.Single(room => room.NodeId == "n_room");
            IDoorSocketDescriptor startSnapshotDoor = snapshots["n_start"].Doors.Single();
            IDoorSocketDescriptor roomSnapshotDoor = snapshots["n_room"].Doors.Single();
            (float x, float z) startPoint = DoorWorldPoint(start, startSnapshotDoor);
            (float x, float z) roomPoint = DoorWorldPoint(placedRoom, roomSnapshotDoor);
            Assert.That(startPoint.x, Is.EqualTo(roomPoint.x).Within(0.001f));
            Assert.That(startPoint.z, Is.EqualTo(roomPoint.z).Within(0.001f));
        }

        [Test]
        public void Place_DenseMazeBranchRoles_AssignEachAnswerSocketExactlyOnce()
        {
            LevelGraph graph = DenseMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = DenseMazeSelections();

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                graph,
                selections,
                new SystemRandomRng(1499425446));

            Assert.AreEqual(graph.Nodes.Count, layout.Rooms.Count, "Dense MazeBranch graph should fully place.");
            DoorConnection entry = layout.Connections.Single(c => c.ToNodeId == "n_maze");
            Assert.AreEqual("entry", entry.ToSocketId);
            DoorConnection correct = layout.Connections.Single(c => c.FromNodeId == "n_maze"
                                                                    && c.ToNodeId == "n_correct_tunnel");
            Assert.IsTrue(correct.IsMainPath);

            DoorConnection wrong1 = layout.Connections.Single(c => c.FromNodeId == "n_maze"
                                                                   && c.ToNodeId == "n_wrong_1");
            DoorConnection wrong2 = layout.Connections.Single(c => c.FromNodeId == "n_maze"
                                                                   && c.ToNodeId == "n_wrong_2");
            Assert.IsFalse(wrong1.IsMainPath);
            Assert.IsFalse(wrong2.IsMainPath);

            // Каждая дверь ответа (Locked) занята ровно одним ребром: правильным или неверным.
            CollectionAssert.AreEquivalent(
                new[] { "correct", "wrong_1", "wrong_2" },
                new[] { correct.FromSocketId, wrong1.FromSocketId, wrong2.FromSocketId });
            Assert.IsTrue(graph.Nodes.Single(n => n.NodeId == "n_wrong_1").IsDeadEnd);
            Assert.IsTrue(graph.Nodes.Single(n => n.NodeId == "n_wrong_2").IsDeadEnd);
        }

        [Test]
        public void Place_MazeCorrectAnswerDoor_IsChosenBySeed_NotAlwaysStraightAhead()
        {
            // Раньше правильной всегда становилась дверь «прямо по курсу» (мировой угол ≈ 0°, здесь — «correct»),
            // и ответ угадывался по направлению. Теперь её выбирает ГСЧ сида среди дверей ответа.
            LevelGraph graph = DenseMazeGraph();
            HashSet<string> correctSockets = new();
            for (int seed = 1; seed <= 40; seed++)
            {
                string first = PlaceAndGetCorrectSocket(graph, seed);
                string second = PlaceAndGetCorrectSocket(graph, seed);
                Assert.AreEqual(first, second, $"Seed {seed} must pick the same correct door every time.");
                correctSockets.Add(first);
            }

            Assert.That(
                correctSockets.Count,
                Is.GreaterThanOrEqualTo(2),
                $"The correct answer door must vary across seeds; got only: {string.Join(", ", correctSockets)}.");
            CollectionAssert.IsSubsetOf(correctSockets, new[] { "correct", "wrong_1", "wrong_2" });
        }

        [Test]
        public void Place_WrongAnswerCapThatCannotFit_IsNotForcePlacedWithOverlap()
        {
            // Заглушка n_wrong_1 своей стеной всегда пересекает стены комнаты-лабиринта (у любой двери ответа).
            // Раньше её ставили принудительно С наложением; теперь она остаётся неразмещённой, а n_wrong_2 ставится.
            LevelGraph graph = DenseMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = UnfittableCapMazeSelections();

            for (int seed = 1; seed <= 5; seed++)
            {
                LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                    graph,
                    selections,
                    new SystemRandomRng(seed));

                Assert.IsFalse(layout.Rooms.Any(room => room.NodeId == "n_wrong_1"),
                    $"Seed {seed}: an unfittable wrong-answer cap must never be force-placed over the maze room.");
                Assert.IsFalse(layout.Connections.Any(c => c.ToNodeId == "n_wrong_1"));
                Assert.IsTrue(layout.Rooms.Any(room => room.NodeId == "n_wrong_2"),
                    $"Seed {seed}: the cap that fits is still placed.");
                Assert.IsTrue(layout.Rooms.Any(room => room.NodeId == "n_exit"),
                    $"Seed {seed}: the main path must still be complete.");
            }
        }

        [Test]
        public async Task Place_SameSeed_SyncAndCooperativePlacementProduceIdenticalLayouts()
        {
            LevelGraph graph = DenseMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = DenseMazeSelections();
            SocketBacktrackingRoomPlacer placer = new(TestInteractiveRoomKinds.Standard);
            foreach (int seed in new[] { 3, 17, 1499425446 })
            {
                string sync = Fingerprint(placer.Place(graph, selections, new SystemRandomRng(seed)));
                string again = Fingerprint(placer.Place(graph, selections, new SystemRandomRng(seed)));
                string cooperative = Fingerprint(
                    await placer.PlaceAsync(graph, selections, new SystemRandomRng(seed)));

                Assert.AreEqual(sync, again, $"Seed {seed}: placement must be deterministic.");
                Assert.AreEqual(sync, cooperative, $"Seed {seed}: cooperative placement must match the sync one.");
            }
        }

        [Test]
        public void Place_ExhaustedCandidateBudget_StopsDeterministically()
        {
            // Предел поиска — счётный, а не временной: крошечный бюджет обрывает размещение в одной и той же точке.
            LevelGraph graph = DenseMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = DenseMazeSelections();
            SocketBacktrackingRoomPlacer placer = new(TestInteractiveRoomKinds.Standard) { CandidateEvaluationBudget = 1 };
            LevelGenerationDebugCounters counters = new();
            placer.SetDebugCounters(counters);

            LevelLayout first = placer.Place(graph, selections, new SystemRandomRng(5));
            LevelLayout second = placer.Place(graph, selections, new SystemRandomRng(5));

            Assert.Less(first.Rooms.Count, graph.Nodes.Count, "A one-evaluation budget cannot place the whole graph.");
            Assert.AreEqual(Fingerprint(first), Fingerprint(second));
            Assert.That(counters.PlacementBudgetExhaustions, Is.GreaterThan(0));
        }

        [Test]
        public void Placer_KeepsNoStaticMutableCollections()
        {
            // Предупреждения о заглушках раньше копились в общем статическом списке размещения
            // (ForcedMazeDeadEndWarnings), который делили все прогоны и потоки. Состояние прогона — только в экземплярах.
            // Проверяются и вложенные типы, и сами значения: изменяемая коллекция за полем IReadOnly* тоже общая.
            foreach (Type type in SelfAndNestedTypes(typeof(SocketBacktrackingRoomPlacer)))
            {
                if (type.ContainsGenericParameters)
                {
                    continue;
                }

                FieldInfo[] staticFields = type.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (FieldInfo field in staticFields)
                {
                    if (field.IsLiteral)
                    {
                        continue;
                    }

                    object value = field.GetValue(null);
                    bool mutable = value == null
                        ? typeof(System.Collections.ICollection).IsAssignableFrom(field.FieldType)
                          || ImplementsGenericCollection(field.FieldType)
                        : IsMutableCollectionInstance(value);
                    Assert.IsFalse(mutable,
                        $"Static field '{type.Name}.{field.Name}' ({value?.GetType().Name ?? field.FieldType.Name}) is shared mutable state across generation runs.");
                }
            }
        }

        [Test]
        public void Place_ReservedCapThatWouldBlockTheCorrectSuccessor_IsDeferredInsteadOfFailing()
        {
            // «Y-лабиринт»: две двери ответа рядом, заглушка у неверной двери занимает ровно то место, куда встаёт
            // тоннель правильного ответа (и наоборот — при любом выборе правильной двери). Резерв заглушки при
            // постановке лабиринта блокировал тоннель во всех позах и перезапусках — стадия падала для любого сида.
            // Теперь тот же кандидат повторяется без резерва: тоннель и выход ставятся, заглушка — нет (без наложения).
            LevelGraph graph = YMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = YMazeSelections();
            for (int seed = 1; seed <= 6; seed++)
            {
                LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                    graph,
                    selections,
                    new SystemRandomRng(seed));

                Assert.IsTrue(layout.Rooms.Any(room => room.NodeId == "n_tunnel"),
                    $"Seed {seed}: a best-effort cap must never block the correct-answer successor.");
                Assert.IsTrue(layout.Rooms.Any(room => room.NodeId == "n_exit"), $"Seed {seed}: main path incomplete.");
                Assert.IsFalse(layout.Rooms.Any(room => room.NodeId == "n_wrong"),
                    $"Seed {seed}: the cap only fits over the tunnel, so it must stay unplaced.");
                AssertNoFootprintOverlaps(layout, selections, seed);
            }
        }

        [Test]
        public void Place_MazePoseWhereTheCapFits_IsPreferredOverAPoseWhereItDoesNot()
        {
            // У старта две двери; через «a_exit» (раньше по порядку дверей) заглушка неверного ответа легла бы на
            // вторую клетку старта, через «b_exit» — помещается. Штраф позы без места под заглушку выбирает b_exit.
            LevelGraph graph = SingleCapMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = SingleCapMazeSelections(
                new FakeRoomTemplate(
                    "room_start_two_exits",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("a_exit", 0f, 0f, 0f, flowDirection: DoorSocketFlowDirection.ExitOnly), new MetricDoor("b_exit", 0f, -20f, 0f, flowDirection: DoorSocketFlowDirection.ExitOnly) },
                    footprint: new RoomFootprint(3, 3),
                    footprintShape: new RoomFootprintShape(
                        new RoomFootprint(3, 3),
                        new[] { new RoomFootprintCell(0, 0), new RoomFootprintCell(2, 2) })),
                DenseTunnelTemplate());

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                graph,
                selections,
                new SystemRandomRng(11));

            Assert.AreEqual(graph.Nodes.Count, layout.Rooms.Count, "The cap must be placed in the pose where it fits.");
            Assert.AreEqual("b_exit", layout.Connections.Single(c => c.ToNodeId == "n_maze").FromSocketId);
            AssertNoFootprintOverlaps(layout, selections, 11);
        }

        [Test]
        public void Place_ReservesWrongAnswerCapBeforeTheMainPathContinues()
        {
            // Предпочтительная поза тоннеля (дверью t1) заняла бы место заглушки. Заглушка резервируется вместе с
            // лабиринтом, поэтому тоннель встаёт другой дверью (t2), и в раскладке есть все комнаты. Без резерва
            // тоннель занял бы это место, а заглушка в конце не поместилась бы.
            LevelGraph graph = SingleCapMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = SingleCapMazeSelections(
                new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("start_exit", 0f, 0f, 0f, flowDirection: DoorSocketFlowDirection.ExitOnly) }),
                new FakeRoomTemplate(
                    "room_two_way_tunnel",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("t1", 10f, -10f, 180f), new MetricDoor("t2", 0f, 0f, 90f) }));

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                graph,
                selections,
                new SystemRandomRng(3));

            Assert.AreEqual(graph.Nodes.Count, layout.Rooms.Count, "The reserved cap and the whole main path must fit.");
            Assert.AreEqual("t2", layout.Connections.Single(c => c.ToNodeId == "n_tunnel").ToSocketId,
                "The tunnel must avoid the space reserved for the wrong-answer cap.");
            AssertNoFootprintOverlaps(layout, selections, 3);
        }

        [Test]
        public void Place_FailingCorrectDoorAssignment_IsRotatedOnRestart()
        {
            // Тоннель стыкуется только к средней (Medium) двери ответа, к большой (Large) — нет. Если сид выбрал
            // большую дверь правильной, раньше этот выбор держался во всех перезапусках и стадия падала; теперь
            // следующий перезапуск берёт следующую по кругу дверь.
            LevelGraph graph = DenseMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = DenseMazeSelections();
            selections["n_maze"] = new FakeRoomTemplate(
                "room_maze_sizes",
                RoomKind.Interactive,
                doors: new IDoorSocketDescriptor[]
                {
                    new MetricDoor("entry", -10f, 0f, 180f, flowDirection: DoorSocketFlowDirection.EntryOnly),
                    new MetricDoor("answer_medium", 10f, 0f, 0f, DoorState.Locked, DoorSocketFlowDirection.ExitOnly),
                    new MetricDoor("answer_large", 0f, 10f, 270f, DoorState.Locked, DoorSocketFlowDirection.ExitOnly, DoorSize.Large)
                },
                interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                interactiveOptionCapacity: 2);
            selections["n_wrong_1"] = new FakeRoomTemplate(
                "room_wrong_large",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry_large", 0f, 0f, 90f, size: DoorSize.Large) });
            LevelGraph twoAnswerGraph = new(
                graph.Nodes.Where(node => node.NodeId != "n_wrong_2").ToArray(),
                graph.Edges.Where(edge => edge.ToNodeId != "n_wrong_2").ToArray());
            selections.Remove("n_wrong_2");

            for (int seed = 1; seed <= 20; seed++)
            {
                LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                    twoAnswerGraph,
                    selections,
                    new SystemRandomRng(seed));

                Assert.AreEqual(twoAnswerGraph.Nodes.Count, layout.Rooms.Count,
                    $"Seed {seed}: a correct-door pick that cannot host the tunnel must not fail every restart.");
                Assert.AreEqual("answer_medium",
                    layout.Connections.Single(c => c.FromNodeId == "n_maze" && c.ToNodeId == "n_correct_tunnel")
                        .FromSocketId);
            }
        }

        [Test]
        public void Place_Report_NamesTheMazeDoorOfAnUnplacedCap()
        {
            LevelGraph graph = DenseMazeGraph();
            Dictionary<string, IRoomTemplateDescriptor> selections = UnfittableCapMazeSelections();
            RoomPlacementReport report = new();

            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                graph,
                selections,
                new SystemRandomRng(4),
                report);

            Assert.IsTrue(report.TryGetUnplacedMazeCap("n_wrong_1", out RoomPlacementReport.UnplacedMazeCap cap));
            Assert.AreEqual("n_maze", cap.MazeNodeId);
            CollectionAssert.Contains(new[] { "correct", "wrong_1", "wrong_2" }, cap.MazeSocketId);
            Assert.IsFalse(layout.Connections.Any(c => c.FromNodeId == "n_maze" && c.FromSocketId == cap.MazeSocketId),
                "The reported door is the answer door left without a connection.");
            Assert.IsFalse(report.TryGetUnplacedMazeCap("n_wrong_2", out _));
        }

        internal static LevelGraph YMazeGraph()
        {
            return new LevelGraph(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_maze", RoomKind.Interactive, 3, isOnMainPath: true), new RoomNode("n_tunnel", RoomKind.Passage, 2, isOnMainPath: true), new RoomNode("n_exit", RoomKind.Exit, 1), new RoomNode("n_wrong", RoomKind.Passage, 1, isOnMainPath: false, isDeadEnd: true) },
                new[] { new LevelGraphEdge("n_start", "n_maze", true, EdgeBranchRole.MazeEntry), new LevelGraphEdge("n_maze", "n_tunnel", true, EdgeBranchRole.MazeCorrectExit), new LevelGraphEdge("n_tunnel", "n_exit", true), new LevelGraphEdge("n_maze", "n_wrong", false, EdgeBranchRole.MazeWrongDeadEnd) });
        }

        /// <summary>
        /// Комнаты без стен (проверка по клеткам 5×5 м у точки опоры): две двери ответа на правой грани лабиринта в 3 м
        /// друг от друга. Тоннель у одной и заглушка у другой занимают перекрывающиеся клетки при любом назначении.
        /// </summary>
        internal static Dictionary<string, IRoomTemplateDescriptor> YMazeSelections()
        {
            return new Dictionary<string, IRoomTemplateDescriptor>
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("s_exit", 5f, 2.5f, 0f, flowDirection: DoorSocketFlowDirection.ExitOnly) }),
                ["n_maze"] = new FakeRoomTemplate(
                    "room_y_maze",
                    RoomKind.Interactive,
                    doors: new IDoorSocketDescriptor[]
                    {
                        new MetricDoor("entry", 0f, 2.5f, 180f, flowDirection: DoorSocketFlowDirection.EntryOnly),
                        new MetricDoor("answer_a", 5f, 4f, 0f, DoorState.Locked, DoorSocketFlowDirection.ExitOnly),
                        new MetricDoor("answer_b", 5f, 1f, 0f, DoorState.Locked, DoorSocketFlowDirection.ExitOnly)
                    },
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    interactiveOptionCapacity: 2),
                ["n_tunnel"] = new FakeRoomTemplate(
                    "room_tunnel",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("t_in", 0f, 2.5f, 180f), new MetricDoor("t_out", 5f, 2.5f, 0f) }),
                ["n_exit"] = new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("e_in", 0f, 2.5f, 180f) }),
                ["n_wrong"] = new FakeRoomTemplate(
                    "room_cap",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("c_in", 0f, 2.5f, 180f) })
            };
        }

        private static LevelGraph SingleCapMazeGraph()
        {
            return new LevelGraph(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_maze", RoomKind.Interactive, 3, isOnMainPath: true), new RoomNode("n_tunnel", RoomKind.Passage, 2, isOnMainPath: true), new RoomNode("n_exit", RoomKind.Exit, 1), new RoomNode("n_wrong", RoomKind.Passage, 1, isOnMainPath: false, isDeadEnd: true) },
                new[] { new LevelGraphEdge("n_start", "n_maze", true, EdgeBranchRole.MazeEntry), new LevelGraphEdge("n_maze", "n_tunnel", true, EdgeBranchRole.MazeCorrectExit), new LevelGraphEdge("n_tunnel", "n_exit", true), new LevelGraphEdge("n_maze", "n_wrong", false, EdgeBranchRole.MazeWrongDeadEnd) });
        }

        // Лабиринт с одной дверью ответа под замком («answer», правильная при любом сиде) и одной боковой дверью
        // («side», неверный ответ); геометрия плотного теста: двери в 10 м от точки опоры, клетки 5×5 м.
        private static Dictionary<string, IRoomTemplateDescriptor> SingleCapMazeSelections(
            IRoomTemplateDescriptor start,
            IRoomTemplateDescriptor tunnel)
        {
            return new Dictionary<string, IRoomTemplateDescriptor>
            {
                ["n_start"] = start,
                ["n_maze"] = new FakeRoomTemplate(
                    "room_single_cap_maze",
                    RoomKind.Interactive,
                    doors: new IDoorSocketDescriptor[]
                    {
                        new MetricDoor("entry", -10f, 0f, 180f, flowDirection: DoorSocketFlowDirection.EntryOnly),
                        new MetricDoor("answer", 10f, 0f, 0f, DoorState.Locked, DoorSocketFlowDirection.ExitOnly),
                        new MetricDoor("side", 0f, 10f, 270f, flowDirection: DoorSocketFlowDirection.ExitOnly)
                    },
                    interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                    interactiveOptionCapacity: 2),
                ["n_tunnel"] = tunnel,
                ["n_exit"] = new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("exit_entry", 0f, 0f, 180f) }),
                ["n_wrong"] = new FakeRoomTemplate(
                    "room_cap",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry", 0f, 0f, 90f) })
            };
        }

        private static FakeRoomTemplate DenseTunnelTemplate()
        {
            return new FakeRoomTemplate(
                "room_correct_tunnel",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new MetricDoor("correct_entry", 0f, 0f, 180f), new MetricDoor("correct_exit", 10f, 0f, 0f) });
        }

        private static void AssertNoFootprintOverlaps(
            LevelLayout layout,
            IReadOnlyDictionary<string, IRoomTemplateDescriptor> selections,
            int seed)
        {
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                for (int j = i + 1; j < layout.Rooms.Count; j++)
                {
                    PlacedRoom a = layout.Rooms[i];
                    PlacedRoom b = layout.Rooms[j];
                    Assert.IsFalse(
                        RoomPlacer.Overlaps(a.Pose, selections[a.NodeId], b.Pose, selections[b.NodeId]),
                        $"Seed {seed}: rooms '{a.NodeId}' and '{b.NodeId}' overlap.");
                }
            }
        }

        private static IEnumerable<Type> SelfAndNestedTypes(Type root)
        {
            yield return root;
            foreach (Type nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (Type type in SelfAndNestedTypes(nested))
                {
                    yield return type;
                }
            }
        }

        private static bool IsMutableCollectionInstance(object value)
        {
            switch (value)
            {
                case string:
                    return false;
                case Array array:
                    return array.Length > 0;
                case System.Collections.IDictionary dictionary:
                    return !dictionary.IsReadOnly;
                case System.Collections.IList list:
                    return !list.IsReadOnly;
            }

            foreach (Type contract in value.GetType().GetInterfaces())
            {
                if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICollection<>))
                {
                    object readOnly = contract.GetProperty("IsReadOnly")?.GetValue(value);
                    return !(readOnly is bool isReadOnly && isReadOnly);
                }
            }

            return value is System.Collections.ICollection;
        }

        internal static LevelGraph DenseMazeGraph()
        {
            return new LevelGraph(
                new[] { new RoomNode("n_start", RoomKind.Start, 1), new RoomNode("n_maze", RoomKind.Interactive, 4, isOnMainPath: true, contentId: "Q_MAZE"), new RoomNode("n_correct_tunnel", RoomKind.Passage, 2, isOnMainPath: true), new RoomNode("n_exit", RoomKind.Exit, 1), new RoomNode("n_wrong_1", RoomKind.Passage, 1, isDeadEnd: true), new RoomNode("n_wrong_2", RoomKind.Passage, 1, isDeadEnd: true) },
                new[] { new LevelGraphEdge("n_start", "n_maze", true, EdgeBranchRole.MazeEntry), new LevelGraphEdge("n_maze", "n_correct_tunnel", true, EdgeBranchRole.MazeCorrectExit), new LevelGraphEdge("n_correct_tunnel", "n_exit", true), new LevelGraphEdge("n_maze", "n_wrong_1", false, EdgeBranchRole.MazeWrongDeadEnd), new LevelGraphEdge("n_maze", "n_wrong_2", false, EdgeBranchRole.MazeWrongDeadEnd) });
        }

        internal static Dictionary<string, IRoomTemplateDescriptor> DenseMazeSelections()
        {
            return new Dictionary<string, IRoomTemplateDescriptor>
            {
                ["n_start"] = new FakeRoomTemplate(
                    "room_start",
                    RoomKind.Start,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("start_exit", 0f, 0f, 0f, flowDirection: DoorSocketFlowDirection.ExitOnly) }),
                ["n_maze"] = MazeTemplate(null),
                ["n_correct_tunnel"] = new FakeRoomTemplate(
                    "room_correct_tunnel",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("correct_entry", 0f, 0f, 180f), new MetricDoor("correct_exit", 10f, 0f, 0f) }),
                ["n_exit"] = new FakeRoomTemplate(
                    "room_exit",
                    RoomKind.Exit,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("exit_entry", 0f, 0f, 180f) }),
                ["n_wrong_1"] = new FakeRoomTemplate(
                    "room_wrong_1",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry_1", 0f, 0f, 90f) }),
                ["n_wrong_2"] = new FakeRoomTemplate(
                    "room_wrong_2",
                    RoomKind.Passage,
                    doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry_2", 0f, 0f, 270f) })
            };
        }

        /// <summary>
        /// Лабиринт со стенами-крестом в центре и заглушка n_wrong_1, чья стена уходит на 8 м в сторону лабиринта:
        /// у любой из дверей ответа она пересекает крест вдали от шва двери, поэтому без наложения не ставится.
        /// Остальные комнаты без стен (проверка по клеткам) и помещаются при любой правильной двери. Заглушки не
        /// годятся в пул тупикового прохода (CanBeSideBranch = false), чтобы запечатанная дверь не закрывалась им.
        /// </summary>
        internal static Dictionary<string, IRoomTemplateDescriptor> UnfittableCapMazeSelections()
        {
            Dictionary<string, IRoomTemplateDescriptor> selections = DenseMazeSelections();
            selections["n_maze"] = MazeTemplate(new[]
            {
                new RoomWallSegment2D(0f, -5f, 0f, 5f, 0.4f),
                new RoomWallSegment2D(-5f, 0f, 5f, 0f, 0.4f)
            });
            selections["n_wrong_1"] = new FakeRoomTemplate(
                "room_wrong_1",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry_1", 0f, 0f, 90f) },
                canBeSideBranch: false,
                wallSegments: new[] { new RoomWallSegment2D(-3f, -8f, 3f, -8f, 0.4f) });
            selections["n_wrong_2"] = new FakeRoomTemplate(
                "room_wrong_2",
                RoomKind.Passage,
                doors: new IDoorSocketDescriptor[] { new MetricDoor("wrong_entry_2", 0f, 0f, 270f) },
                canBeSideBranch: false);
            return selections;
        }

        internal static string Fingerprint(LevelLayout layout)
        {
            CultureInfo invariant = CultureInfo.InvariantCulture;
            StringBuilder builder = new();
            foreach (PlacedRoom room in layout.Rooms.OrderBy(r => r.NodeId, StringComparer.Ordinal))
            {
                builder.Append(room.NodeId).Append('=').Append(room.RoomTemplateId)
                    .Append('@').Append(room.Pose.CellX.ToString("R", invariant))
                    .Append(',').Append(room.Pose.CellY.ToString("R", invariant))
                    .Append(',').Append(room.Pose.RotationDegrees);
                if (room.WorldPose.HasValue)
                {
                    RoomWorldPose pose = room.WorldPose.Value;
                    builder.Append('|').Append(pose.X.ToString("R", invariant))
                        .Append(',').Append(pose.Z.ToString("R", invariant))
                        .Append(',').Append(pose.RotationY.ToString("R", invariant));
                }

                builder.Append(';');
            }

            foreach (string connection in layout.Connections
                         .Select(c => $"{c.FromNodeId}.{c.FromSocketId}>{c.ToNodeId}.{c.ToSocketId}:{c.IsMainPath}")
                         .OrderBy(c => c, StringComparer.Ordinal))
            {
                builder.Append(connection).Append(';');
            }

            return builder.ToString();
        }

        private static FakeRoomTemplate MazeTemplate(IReadOnlyList<RoomWallSegment2D> wallSegments)
        {
            return new FakeRoomTemplate(
                "room_maze",
                RoomKind.Interactive,
                doors: new IDoorSocketDescriptor[]
                {
                    new MetricDoor("entry", -10f, 0f, 180f, flowDirection: DoorSocketFlowDirection.EntryOnly), new MetricDoor("correct", 10f, 0f, 0f, DoorState.Locked,
                        DoorSocketFlowDirection.ExitOnly),
                    new MetricDoor("wrong_1", 0f, 10f, 270f, DoorState.Locked,
                        DoorSocketFlowDirection.ExitOnly),
                    new MetricDoor("wrong_2", 0f, -10f, 90f, DoorState.Locked,
                        DoorSocketFlowDirection.ExitOnly)
                },
                interactiveKind: TestInteractiveRoomKinds.MazeBranch,
                interactiveOptionCapacity: 3,
                wallSegments: wallSegments);
        }

        private static string PlaceAndGetCorrectSocket(LevelGraph graph, int seed)
        {
            LevelLayout layout = new SocketBacktrackingRoomPlacer(TestInteractiveRoomKinds.Standard).Place(
                graph,
                DenseMazeSelections(),
                new SystemRandomRng(seed));
            Assert.AreEqual(graph.Nodes.Count, layout.Rooms.Count, $"Seed {seed}: dense maze graph should fully place.");
            return layout.Connections.Single(c => c.FromNodeId == "n_maze" && c.ToNodeId == "n_correct_tunnel")
                .FromSocketId;
        }

        private static bool ImplementsGenericCollection(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICollection<>))
            {
                return true;
            }

            foreach (Type contract in type.GetInterfaces())
            {
                if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICollection<>))
                {
                    return true;
                }
            }

            return false;
        }

        private static (float x, float z) DoorWorldPoint(PlacedRoom room, IDoorSocketDescriptor door)
        {
            RoomWorldPose pose = room.WorldPose.Value;
            (float x, float z) local = Rotate((door.ConnectionLocalXMeters(), door.ConnectionLocalZMeters()),
                YawDegrees(pose));
            return (pose.X + local.x, pose.Z + local.z);
        }

        private static float YawDegrees(RoomWorldPose pose)
        {
            float siny = 2f * (pose.RotationW * pose.RotationY + pose.RotationZ * pose.RotationX);
            float cosy = 1f - 2f * (pose.RotationY * pose.RotationY + pose.RotationX * pose.RotationX);
            float yaw = (float)(Math.Atan2(siny, cosy) * 180.0 / Math.PI);
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private static (float x, float z) Rotate((float x, float z) point, float degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return (point.x * cos + point.z * sin, -point.x * sin + point.z * cos);
        }

        internal sealed class MetricDoor : IDoorSocketDescriptor, IDoorSocketMetricDescriptor,
            IDoorSocketConnectionPointDescriptor, IDoorSocketInitialStateDescriptor, IDoorSocketFlowDescriptor
        {
            public MetricDoor(
                string socketId,
                float x,
                float z,
                float localYawDegrees,
                DoorState initialState = DoorState.Closed,
                DoorSocketFlowDirection flowDirection = DoorSocketFlowDirection.Both,
                DoorSize size = DoorSize.Medium)
            {
                SocketId = socketId;
                Size = size;
                LocalXMeters = x;
                LocalZMeters = z;
                ConnectionLocalXMeters = x;
                ConnectionLocalYMeters = 0f;
                ConnectionLocalZMeters = z;
                LocalYawDegrees = localYawDegrees;
                InitialState = initialState;
                FlowDirection = flowDirection;
            }

            public string SocketId { get; }
            public DoorSize Size { get; }
            public DoorDirection Direction => DoorDirection.East;
            public int GridOffsetSteps => 0;
            public int LocalXSteps => 0;
            public int LocalZSteps => 0;
            public float LocalYawDegrees { get; }
            public bool CanBeMainPath => true;
            public bool CanBeSidePath => true;
            public bool CanBeLocked => true;
            public float GridOriginXMeters => 0f;
            public float GridOriginZMeters => 0f;
            public float LocalXMeters { get; }
            public float LocalZMeters { get; }
            public float ConnectionLocalXMeters { get; }
            public float ConnectionLocalYMeters { get; }
            public float ConnectionLocalZMeters { get; }
            public DoorState InitialState { get; }
            public DoorSocketFlowDirection FlowDirection { get; }
        }
    }
}
