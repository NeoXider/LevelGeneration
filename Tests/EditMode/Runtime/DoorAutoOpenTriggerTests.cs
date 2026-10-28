using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.Features.LevelGeneration.Generation.Domain.Models;
using RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Runtime
{
    [Category("LevelGeneration")]
    public sealed class DoorAutoOpenTriggerTests
    {
        [Test]
        public void OpenAndCloseWhenSinglePlayerEntersAndLeaves()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            (DoorSocket socket, DoorAutoOpenTrigger trigger) =
                CreateDoorWithTrigger(root, closedVisual, true);
            GameObject player = CreateTaggedCollider("player", "Untagged");

            try
            {
                InvokePrivate(trigger, "OnTriggerEnter", player.GetComponent<Collider>());

                Assert.IsFalse(closedVisual.activeSelf);

                InvokePrivate(trigger, "OnTriggerExit", player.GetComponent<Collider>());

                Assert.IsTrue(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void KeepsOpenWhileAnyPlayerColliderIsInside()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            (_, DoorAutoOpenTrigger trigger) = CreateDoorWithTrigger(root, closedVisual, true);
            GameObject playerA = CreateTaggedCollider("playerA", "Untagged");
            GameObject playerB = CreateTaggedCollider("playerB", "Untagged");

            try
            {
                InvokePrivate(trigger, "OnTriggerEnter", playerA.GetComponent<Collider>());
                InvokePrivate(trigger, "OnTriggerExit", playerA.GetComponent<Collider>());

                Assert.IsTrue(closedVisual.activeSelf);

                InvokePrivate(trigger, "OnTriggerEnter", playerB.GetComponent<Collider>());
                InvokePrivate(trigger, "OnTriggerEnter", playerA.GetComponent<Collider>());
                Assert.IsFalse(closedVisual.activeSelf);

                InvokePrivate(trigger, "OnTriggerExit", playerA.GetComponent<Collider>());
                Assert.IsFalse(closedVisual.activeSelf);

                InvokePrivate(trigger, "OnTriggerExit", playerB.GetComponent<Collider>());
                Assert.IsTrue(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(playerA);
                Object.DestroyImmediate(playerB);
            }
        }

        [Test]
        public void OnTriggerEnter_OnLockedDoor_NotifiesAutoOpenReceiverAsBlocked()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            (_, DoorAutoOpenTrigger trigger) = CreateDoorWithTrigger(root, closedVisual, true);
            DoorAutoOpenTriggerTestReceiver receiver = root.AddComponent<DoorAutoOpenTriggerTestReceiver>();
            DoorSocket socket = root.GetComponentInChildren<DoorSocket>(true);
            GameObject player = CreateTaggedCollider("player", "Untagged");

            try
            {
                SetDoorInitialState(socket, DoorState.Locked);
                socket.ApplyGeneratedState(DoorState.Locked);

                InvokePrivate(trigger, "OnTriggerEnter", player.GetComponent<Collider>());

                CollectionAssert.AreEqual(new[] { false }, receiver.Events);
                Assert.IsTrue(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void RefreshTriggeredOpen_OpensDoor_WhenUnlockedWhilePlayerInside()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            (DoorSocket socket, DoorAutoOpenTrigger trigger) =
                CreateDoorWithTrigger(root, closedVisual, true);
            GameObject player = CreateTaggedCollider("player", "Untagged");

            try
            {
                SetDoorInitialState(socket, DoorState.Locked);
                socket.ApplyGeneratedState(DoorState.Locked);

                // Player walks up to the locked door and stays inside the trigger volume.
                InvokePrivate(trigger, "OnTriggerEnter", player.GetComponent<Collider>());
                Assert.IsTrue(closedVisual.activeSelf);

                // Password terminal unlocks the door while the player is still inside the trigger.
                socket.ApplyState(DoorState.Open);

                Assert.IsFalse(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void ApplyStateOpen_UpdatesSingleAnimatorBoolParameter()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            string controllerPath =
                AssetDatabase.GenerateUniqueAssetPath("Assets/__DoorAutoOpenTriggerTests.controller");
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);

            try
            {
                DoorSocket socket = CreateDoorWithAnimator(root, closedVisual, controller, "DoorOpen");

                socket.ApplyState(DoorState.Open);

                Animator animator = root.GetComponentInChildren<Animator>(true);
                Assert.IsTrue(animator.GetBool("DoorOpen"));
                Assert.IsTrue(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(controllerPath);
            }
        }

        [Test]
        public void SpawnerPreservesClosedRuntimeOnlyNoneDoor()
        {
            GameObject root = new("door");

            try
            {
                DoorSocket socket = root.AddComponent<DoorSocket>();
                SerializedObject socketSo = new(socket);
                socketSo.FindProperty("socketId").stringValue = "east_1";
                socketSo.FindProperty("flowDirection").enumValueIndex = (int)DoorSocketFlowDirection.None;
                socketSo.ApplyModifiedPropertiesWithoutUndo();

                PlacedRoom placed = new(
                    "room",
                    "template",
                    new GridPose(0f, 0f, 0),
                    new Dictionary<string, DoorState> { ["east_1"] = DoorState.Closed });

                System.Type spawnerType = System.Type.GetType(
                    "RedoSchool.Features.LevelGeneration.Runtime.Infrastructure.LevelSpawner, RedoSchool.World");
                Assert.NotNull(spawnerType);

                MethodInfo method = spawnerType.GetMethod(
                    "ShouldPreserveRuntimeOnlyDoor",
                    BindingFlags.Static | BindingFlags.NonPublic);

                Assert.NotNull(method);
                Assert.IsTrue((bool)method.Invoke(null, new object[] { socket, placed }));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void OnTriggerEnter_RuntimeDisabledDoor_DoesNotOpenOrNotify()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            (DoorSocket socket, DoorAutoOpenTrigger trigger) =
                CreateDoorWithTrigger(root, closedVisual, true);
            DoorAutoOpenTriggerTestReceiver receiver = root.AddComponent<DoorAutoOpenTriggerTestReceiver>();
            GameObject player = CreateTaggedCollider("player", "Untagged");

            try
            {
                // InitialState stays Closed (authored default) but the door becomes a runtime dead-end.
                socket.ApplyGeneratedState(DoorState.Disabled);

                InvokePrivate(trigger, "OnTriggerEnter", player.GetComponent<Collider>());

                Assert.IsTrue(closedVisual.activeSelf);
                CollectionAssert.IsEmpty(receiver.Events);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void DisablesAndClosesDoorWhenTriggerComponentIsDisabled()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            (_, DoorAutoOpenTrigger trigger) = CreateDoorWithTrigger(root, closedVisual, false);
            GameObject player = CreateTaggedCollider("player", "Untagged");

            try
            {
                SetDoorTargetDoorField(trigger, null);
                InvokePrivate(trigger, "OnTriggerEnter", player.GetComponent<Collider>());
                Assert.IsFalse(closedVisual.activeSelf);

                InvokePrivate(trigger, "OnDisable");

                Assert.IsTrue(closedVisual.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void OpenedForPlayer_IsRaisedOnlyWhenTheTriggerOpensTheDoorForThePlayer()
        {
            GameObject root = new("door");
            GameObject closedVisual = new("closed");
            (DoorSocket socket, DoorAutoOpenTrigger trigger) = CreateDoorWithTrigger(root, closedVisual, true);
            GameObject player = CreateTaggedCollider("player", "Untagged");
            int opened = 0;
            socket.OpenedForPlayer += _ => opened++;

            try
            {
                socket.ApplyState(DoorState.Open);
                Assert.AreEqual(0, opened, "восстановление состояния двери не должно считаться её открытием игроком");

                InvokePrivate(trigger, "OnTriggerEnter", player.GetComponent<Collider>());
                Assert.AreEqual(1, opened);

                InvokePrivate(trigger, "OnTriggerExit", player.GetComponent<Collider>());
                socket.ApplyState(DoorState.Locked);
                InvokePrivate(trigger, "OnTriggerEnter", player.GetComponent<Collider>());
                Assert.AreEqual(1, opened, "запертая дверь перед игроком не открывается");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(player);
            }
        }

        private static (DoorSocket socket, DoorAutoOpenTrigger trigger) CreateDoorWithTrigger(
            GameObject root,
            GameObject closedVisual,
            bool assignTargetDoor)
        {
            closedVisual.transform.SetParent(root.transform, false);

            DoorSocket socket = root.AddComponent<DoorSocket>();
            SerializedObject socketSo = new(socket);
            socketSo.FindProperty("initialState").enumValueIndex = (int)DoorState.Closed;
            socketSo.FindProperty("closedVisual").objectReferenceValue = closedVisual;
            socketSo.ApplyModifiedPropertiesWithoutUndo();

            socket.ApplyGeneratedState(DoorState.Closed);

            GameObject triggerObject = new("trigger");
            triggerObject.transform.SetParent(root.transform, false);
            triggerObject.AddComponent<BoxCollider>().isTrigger = true;
            DoorAutoOpenTrigger trigger = triggerObject.AddComponent<DoorAutoOpenTrigger>();
            SerializedObject triggerSo = new(trigger);
            triggerSo.FindProperty("playerTag").stringValue = "Untagged";
            triggerSo.ApplyModifiedPropertiesWithoutUndo();
            if (assignTargetDoor)
            {
                SetDoorTargetDoorField(trigger, socket);
            }

            return (socket, trigger);
        }

        private static DoorSocket CreateDoorWithAnimator(
            GameObject root,
            GameObject closedVisual,
            AnimatorController controller,
            string boolParameterName)
        {
            closedVisual.transform.SetParent(root.transform, false);

            Animator animator = root.AddComponent<Animator>();
            controller.AddParameter(boolParameterName, AnimatorControllerParameterType.Bool);
            animator.runtimeAnimatorController = controller;

            DoorSocket socket = root.AddComponent<DoorSocket>();
            SerializedObject socketSo = new(socket);
            socketSo.FindProperty("initialState").enumValueIndex = (int)DoorState.Closed;
            socketSo.FindProperty("closedVisual").objectReferenceValue = closedVisual;
            socketSo.FindProperty("animatorOpenBool").stringValue = "MissingBool";
            socketSo.ApplyModifiedPropertiesWithoutUndo();

            socket.ApplyGeneratedState(DoorState.Closed);
            return socket;
        }

        private static GameObject CreateTaggedCollider(string name, string tag)
        {
            GameObject player = new(name);
            player.tag = tag;
            player.AddComponent<BoxCollider>().isTrigger = true;
            return player;
        }

        private static void SetDoorTargetDoorField(DoorAutoOpenTrigger trigger, DoorSocket targetDoor)
        {
            SerializedObject triggerSo = new(trigger);
            triggerSo.FindProperty("targetDoor").objectReferenceValue = targetDoor;
            triggerSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetDoorInitialState(DoorSocket door, DoorState initialState)
        {
            SerializedObject socketSo = new(door);
            socketSo.FindProperty("initialState").enumValueIndex = (int)initialState;
            socketSo.ApplyModifiedPropertiesWithoutUndo();
        }

        private sealed class DoorAutoOpenTriggerTestReceiver : MonoBehaviour, IDoorAutoOpenReceiver
        {
            public List<bool> Events { get; } = new();

            public void HandleDoorAutoOpen(DoorSocket door, bool isOpen) => Events.Add(isOpen);
        }

        private static void InvokePrivate(object target, string methodName, params object[] parameters)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(target, parameters);
        }
    }
}
