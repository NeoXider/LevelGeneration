using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Runtime.Application;
using Neoxider.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Большая карта: вписанный прямоугольник показанных комнат с полями, центр — по нему,
    /// а не по игроку; мелкую карту кламп не раздувает сильнее утроенного масштаба угловой;
    /// при повороте карты вписанный прямоугольник поворачивается вместе с ней.
    /// Компактная карта по-прежнему центрирована на игроке.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class LevelMinimapFullMapFitTests
    {
        [Test]
        public void FullMap_WideLevel_FitsTheWholeExtent_AndCentersOnRoomsNotOnPlayer()
        {
            GameObject controllerGo = new("MinimapFitWide");
            GameObject playerGo = new("MinimapFitWidePlayer");
            try
            {
                playerGo.transform.position = new Vector3(100f, 0f, 50f);
                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);
                controller.Bind(CreateWideLayout());

                float compactScale = controller.CompactMapScalePixelsPerMeter();
                Rect rect = new(0f, 0f, 1136f, 680f);
                Assert.IsTrue(controller.TryBuildProjection(controller.Session.GetAllRooms(), rect, true,
                    out LevelMinimapController.MapProjection projection));

                Assert.Less(projection.Scale, compactScale / LevelMinimapController.FullMapZoomDivider,
                    "A 600 m wide level cannot keep the old zoom floor: the whole revealed extent must fit.");
                Assert.That(projection.WorldCenter.x, Is.EqualTo(0f).Within(6f),
                    "The map centers on the rooms, not on the player.");
                Assert.That(projection.WorldCenter.y, Is.EqualTo(-30f).Within(6f));
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        [Test]
        public void FullMap_TinyCluster_IsClampedToThreeCompactScales()
        {
            GameObject controllerGo = new("MinimapFitTiny");
            GameObject playerGo = new("MinimapFitTinyPlayer");
            try
            {
                playerGo.transform.position = new Vector3(6f, 0f, 0f);
                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);
                controller.Bind(CreateTinyLayout());

                float compactScale = controller.CompactMapScalePixelsPerMeter();
                Rect rect = new(0f, 0f, 1136f, 680f);
                Assert.IsTrue(controller.TryBuildProjection(controller.Session.GetAllRooms(), rect, true,
                    out LevelMinimapController.MapProjection projection));

                Assert.AreEqual(3f * compactScale, projection.Scale, 0.05f,
                    "A tiny map must not magnify beyond three compact scales.");
                Assert.That(projection.WorldCenter.x, Is.EqualTo(6f).Within(6f));
                Assert.That(projection.WorldCenter.y, Is.EqualTo(0f).Within(6f));
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        [Test]
        public void FullMap_Rotated45_FitsTheRotatedExtent()
        {
            GameObject straightGo = new("MinimapFitStraight");
            GameObject straightPlayerGo = new("MinimapFitStraightPlayer");
            GameObject rotatedGo = new("MinimapFitRotated");
            GameObject rotatedPlayerGo = new("MinimapFitRotatedPlayer");
            try
            {
                straightPlayerGo.transform.position = new Vector3(50f, 0f, 50f);
                AddLookCamera(straightPlayerGo.transform, 0f);
                LevelMinimapController straight = straightGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(straight, straightPlayerGo.transform);
                SetRotateWithPlayer(straight, true);
                straight.Bind(CreateDiagonalLayout());

                rotatedPlayerGo.transform.position = new Vector3(50f, 0f, 50f);
                AddLookCamera(rotatedPlayerGo.transform, 45f);
                LevelMinimapController rotated = rotatedGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(rotated, rotatedPlayerGo.transform);
                SetRotateWithPlayer(rotated, true);
                rotated.Bind(CreateDiagonalLayout());

                Rect rect = new(0f, 0f, 1136f, 680f);
                Assert.IsTrue(straight.TryBuildProjection(straight.Session.GetAllRooms(), rect, true,
                    out LevelMinimapController.MapProjection straightProjection));
                Assert.IsTrue(rotated.TryBuildProjection(rotated.Session.GetAllRooms(), rect, true,
                    out LevelMinimapController.MapProjection rotatedProjection));

                Assert.Less(rotatedProjection.Scale, straightProjection.Scale * 0.95f,
                    "At 45 degrees a diagonal level spans wider on screen: the fit must use the rotated extent.");
                Assert.LessOrEqual(rotatedProjection.Scale, 680f / (100f * Mathf.Sqrt(2f) + 8f) + 0.01f);
                Assert.That(rotatedProjection.WorldCenter.x, Is.EqualTo(50f).Within(6f));
                Assert.That(rotatedProjection.WorldCenter.y, Is.EqualTo(50f).Within(6f));
            }
            finally
            {
                Object.DestroyImmediate(straightGo);
                Object.DestroyImmediate(straightPlayerGo);
                Object.DestroyImmediate(rotatedGo);
                Object.DestroyImmediate(rotatedPlayerGo);
            }
        }

        [Test]
        public void CompactMap_StaysCenteredOnThePlayer()
        {
            GameObject controllerGo = new("MinimapFitCompact");
            GameObject playerGo = new("MinimapFitCompactPlayer");
            try
            {
                playerGo.transform.position = new Vector3(100f, 0f, 50f);
                LevelMinimapController controller = controllerGo.AddComponent<LevelMinimapController>();
                SetPlayerTransform(controller, playerGo.transform);
                controller.Bind(CreateWideLayout());

                Assert.IsTrue(controller.TryBuildProjection(controller.Session.GetAllRooms(),
                    new Rect(0f, 0f, 230f, 178f), false,
                    out LevelMinimapController.MapProjection projection));

                Assert.That(projection.WorldCenter.x, Is.EqualTo(100f).Within(0.01f));
                Assert.That(projection.WorldCenter.y, Is.EqualTo(50f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(controllerGo);
                Object.DestroyImmediate(playerGo);
            }
        }

        private static LevelLayout CreateWideLayout()
        {
            return new LevelLayout(
                new[] { WorldRoom("n_start", 0f, 20f), WorldRoom("n_objective", 300f, 20f), WorldRoom("n_exit", -300f, -80f) },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west"), new DoorConnection("n_objective", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
        }

        private static LevelLayout CreateTinyLayout()
        {
            return new LevelLayout(
                new[] { WorldRoom("n_start", 0f, 0f), WorldRoom("n_objective", 6f, 0f), WorldRoom("n_exit", 12f, 0f) },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west"), new DoorConnection("n_objective", "east", "n_exit", "west") },
                "n_start",
                new[] { "n_exit" });
        }

        private static LevelLayout CreateDiagonalLayout()
        {
            return new LevelLayout(
                new[] { WorldRoom("n_start", 0f, 0f), WorldRoom("n_objective", 100f, 100f) },
                new[] { new DoorConnection("n_start", "east", "n_objective", "west") },
                "n_start",
                new[] { "n_objective" });
        }

        private static PlacedRoom WorldRoom(string nodeId, float x, float z) =>
            new(nodeId, $"template_{nodeId}", new GridPose(0f, 0f, 0),
                new Dictionary<string, DoorState>(), new RoomWorldPose(x, 0f, z, 0f, 0f, 0f, 1f));

        private static void SetPlayerTransform(LevelMinimapController controller, Transform transform)
        {
            SerializedObject serialized = new(controller);
            serialized.FindProperty("playerTransform").objectReferenceValue = transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetRotateWithPlayer(LevelMinimapController controller, bool enabled)
        {
            SerializedObject serialized = new(controller);
            serialized.FindProperty("rotateWithPlayer").boolValue = enabled;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Камера взгляда на игроке: yaw карты берётся с неё, а не со сцены — иначе `Camera.main`
        /// открытого редактора подменил бы угол в тесте.
        /// </summary>
        private static void AddLookCamera(Transform player, float yawDegrees)
        {
            GameObject cameraGo = new("MinimapFitLookCamera");
            cameraGo.transform.SetParent(player, false);
            cameraGo.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);
            cameraGo.AddComponent<Camera>();
        }
    }
}
