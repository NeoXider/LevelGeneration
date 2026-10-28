using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using RedoSchool.Features.LevelGeneration.Runtime.Infrastructure;
using RedoSchool.Features.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    /// <summary>Every shipped room needs an active upward floor surface reachable by the production probe.</summary>
    [Category("LevelGeneration")]
    public sealed class RouteHintFloorLayersGuardTests
    {
        private const string RoomPrefabsRoot = "Assets/_source/Prefabs/Rooms";
        /// <summary>Тот же путь Resources, по которому настройки грузит карта: пакет лежит и в Assets, и в Packages.</summary>
        private const string SettingsResourcePath = "Minimap/LevelMinimapSettings";
        private static readonly Vector3 ProbeRoomPosition = new(10000f, 0f, 10000f);

        [Test]
        public void EveryRoomTemplate_HasFloorCollider_OnConfiguredLayers()
        {
            LevelMinimapSettings settings = LoadSettings();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { RoomPrefabsRoot });
            int roomCount = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponent<RoomTemplate>() == null)
                {
                    continue;
                }

                roomCount++;
                GameObject instance = Object.Instantiate(prefab, ProbeRoomPosition, Quaternion.identity);
                try
                {
                    Physics.SyncTransforms();
                    Assert.IsTrue(HasProbeableFloor(instance, settings),
                        $"Room '{path}' has no active upward floor surface under its floor area reachable by "
                        + $"the production downward probe (layer mask {settings.RouteHintFloorLayers.value}). "
                        + "A wall collider or a floor under an inactive ancestor cannot satisfy this guard.");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }

            Assert.Greater(roomCount, 0, "No RoomTemplate prefabs found under " + RoomPrefabsRoot + ".");
        }

        [TestCase(true, true, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        public void FloorGuard_RequiresIncludedActiveFloor_EvenWithIncludedWall(
            bool floorIncluded, bool floorAncestorActive, bool expected)
        {
            LevelMinimapSettings settings = LoadSettings();
            int includedLayer = FindLayer(settings.RouteHintFloorLayers, true);
            int excludedLayer = FindLayer(settings.RouteHintFloorLayers, false);
            GameObject room = new("Floor probe regression room");
            room.transform.position = ProbeRoomPosition;
            GameObject floorParent = new("Floor parent");
            floorParent.transform.SetParent(room.transform, false);
            GameObject floor = new("Floor");
            floor.transform.SetParent(floorParent.transform, false);
            floor.layer = floorIncluded ? includedLayer : excludedLayer;
            BoxCollider floorCollider = floor.AddComponent<BoxCollider>();
            floorCollider.center = Vector3.down * 0.1f;
            floorCollider.size = new Vector3(4f, 0.2f, 4f);
            floorParent.SetActive(floorAncestorActive);
            GameObject wall = new("Included wall");
            wall.transform.SetParent(room.transform, false);
            wall.layer = includedLayer;
            BoxCollider wallCollider = wall.AddComponent<BoxCollider>();
            wallCollider.center = new Vector3(1.9f, 1.5f, 0f);
            wallCollider.size = new Vector3(0.2f, 3f, 4f);
            Physics.SyncTransforms();
            try
            {
                bool actual = HasProbeableFloor(room, settings);

                Assert.AreEqual(expected, actual,
                    "An included wall must not hide an excluded floor or an inactive floor ancestor.");
            }
            finally
            {
                Object.DestroyImmediate(room);
            }
        }

        private static bool HasProbeableFloor(GameObject room, LevelMinimapSettings settings)
        {
            float floorHeight = room.transform.position.y;
            float probeHeight = settings.RouteHintFloorProbeHeightMeters;
            float floorTolerance = settings.RouteHintFloorMaxStepMeters;
            RouteHintFloorRaycastProbe probe = new(settings.RouteHintFloorLayers, probeHeight, null);
            RoomBounds roomBounds = room.GetComponentInChildren<RoomBounds>();
            Collider[] colliders = room.GetComponentsInChildren<Collider>(true);
            foreach (Collider collider in colliders)
            {
                if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Bounds area = collider.bounds;
                for (int x = 1; x <= 3; x++)
                {
                    for (int z = 1; z <= 3; z++)
                    {
                        Vector3 origin = new(Mathf.Lerp(area.min.x, area.max.x, x * 0.25f),
                            floorHeight + probeHeight, Mathf.Lerp(area.min.z, area.max.z, z * 0.25f));
                        if (roomBounds != null && !IsWithinFloorArea(roomBounds.Box, origin))
                        {
                            continue;
                        }

                        // Sample geometry independently of layers: a wall on an included layer cannot substitute
                        // for the actual floor, and wall tops above the room floor are not walkable samples.
                        if (!collider.Raycast(new Ray(origin, Vector3.down), out RaycastHit surface,
                                probeHeight + floorTolerance)
                            || Vector3.Dot(surface.normal, Vector3.up) < 0.7f
                            || Mathf.Abs(surface.point.y - floorHeight) > floorTolerance)
                        {
                            continue;
                        }

                        Vector2 point = new(surface.point.x, surface.point.z);
                        if (probe.Probe(point, floorHeight, out float hitHeight)
                            && Mathf.Abs(hitHeight - surface.point.y) < 0.01f)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsWithinFloorArea(BoxCollider box, Vector3 point)
        {
            Vector3 local = box.transform.InverseTransformPoint(point) - box.center;
            return Mathf.Abs(local.x) <= box.size.x * 0.5f && Mathf.Abs(local.z) <= box.size.z * 0.5f;
        }

        private static LevelMinimapSettings LoadSettings()
        {
            LevelMinimapSettings settings = Resources.Load<LevelMinimapSettings>(SettingsResourcePath);
            Assert.NotNull(settings, "The production minimap settings must exist: Resources/" + SettingsResourcePath);
            Assert.AreNotEqual(0, settings.RouteHintFloorLayers.value, "The floor probe layers must not be empty.");
            return settings;
        }

        private static int FindLayer(LayerMask mask, bool included)
        {
            for (int layer = 0; layer < 32; layer++)
            {
                if (((mask.value & (1 << layer)) != 0) == included)
                {
                    return layer;
                }
            }

            Assert.Fail("The regression fixture needs both included and excluded production probe layers.");
            return 0;
        }
    }
}
