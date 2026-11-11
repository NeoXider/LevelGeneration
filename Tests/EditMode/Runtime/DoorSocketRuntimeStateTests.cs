using NUnit.Framework;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Presentation.Runtime;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    [Category("LevelGeneration")]
    public sealed class DoorSocketRuntimeStateTests
    {
        [Test]
        public void ApplyGeneratedState_OpenDoorStartsVisuallyClosed()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            closedVisual.transform.SetParent(root.transform);
            DoorSocket socket = root.AddComponent<DoorSocket>();
            SetClosedVisual(socket, closedVisual);

            try
            {
                socket.ApplyGeneratedState(DoorState.Open);
                Assert.IsTrue(closedVisual.activeSelf);

                socket.ApplyState(DoorState.Open);
                Assert.IsFalse(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ApplyGeneratedState_DisabledDoorKeepsClosedBlockerAndCannotAutoOpen()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            closedVisual.transform.SetParent(root.transform);
            DoorSocket socket = root.AddComponent<DoorSocket>();
            Animator animator = root.AddComponent<Animator>();
            SetDoorVisuals(socket, closedVisual, animator);

            try
            {
                socket.ApplyGeneratedState(DoorState.Disabled);

                Assert.IsTrue(closedVisual.activeSelf);
                Assert.IsTrue(animator.enabled);
                Assert.IsFalse(socket.TrySetTriggeredOpen(true));
                Assert.IsTrue(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ApplyGeneratedState_DisabledDoorDisablesAutoOpenTriggerAndCollider()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            closedVisual.transform.SetParent(root.transform);
            DoorSocket socket = root.AddComponent<DoorSocket>();
            SetClosedVisual(socket, closedVisual);

            GameObject triggerObject = new("auto open trigger");
            triggerObject.transform.SetParent(root.transform);
            BoxCollider collider = triggerObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            DoorAutoOpenTrigger trigger = triggerObject.AddComponent<DoorAutoOpenTrigger>();

            try
            {
                socket.ApplyGeneratedState(DoorState.Disabled);

                Assert.IsFalse(trigger.enabled);
                Assert.IsFalse(collider.enabled);
                Assert.IsFalse(socket.TrySetTriggeredOpen(true));
                Assert.IsTrue(closedVisual.activeSelf);

                socket.ApplyGeneratedState(DoorState.Open);

                Assert.IsTrue(trigger.enabled);
                Assert.IsTrue(collider.enabled);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ApplyState_DisabledAnimatorUsesClosedVisualFallback()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            closedVisual.transform.SetParent(root.transform);
            string controllerPath =
                AssetDatabase.GenerateUniqueAssetPath("Assets/__DoorSocketRuntimeStateTests.controller");
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            controller.AddParameter("IsOpen", AnimatorControllerParameterType.Bool);
            Animator animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.enabled = false;
            DoorSocket socket = root.AddComponent<DoorSocket>();
            SetDoorVisuals(socket, closedVisual, animator);

            try
            {
                socket.ApplyGeneratedState(DoorState.Closed);
                Assert.IsTrue(closedVisual.activeSelf);

                socket.ApplyState(DoorState.Open);
                Assert.IsFalse(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(controllerPath);
            }
        }

        [Test]
        public void ResolveAuthoringWorldCenter_IgnoresInteractionRendererBounds()
        {
            GameObject root = new("door");
            DoorSocket socket = root.AddComponent<DoorSocket>();
            GameObject doorVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            doorVisual.name = "door visual";
            doorVisual.transform.SetParent(root.transform);
            doorVisual.transform.localPosition = new Vector3(0f, 2f, 0f);

            GameObject interactionVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            interactionVisual.name = "interaction marker";
            interactionVisual.transform.SetParent(root.transform);
            interactionVisual.transform.localPosition = new Vector3(0f, -2f, 0f);

            try
            {
                Vector3 center = socket.ResolveAuthoringWorldCenter();

                Assert.That(center.y, Is.EqualTo(2f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ResolveAuthoringWorldCenter_IgnoresAutoOpenTriggerColliderBounds()
        {
            GameObject root = new("door");
            DoorSocket socket = root.AddComponent<DoorSocket>();

            GameObject doorCollider = new("door collider");
            doorCollider.transform.SetParent(root.transform);
            doorCollider.transform.localPosition = new Vector3(0f, 2f, 0f);
            doorCollider.AddComponent<BoxCollider>().size = Vector3.one;

            GameObject trigger = new("auto open trigger");
            trigger.transform.SetParent(root.transform);
            trigger.transform.localPosition = new Vector3(0f, -2f, 0f);
            trigger.AddComponent<BoxCollider>().isTrigger = true;
            trigger.AddComponent<DoorAutoOpenTrigger>();

            try
            {
                Vector3 center = socket.ResolveAuthoringWorldCenter();

                Assert.That(center.y, Is.EqualTo(2f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ResolveAuthoringWorldCenter_UsesOnValidateCachedCenter()
        {
            GameObject root = new("door");
            DoorSocket socket = root.AddComponent<DoorSocket>();
            GameObject doorVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            doorVisual.name = "door visual";
            doorVisual.transform.SetParent(root.transform);
            doorVisual.transform.localPosition = new Vector3(0f, 2f, 0f);

            try
            {
                InvokeOnValidate(socket);
                doorVisual.transform.localPosition = new Vector3(0f, -2f, 0f);

                Vector3 center = socket.ResolveAuthoringWorldCenter();

                Assert.That(center.y, Is.EqualTo(2f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void InvokeOnValidate(DoorSocket socket)
        {
            MethodInfo method = typeof(DoorSocket).GetMethod(
                "OnValidate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(socket, null);
        }

        private static void SetClosedVisual(DoorSocket socket, GameObject closedVisual) => SetDoorVisuals(socket, closedVisual, null);

        private static void SetDoorVisuals(
            DoorSocket socket,
            GameObject closedVisual,
            Animator animator)
        {
            SerializedObject serialized = new(socket);
            serialized.FindProperty("closedVisual").objectReferenceValue = closedVisual;
            serialized.FindProperty("doorAnimator").objectReferenceValue = animator;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
