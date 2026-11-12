using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using Neoxider.LevelGeneration.Runtime.Presentation;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    [Category("LevelGeneration")]
    public sealed class MinimapWallTextureBakerTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null)
                {
                    DestroyCreatedObject(_created[i]);
                }
            }

            _created.Clear();
        }

        [Test]
        public void Bake_WithWallParent_CreatesWallSegments()
        {
            GameObject room = CreateRoom(out RoomTemplate template);
            Transform walls = CreateWallRoot(room.transform, "Walls", new Vector3(3f, 0f, 5f));
            SetPrivate(template, "minimapWallParent", walls.gameObject);
            InvalidateWallCaches(template);

            MinimapWallTexture baked = MinimapWallTextureBaker.Bake(template);
            try
            {
                Assert.IsNotNull(baked);
                Assert.IsNotNull(baked.Segments);
                Assert.Greater(baked.Segments.Count, 0);
                Assert.IsTrue(baked.WorldRect.Contains(new Vector2(3f, 5f)));
            }
            finally
            {
                baked?.Dispose();
            }
        }

        [Test]
        public void Bake_WithWallParent_IgnoresManualRoots()
        {
            GameObject room = CreateRoom(out RoomTemplate template);
            Transform parentWalls = CreateWallRoot(room.transform, "WallsParent", Vector3.zero);
            Transform manualWalls = CreateWallRoot(room.transform, "ManualWalls", new Vector3(10f, 0f, 10f));
            SetPrivate(template, "minimapWallParent", parentWalls.gameObject);
            SetPrivate(template, "minimapWallRoots", new List<Transform> { manualWalls });
            InvalidateWallCaches(template);

            MinimapWallTexture baked = MinimapWallTextureBaker.Bake(template);
            try
            {
                Assert.IsNotNull(baked);
                Assert.IsTrue(baked.WorldRect.Contains(Vector2.zero));
                Assert.IsFalse(baked.WorldRect.Contains(new Vector2(10f, 10f)));
            }
            finally
            {
                baked?.Dispose();
            }
        }

        [Test]
        public void Bake_WithRoomRootAsWallParent_IgnoresFloorRenderers()
        {
            GameObject room = CreateRoom(out RoomTemplate template);
            CreateWallRoot(room.transform, "SM_Bld_Wall_02", Vector3.zero);
            CreateWallRoot(room.transform, "SM_Bld_Floor_Large_Walkway", new Vector3(20f, 0f, 20f));
            SetPrivate(template, "minimapWallParent", room);
            InvalidateWallCaches(template);

            MinimapWallTexture baked = MinimapWallTextureBaker.Bake(template);
            try
            {
                Assert.IsNotNull(baked);
                Assert.IsTrue(baked.WorldRect.Contains(Vector2.zero));
                Assert.IsFalse(baked.WorldRect.Contains(new Vector2(20f, 20f)));
            }
            finally
            {
                baked?.Dispose();
            }
        }

        [Test]
        public void Bake_WhenWallGeometryDisabled_ReturnsNull()
        {
            GameObject room = CreateRoom(out RoomTemplate template);
            Transform walls = CreateWallRoot(room.transform, "Walls", Vector3.zero);
            SetPrivate(template, "minimapWallParent", walls.gameObject);
            SetPrivate(template, "useMinimapWallGeometry", false);
            InvalidateWallCaches(template);

            MinimapWallTexture baked = MinimapWallTextureBaker.Bake(template);

            Assert.IsNull(baked);
        }

        [Test]
        public void Bake_WithNonReadableMesh_StillCreatesWallSegments()
        {
            GameObject room = CreateRoom(out RoomTemplate template);
            Transform walls = CreateWallRoot(room.transform, "Walls", new Vector3(2f, 0f, 4f));
            MeshFilter meshFilter = walls.GetComponentInChildren<MeshFilter>();
            meshFilter.sharedMesh.UploadMeshData(true);
            SetPrivate(template, "minimapWallParent", walls.gameObject);
            InvalidateWallCaches(template);

            MinimapWallTexture baked = MinimapWallTextureBaker.Bake(template);
            try
            {
                Assert.IsNotNull(baked);
                Assert.IsNotNull(baked.Segments);
                Assert.Greater(baked.Segments.Count, 0);
                Assert.IsTrue(baked.WorldRect.Contains(new Vector2(2f, 4f)));
            }
            finally
            {
                baked?.Dispose();
            }
        }

        private GameObject CreateRoom(out RoomTemplate template)
        {
            GameObject room = Track(new GameObject("MinimapWallBakeRoom"));
            room.hideFlags = HideFlags.DontSave;
            template = room.AddComponent<RoomTemplate>();
            return room;
        }

        private Transform CreateWallRoot(Transform roomRoot, string name, Vector3 localPosition)
        {
            GameObject root = new(name);
            root.transform.SetParent(roomRoot, false);
            root.transform.localPosition = localPosition;

            GameObject wall = new($"{name}_Mesh");
            wall.transform.SetParent(root.transform, false);
            MeshFilter filter = wall.AddComponent<MeshFilter>();
            filter.sharedMesh = Track(CreateWallMesh());
            wall.AddComponent<MeshRenderer>();
            return root.transform;
        }

        private static Mesh CreateWallMesh()
        {
            Mesh mesh = new() { name = "MinimapWallBakeTestMesh", vertices = new[] { new Vector3(-1f, 0f, -0.15f), new Vector3(1f, 0f, -0.15f), new Vector3(1f, 0f, 0.15f), new Vector3(-1f, 0f, 0.15f) }, triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            mesh.RecalculateBounds();
            return mesh;
        }

        private T Track<T>(T obj)
            where T : Object
        {
            _created.Add(obj);
            return obj;
        }

        private static void DestroyCreatedObject(Object obj)
        {
            if (obj is GameObject gameObject)
            {
                MonoBehaviour[] behaviours = gameObject.GetComponentsInChildren<MonoBehaviour>(true);
                Array.Sort(behaviours, CompareDestroyOrder);
                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] != null)
                    {
                        Object.DestroyImmediate(behaviours[i]);
                    }
                }
            }

            Object.DestroyImmediate(obj);
        }

        private static int CompareDestroyOrder(MonoBehaviour left, MonoBehaviour right) => DestroyOrder(left).CompareTo(DestroyOrder(right));

        private static int DestroyOrder(MonoBehaviour behaviour)
        {
            if (behaviour == null)
            {
                return 0;
            }

            string typeName = behaviour.GetType().FullName;
            if (typeName != null && typeName.EndsWith(".InteractiveQuestionWallPanelView", StringComparison.Ordinal))
            {
                return 0;
            }

            if (typeName != null && typeName.EndsWith(".WorldSpaceUiDocumentInput", StringComparison.Ordinal))
            {
                return 10;
            }

            if (string.Equals(typeName, "UnityEngine.UIElements.UIDocument", StringComparison.Ordinal))
            {
                return 100;
            }

            return 1;
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, fieldName);

            switch (value)
            {
                case bool boolValue:
                    field.SetValue(target, boolValue);
                    break;
                case GameObject gameObjectValue:
                    field.SetValue(target, gameObjectValue);
                    break;
                case List<Transform> transforms:
                    field.SetValue(target, transforms);
                    break;
                default:
                    Assert.Fail(
                        $"Unsupported serialized value type for '{fieldName}': {value?.GetType().Name ?? "null"}");
                    break;
            }

            if (target is RoomTemplate template)
            {
                InvalidateWallCaches(template);
            }
        }

        private static void InvalidateWallCaches(RoomTemplate template) => template.InvalidateCachedGeometryForEditorTests();
    }
}
