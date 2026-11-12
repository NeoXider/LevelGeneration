using System.Collections.Generic;
using NUnit.Framework;
using Neoxider.LevelGeneration.Generation.Domain.Enums;
using Neoxider.LevelGeneration.Generation.Presentation.Runtime;
using UnityEngine;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// <see cref="DoorSocket.VisualOpenChanged"/> is what the game voices as door sounds: it must fire on
    /// every real visible open/close during play and never on generation or checkpoint restore — otherwise
    /// a resumed mission would bang every passed door of the level at once.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class DoorSocketVisualOpenChangedTests
    {
        private GameObject _root;
        private DoorSocket _door;
        private List<bool> _announced;

        [SetUp]
        public void CreateDoor()
        {
            _root = new GameObject("door");
            _door = _root.AddComponent<DoorSocket>();
            _announced = new List<bool>();
            _door.VisualOpenChanged += (_, opened) => _announced.Add(opened);
        }

        [TearDown]
        public void DestroyDoor() => Object.DestroyImmediate(_root);

        [Test]
        public void PlayerApproach_AnnouncesOpening()
        {
            _door.ApplyGeneratedState(DoorState.Closed);

            _door.TrySetTriggeredOpen(true);

            Assert.That(_announced, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void PlayerLeaving_AnnouncesClosing()
        {
            _door.ApplyGeneratedState(DoorState.Closed);
            _door.TrySetTriggeredOpen(true);
            _announced.Clear();

            _door.TrySetTriggeredOpen(false);

            Assert.That(_announced, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void SecondTriggerOfAnOpenDoor_IsNotAnotherOpening()
        {
            _door.ApplyGeneratedState(DoorState.Closed);
            _door.TrySetTriggeredOpen(true);
            _announced.Clear();

            _door.TrySetTriggeredOpen(true);

            Assert.That(_announced, Is.Empty, "the door was already open; nothing moved");
        }

        [Test]
        public void LevelLogicUnlock_AnnouncesOpening()
        {
            _door.ApplyGeneratedState(DoorState.Locked);

            _door.ApplyState(DoorState.Open);

            Assert.That(_announced, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void CheckpointRestore_OpensTheDoorSilently()
        {
            _door.ApplyGeneratedState(DoorState.Locked);

            _door.RestoreState(DoorState.Open);

            Assert.That(_door.IsVisuallyOpen, Is.True, "restore must still open the door");
            Assert.That(_announced, Is.Empty, "a restore is not a door motion");
        }

        [Test]
        public void Generation_IsSilent()
        {
            _door.ApplyGeneratedState(DoorState.Closed);
            _door.TrySetTriggeredOpen(true);
            _announced.Clear();

            _door.ApplyGeneratedState(DoorState.Disabled);

            Assert.That(_door.IsVisuallyOpen, Is.False);
            Assert.That(_announced, Is.Empty);
        }
    }
}
