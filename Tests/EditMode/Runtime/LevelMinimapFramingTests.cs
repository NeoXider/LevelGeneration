using System.Reflection;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Runtime.Presentation;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Читаемость мини-карты: масштаб против фиксированного размера маркеров. Два дефекта, названных
    /// владельцем дословно, спорят друг с другом и оба сторожатся здесь. Первый — «треугольник игрока
    /// вылазит за стены»: маркер рисуется в пикселях и с картой не масштабируется, поэтому при слишком
    /// широком обзоре перерастает целую комнату. Второй — «отметки колб почти не видны» и «стрелку
    /// крупнее»: маркер, честно уменьшенный вместе с содержимым, вырождается в несколько пикселей и
    /// перестаёт работать ориентиром. Тесты держат маркеры в коридоре между «не видно» и «за стенами».
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelMinimapFramingTests
    {
        /// <summary>
        /// Компактная карта должна показывать вдвое больше мира без изменения экранной карточки:
        /// единый authored-масштаб применяется и к проекции мира, и к динамическим маркерам. Рост
        /// стрелки игрока живёт ОТДЕЛЬНЫМ бустом поверх этой базы, а не подменой базы: подмена
        /// утащила бы за собой двери и прочее содержимое карты.
        /// </summary>
        [Test]
        public void CompactMap_ContentAndMarkers_UseTheSameAuthoredScale()
        {
            GameObject host = new(nameof(CompactMap_ContentAndMarkers_UseTheSameAuthoredScale));
            try
            {
                LevelMinimapController controller = host.AddComponent<LevelMinimapController>();
                float contentScale = (float)Invoke(controller, "CompactContentScaleValue");
                float markerScale = (float)Invoke(controller, "ContentMarkerScale", false);
                float pixelsPerMeter = (float)Invoke(controller, "CompactMapScalePixelsPerMeter");
                float playerScale = (float)Invoke(controller, "PlayerMarkerScale", pixelsPerMeter);

                Assert.That(contentScale, Is.EqualTo(0.5f).Within(0.0001f),
                    "Компактная карта обязана показывать вдвое больше мира в прежнем viewport.");
                Assert.That(markerScale, Is.EqualTo(contentScale).Within(0.0001f),
                    "Содержимое карты обязано уменьшаться тем же authored-масштабом, что и геометрия.");
                Assert.That(
                    playerScale,
                    Is.EqualTo(contentScale * LevelMinimapController.CompactPlayerMarkerBoost).Within(0.0001f),
                    "Стрелка игрока обязана строиться как authored-масштаб ПЛЮС буст: буст растит только "
                    + "стрелку, а не всё содержимое карты.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Стрелка игрока зажата между двумя границами. Сверху — ячейка сетки: ячейка это минимальная
        /// комната, и стрелка шире неё читается как «игрок вне стен». Снизу — заметность: владелец
        /// просил стрелку крупнее, и стрелка, потерявшаяся на карточке, не выполняет свою работу.
        /// Прежнего запаса в 20% под верхней границей больше нет — это осознанная плата за размер.
        /// </summary>
        [Test]
        public void PlayerMarker_OnCompactMap_FillsMostOfAGridCellWithoutLeavingIt()
        {
            GameObject host = new(nameof(PlayerMarker_OnCompactMap_FillsMostOfAGridCellWithoutLeavingIt));
            try
            {
                LevelMinimapController controller = host.AddComponent<LevelMinimapController>();
                float pixelsPerMeter = (float)Invoke(controller, "CompactMapScalePixelsPerMeter");
                float cellPixels = LevelGenerationGridMetrics.CellSizeMeters * pixelsPerMeter;
                float markerPixels = (float)Invoke(controller, "PlayerMarkerPixelSize", pixelsPerMeter);

                Assert.That(
                    markerPixels,
                    Is.LessThan(cellPixels),
                    $"Стрелка игрока {markerPixels:F1} px не помещается в ячейку сетки {cellPixels:F1} px: "
                    + $"на карте она вылезает за стены комнаты. Зум сейчас {pixelsPerMeter:F2} px/м. "
                    + "Расти дальше можно только вместе с зумом карточки, а не одним бустом.");
                Assert.That(
                    markerPixels,
                    Is.GreaterThan(cellPixels * 0.7f),
                    $"Стрелка игрока {markerPixels:F1} px потерялась на фоне комнаты {cellPixels:F1} px. "
                    + "Компактную карту видно постоянно, и владелец уже жаловался, что стрелку на ней "
                    + "не находит глазами.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void PlayerMarker_KeepsTheSameRoomRelativeSizeAtBothMapZooms()
        {
            GameObject host = new(nameof(PlayerMarker_KeepsTheSameRoomRelativeSizeAtBothMapZooms));
            try
            {
                LevelMinimapController controller = host.AddComponent<LevelMinimapController>();
                float compactScale = (float)Invoke(controller, "CompactMapScalePixelsPerMeter");
                float fullScale = compactScale / LevelMinimapController.FullMapZoomDivider;
                float compactMarker = (float)Invoke(controller, "PlayerMarkerPixelSize", compactScale);
                float fullMarker = (float)Invoke(controller, "PlayerMarkerPixelSize", fullScale);

                Assert.That(fullMarker / fullScale, Is.EqualTo(compactMarker / compactScale).Within(0.0001f),
                    "The player marker must occupy the same fraction of a room at either zoom.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Отметка колбы на полной карте не мельче порога заметности В ЭКРАННЫХ пикселях. Именно этот
        /// дефект владелец и увидел: размер был привязан к масштабу содержимого, тот делится на
        /// <see cref="LevelMinimapController.FullMapZoomDivider"/>, и кружок вырождался в 3 px.
        /// </summary>
        [Test]
        public void FragmentMarker_OnFullMap_IsNotSmallerThanTheReadabilityFloor()
        {
            const float minimumDiameterPixels = 14f;
            GameObject host = new(nameof(FragmentMarker_OnFullMap_IsNotSmallerThanTheReadabilityFloor));
            try
            {
                LevelMinimapController controller = host.AddComponent<LevelMinimapController>();
                float diameter = (float)Invoke(controller, "FragmentMarkerRadiusPixels", true) * 2f;

                Assert.That(
                    diameter,
                    Is.GreaterThanOrEqualTo(minimumDiameterPixels),
                    $"Отметка колбы на полной карте {diameter:F1} px в поперечнике — меньше порога "
                    + $"{minimumDiameterPixels:F0} px. Точечный ориентир обязан задаваться экраном, а не "
                    + "зумом мира: привязка к масштабу содержимого уже вырождала отметку в невидимые 3 px.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Размер отметки колбы не должен схлопываться вслед за authored-масштабом содержимого:
        /// у отметки есть пол в экранных пикселях. Проверяется на самом мелком допустимом масштабе —
        /// это ровно тот сценарий, где прежняя формула давала «почти не видно».
        /// </summary>
        [Test]
        public void FragmentMarker_KeepsItsScreenFloor_AtTheSmallestAuthoredContentScale()
        {
            GameObject host = new(nameof(FragmentMarker_KeepsItsScreenFloor_AtTheSmallestAuthoredContentScale));
            try
            {
                LevelMinimapController controller = host.AddComponent<LevelMinimapController>();
                SetPrivateField(controller, "compactContentScale", 0.25f);
                float radius = (float)Invoke(controller, "FragmentMarkerRadiusPixels", true);

                Assert.That(
                    radius,
                    Is.GreaterThanOrEqualTo(LevelMinimapController.FullMapFragmentMarkerMinRadiusPixels),
                    "Отметка колбы обязана держать нижнюю границу в экранных пикселях независимо от "
                    + "authored-масштаба содержимого.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// На компактной карточке отметка колбы обязана быть мельче, чем на полной (карточка вчетверо
        /// мельче по площади, и отметка в полный размер накрыла бы комнату), но всё ещё видимой —
        /// иначе повторяется исходная жалоба «на миникарте очень незаметны».
        /// </summary>
        [Test]
        public void FragmentMarker_OnCompactMap_IsSmallerThanOnFullMapButStillVisible()
        {
            const float minimumDiameterPixels = 6f;
            GameObject host = new(nameof(FragmentMarker_OnCompactMap_IsSmallerThanOnFullMapButStillVisible));
            try
            {
                LevelMinimapController controller = host.AddComponent<LevelMinimapController>();
                float compactRadius = (float)Invoke(controller, "FragmentMarkerRadiusPixels", false);
                float fullMapRadius = (float)Invoke(controller, "FragmentMarkerRadiusPixels", true);

                Assert.That(compactRadius, Is.LessThan(fullMapRadius),
                    $"Отметка на компактной карте ({compactRadius:F1} px) обязана быть мельче, чем на "
                    + $"полной ({fullMapRadius:F1} px).");
                Assert.That(
                    compactRadius * 2f,
                    Is.GreaterThanOrEqualTo(minimumDiameterPixels),
                    $"Отметка на компактной карте {compactRadius * 2f:F1} px в поперечнике — её снова не "
                    + "будет видно.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static object Invoke(LevelMinimapController controller, string method, params object[] args)
        {
            MethodInfo info = typeof(LevelMinimapController)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, "У контроллера нет метода " + method + ".");
            return info.Invoke(controller, args);
        }

        private static void SetPrivateField(LevelMinimapController controller, string field, object value)
        {
            FieldInfo info = typeof(LevelMinimapController)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null, "У контроллера нет поля " + field + ".");
            info.SetValue(controller, value);
        }
    }

    [Category("LevelGeneration")]
    public sealed class RouteHintScreenClearanceTests
    {
        [Test]
        public void FitDotRadius_LeavesVisibleSpaceBetweenDotAndWall()
        {
            RouteHintScreenWall wall = new(new Vector2(-10f, 0f), new Vector2(10f, 0f), 2f);
            Vector2 center = new(0f, 4f);

            float radius = RouteHintScreenClearance.FitDotRadius(center, 3f, new[] { wall });

            Assert.That(radius, Is.GreaterThan(0f));
            Assert.That(radius + wall.StrokeRadius, Is.LessThan(center.y));
        }

        [Test]
        public void FitLineThickness_RejectsSpanThatCrossesWallBetweenEndpoints()
        {
            RouteHintScreenWall wall = new(new Vector2(-2f, -2f), new Vector2(-2f, 2f), 0.25f);

            float thickness = RouteHintScreenClearance.FitLineThickness(
                new Vector2(-4f, 0f), new Vector2(4f, 0f), 3f, new[] { wall });

            Assert.That(thickness, Is.Zero,
                "A route segment crossing a wall must not be drawn, even if its midpoint is clear.");
        }
    }
}
