using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RedoSchool.LevelGeneration.Runtime.Application;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;
using RedoSchool.LevelGeneration.Runtime.Presentation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>
    /// Карта не показывает того, куда ученик ещё не дошёл.
    /// </summary>
    /// <remarks>
    /// <para>Большая карта рисовала уровень целиком: открыв её, ученик сразу видел, где выход, где
    /// тупики и сколько ещё идти. Исследовать после этого было нечего, а разница между большой и
    /// угловой картой оказывалась не в честности, а в масштабе. Владелец: «большая карта как и
    /// маленькая должна показывать только пройденные комнаты, чтобы не спойлерить, что дальше».</para>
    ///
    /// <para><b>Почему тест нужен именно здесь.</b> Правка живёт в <c>LevelMinimapController</c>, а
    /// весь существующий набор целится в <c>LevelMinimapSession</c>, который не менялся. Без этого
    /// теста можно вернуть рисование на <c>GetAllRooms()</c> и остаться полностью зелёным — то есть
    /// спойлер вернулся бы молча.</para>
    /// </remarks>
    [Category("LevelGeneration")]
    public sealed class MinimapNoSpoilerTests
    {
        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _spawned.Clear();
        }

        [Test]
        public void DrawnRooms_AreOnlyTheVisitedOnes_NotTheWholeLevel()
        {
            LevelMinimapController controller = SpawnBoundController(out int totalRooms);
            Assert.That(totalRooms, Is.GreaterThan(1), "Для проверки нужен уровень больше одной комнаты.");

            int drawn = CountRoomsForDrawing(controller);

            Assert.That(
                drawn,
                Is.LessThan(totalRooms),
                "Карта рисует весь уровень целиком — непройденные комнаты видны, и исследовать нечего.");
        }

        [Test]
        public void DrawnRooms_GrowAsTheLearnerWalks()
        {
            // Обратная сторона того же правила: карта не должна быть просто пустой.
            LevelMinimapController controller = SpawnBoundController(out int totalRooms);
            int before = CountRoomsForDrawing(controller);

            RevealSecondRoom(controller);
            int after = CountRoomsForDrawing(controller);

            Assert.That(after, Is.GreaterThan(before), "Пройденная комната обязана появиться на карте.");
            Assert.That(after, Is.LessThan(totalRooms), "И только она — остальной уровень по-прежнему скрыт.");
        }

        [Test]
        public void DrawnConnections_NeverLeadIntoTheUnvisited()
        {
            // Коридор в непройденную комнату выдаёт и её существование, и направление — то есть
            // спойлерит ровно так же, как сама комната.
            LevelMinimapController controller = SpawnBoundController(out _);

            ICollection connections = InvokePrivate(controller, "ConnectionsForDrawing") as ICollection;
            ICollection rooms = InvokePrivate(controller, "RoomsForDrawing") as ICollection;

            Assert.That(rooms, Is.Not.Null);
            Assert.That(
                connections,
                Is.Not.Null.And.Count.EqualTo(0),
                "Из единственной пройденной комнаты вести некуда: связь рисуется только когда " +
                "вскрыты ОБА её конца.");
        }

        private LevelMinimapController SpawnBoundController(out int totalRooms)
        {
            GameObject host = new("MinimapNoSpoilerHost");
            _spawned.Add(host);

            LevelMinimapController controller = host.AddComponent<LevelMinimapController>();
            LevelLayout layout = CreateLayout();
            controller.Bind(layout);

            totalRooms = layout.Rooms.Count;
            return controller;
        }

        private static int CountRoomsForDrawing(LevelMinimapController controller) => (InvokePrivate(controller, "RoomsForDrawing") as ICollection)?.Count ?? -1;

        private static void RevealSecondRoom(LevelMinimapController controller)
        {
            // Типы доступны сборке тестов, поэтому без рефлексии: она уже один раз соврала здесь,
            // вернув null на `NodeId`, и тест упал на пустом списке вместо настоящей проверки.
            IReadOnlyList<MinimapRoomSnapshot> all = controller.Session.GetAllRooms();
            Assert.That(all, Has.Count.GreaterThan(1), "В раскладке меньше двух комнат.");

            List<string> ids = new();
            foreach (MinimapRoomSnapshot room in all)
            {
                ids.Add(room.NodeId);
            }

            controller.Session.RevealRoom(ids[1]);
        }

        /// <summary>Три комнаты в ряд: старт, промежуточная и выход. Та же, что у соседних тестов.</summary>
        private static LevelLayout CreateLayout()
        {
            return new LevelLayout(
                new[] { Room("n_start", 0, 0), Room("n_objective", 2, 0), Room("n_exit", 4, 0) },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west"), new DoorConnection("n_objective", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
        }

        private static PlacedRoom Room(string nodeId, float x, float y)
        {
            return new PlacedRoom(
                nodeId,
                $"template_{nodeId}",
                new GridPose(x, y, 0),
                new Dictionary<string, DoorState>(),
                null,
                null);
        }

        private static object InvokePrivate(LevelMinimapController controller, string method)
        {
            MethodInfo info = typeof(LevelMinimapController)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(
                info,
                Is.Not.Null,
                $"Метод {method} исчез — правило «рисуем только пройденное» перестало быть в одной точке.");
            return info.Invoke(controller, null);
        }
    }
}
