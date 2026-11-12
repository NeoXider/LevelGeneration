using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Application;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Domain.Models;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using UnityEditor;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Generation
{
    [Category("LevelGeneration")]
    public sealed class RoomPrefabGridValidatorTests
    {
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
            {
                Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        [Test]
        public void ValidatePrefab_AcceptsDoorsOnGlobalSnapGridFromFirstDoor()
        {
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            AddDoor(root, "west", new Vector3(0f, 0f, 2.5f), DoorDirection.West);
            AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);
            RefreshRoomTemplate(root);

            IReadOnlyList<string> issues = root.GetComponent<RoomPrefabGridValidator>().ValidatePrefab();

            Assert.IsEmpty(issues);
        }

        [Test]
        public void ValidatePrefab_RejectsDoorMetadataOutsideFootprint()
        {
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            AddDoor(root, "west", new Vector3(0f, 0f, 2.5f), DoorDirection.West);
            AddDoor(root, "east", new Vector3(7.5f, 0f, 2.5f), DoorDirection.East);
            RefreshRoomTemplate(root);

            IReadOnlyList<string> issues = root.GetComponent<RoomPrefabGridValidator>().ValidatePrefab();

            Assert.IsTrue(issues.Any(issue => issue.Contains("inside the room footprint")));
        }

        [Test]
        public void ValidatePrefab_RejectsBoundsLargerThanFootprint()
        {
            GameObject root = CreateRoom(new Vector3(7.6f, 3f, 5f), new Vector3(3.8f, 1.5f, 2.5f));
            SetFootprint(root, 3, 2);
            AddDoor(root, "west", new Vector3(0f, 0f, 2.5f), DoorDirection.West);
            AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);
            RefreshRoomTemplate(root);

            IReadOnlyList<string> issues = root.GetComponent<RoomPrefabGridValidator>().ValidatePrefab();

            Assert.IsTrue(issues.Any(issue => issue.Contains("footprint")));
        }

        [Test]
        public void ValidatePrefab_RejectsSocketIdDirectionMismatch()
        {
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            AddDoor(root, "east", new Vector3(0f, 0f, 2.5f), DoorDirection.West);
            AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);
            RefreshRoomTemplate(root);

            IReadOnlyList<string> issues = root.GetComponent<RoomPrefabGridValidator>().ValidatePrefab();

            Assert.IsTrue(issues.Any(issue => issue.Contains("name/id implies")));
        }

        [Test]
        public void ValidatePrefab_AllowsDoorPivotInsideFootprint()
        {
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            // Разные id: одинаковые id сокетов — отдельный дефект (ValidatePrefab_RejectsDuplicateSocketIds).
            AddDoor(root, "east_inner", new Vector3(0f, 0f, 2.5f), DoorDirection.East);
            AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);
            RefreshRoomTemplate(root);

            IReadOnlyList<string> issues = root.GetComponent<RoomPrefabGridValidator>().ValidatePrefab();

            Assert.IsEmpty(issues);
        }

        [Test]
        public void ValidatePrefab_RejectsDuplicateSocketIds()
        {
            // Состояние двери адресуется по SocketId: две подключаемые двери «east» молча делили бы одно состояние.
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            AddDoor(root, "east", new Vector3(0f, 0f, 2.5f), DoorDirection.East);
            AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);
            RefreshRoomTemplate(root);

            IReadOnlyList<string> issues = root.GetComponent<RoomPrefabGridValidator>().ValidatePrefab();

            Assert.IsTrue(
                issues.Any(issue => issue.Contains("several door sockets with id 'east'")),
                "Duplicate connectable socket ids must be reported by the prefab contract.");
        }

        [Test]
        public void RefreshChildReferences_DeduplicatesDoorList()
        {
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            DoorSocket west = AddDoor(root, "west", new Vector3(0f, 0f, 2.5f), DoorDirection.West);
            DoorSocket east = AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);

            SetRoomDoorReferences(root, west, west, east);
            root.GetComponent<RoomTemplate>().RefreshChildReferences();

            CollectionAssert.AreEquivalent(new[] { west, east }, root.GetComponent<RoomTemplate>().Doors);
            Assert.AreEqual(2, root.GetComponent<RoomTemplate>().Doors.Count);
        }

        [Test]
        public void OnValidate_RefreshesChildReferencesWhenOutdated()
        {
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            DoorSocket west = AddDoor(root, "west", new Vector3(0f, 0f, 2.5f), DoorDirection.West);
            AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);

            root.GetComponent<RoomTemplate>().RefreshChildReferences();
            SetRoomDoorReferences(root, west);
            InvokeOnValidate(root.GetComponent<RoomTemplate>());

            Assert.AreEqual(2, root.GetComponent<RoomTemplate>().Doors.Count);
        }

        [Test]
        public void DoorsView_UsesDoorSocketCenterWhenSocketIsOnOpeningEdge()
        {
            GameObject root = CreateRoom(new Vector3(10f, 3f, 5f), new Vector3(5f, 1.5f, 2.5f));
            AddDoor(root, "west", new Vector3(0f, 0f, 5f), DoorDirection.West);
            AddDoor(root, "east", new Vector3(10f, 0f, 0f), DoorDirection.East);
            RefreshRoomTemplate(root);
            root.SetActive(false);

            IReadOnlyList<IDoorSocketDescriptor> doors = ((IRoomTemplateDescriptor)root.GetComponent<RoomTemplate>())
                .Doors;

            AssertConnection(doors, "west", 0f, 5f);
            AssertConnection(doors, "east", 10f, 0f);
        }

        [Test]
        public void DoorsView_UsesVisibleDoorCenterNotSocketPivot()
        {
            // CONTRACT: docking aligns by the visible door center, never the socket/room pivot. Here the door's
            // visible geometry sits at world x=3 while the socket transform (pivot) is at x=5; the resolved
            // connection center must follow the visible door (x=3), so two rooms mate door-center to door-center.
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            DoorSocket east = AddDoor(root, "east", new Vector3(5f, 0f, 2.5f), DoorDirection.East);
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "door_visual";
            visual.transform.SetParent(east.transform, false);
            visual.transform.localPosition = new Vector3(-2f, 0f, 0f);
            RefreshRoomTemplate(root);
            root.SetActive(false);

            IReadOnlyList<IDoorSocketDescriptor> doors = ((IRoomTemplateDescriptor)root.GetComponent<RoomTemplate>())
                .Doors;

            AssertConnection(doors, "east", 3f, 2.5f);
        }

        [Test]
        public void DoorsView_UsesInsideSocketCenter_WhenNoVisualCenterExists()
        {
            GameObject root = CreateRoom(new Vector3(5f, 3f, 5f), new Vector3(2.5f, 1.5f, 2.5f));
            AddDoor(root, "east", new Vector3(1.5f, 0f, 2.5f), DoorDirection.East);
            RefreshRoomTemplate(root);
            root.SetActive(false);

            IReadOnlyList<IDoorSocketDescriptor> doors = ((IRoomTemplateDescriptor)root.GetComponent<RoomTemplate>())
                .Doors;

            AssertConnection(doors, "east", 1.5f, 2.5f);
        }

        private GameObject CreateRoom(Vector3 boundsSize, Vector3 boundsCenter)
        {
            GameObject root = new("room");
            _created.Add(root);
            root.AddComponent<RoomTemplate>();
            if (root.GetComponent<RoomPrefabGridValidator>() == null)
            {
                root.AddComponent<RoomPrefabGridValidator>();
            }

            GameObject boundsGo = new("bounds");
            boundsGo.transform.SetParent(root.transform, false);
            BoxCollider box = boundsGo.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = boundsSize;
            box.center = boundsCenter;
            RoomBounds roomBounds = boundsGo.AddComponent<RoomBounds>();

            RoomTemplate room = root.GetComponent<RoomTemplate>();
            SerializedObject so = new(room);
            so.FindProperty("bounds").objectReferenceValue = roomBounds;
            so.FindProperty("widthCells").intValue = Mathf.Max(1,
                Mathf.CeilToInt(boundsSize.x / LevelGenerationGridMetrics.CellSizeMeters));
            so.FindProperty("depthCells").intValue = Mathf.Max(1,
                Mathf.CeilToInt(boundsSize.z / LevelGenerationGridMetrics.CellSizeMeters));
            so.ApplyModifiedPropertiesWithoutUndo();
            return root;
        }

        private static DoorSocket AddDoor(GameObject root, string id, Vector3 localPosition, DoorDirection direction)
        {
            GameObject doorGo = new(id);
            doorGo.transform.SetParent(root.transform, false);
            doorGo.transform.localPosition = localPosition;
            DoorSocket door = doorGo.AddComponent<DoorSocket>();

            SerializedObject so = new(door);
            so.FindProperty("socketId").stringValue = id;
            so.FindProperty("direction").enumValueIndex = (int)direction;
            Vector2 origin = ResolveGridOrigin(root);
            float offset = direction == DoorDirection.East || direction == DoorDirection.West
                ? localPosition.z - origin.y
                : localPosition.x - origin.x;
            so.FindProperty("gridOffsetSteps").intValue =
                Mathf.RoundToInt(offset / LevelGenerationGridMetrics.DoorSnapMeters);
            so.FindProperty("localXSteps").intValue =
                Mathf.RoundToInt((localPosition.x - origin.x) / LevelGenerationGridMetrics.DoorSnapMeters);
            so.FindProperty("localZSteps").intValue =
                Mathf.RoundToInt((localPosition.z - origin.y) / LevelGenerationGridMetrics.DoorSnapMeters);
            so.FindProperty("gridOriginXMeters").floatValue = origin.x;
            so.FindProperty("gridOriginZMeters").floatValue = origin.y;
            so.FindProperty("localYawDegrees").floatValue = direction switch
            {
                DoorDirection.South => 90f,
                DoorDirection.West => 180f,
                DoorDirection.North => 270f,
                _ => 0f
            };
            so.ApplyModifiedPropertiesWithoutUndo();
            return door;
        }

        private static Vector2 ResolveGridOrigin(GameObject root)
        {
            DoorSocket firstDoor = root.GetComponentInChildren<DoorSocket>(true);
            if (firstDoor == null)
            {
                return Vector2.zero;
            }

            Vector3 local = root.transform.InverseTransformPoint(firstDoor.transform.position);
            return new Vector2(local.x, local.z);
        }

        private static void RefreshRoomTemplate(GameObject root)
        {
            RoomTemplate room = root.GetComponent<RoomTemplate>();
            DoorSocket[] doors = root.GetComponentsInChildren<DoorSocket>(true);
            SerializedObject so = new(room);
            SerializedProperty list = so.FindProperty("doors");
            list.arraySize = doors.Length;
            for (int i = 0; i < doors.Length; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = doors[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetRoomDoorReferences(GameObject root, params DoorSocket[] doors)
        {
            RoomTemplate room = root.GetComponent<RoomTemplate>();
            SerializedObject so = new(room);
            SerializedProperty list = so.FindProperty("doors");
            list.arraySize = doors.Length;
            for (int i = 0; i < doors.Length; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = doors[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFootprint(GameObject root, int widthCells, int depthCells)
        {
            RoomTemplate room = root.GetComponent<RoomTemplate>();
            SerializedObject so = new(room);
            so.FindProperty("widthCells").intValue = widthCells;
            so.FindProperty("depthCells").intValue = depthCells;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void InvokeOnValidate(RoomTemplate roomTemplate) => roomTemplate.ValidateForEditorTests();

        private static void AssertConnection(
            IReadOnlyList<IDoorSocketDescriptor> doors,
            string socketId,
            float expectedX,
            float expectedZ)
        {
            IDoorSocketDescriptor door = doors.First(candidate => candidate.SocketId == socketId);
            Assert.That(door.ConnectionLocalXMeters(), Is.EqualTo(expectedX).Within(0.001f));
            Assert.That(door.ConnectionLocalZMeters(), Is.EqualTo(expectedZ).Within(0.001f));
        }
    }
}
