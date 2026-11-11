using System.Collections.Generic;
using NUnit.Framework;
using RedoSchool.LevelGeneration.Generation.Application;
using RedoSchool.LevelGeneration.Generation.Application.Pipeline;
using RedoSchool.LevelGeneration.Generation.Application.V2.Domain;
using RedoSchool.LevelGeneration.Generation.Application.V2.Facade;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.Tests.EditMode.LevelGeneration.Generation
{
    /// <summary>
    /// Regression coverage for audit findings H6 (deterministic fallback room selection) and
    /// H7 (platform-independent, single-source retry-stage ladder).
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class RetryLadderAndFallbackDeterminismTests
    {
        // --- H7: single source of truth for the retry-stage ladder ---------------------------------

        [Test]
        public void RetryLadder_TrimOptionalPolicy_ReturnsFullOrderedLadder()
        {
            IReadOnlyList<LayoutRetryStage> stages =
                LayoutPacingMissionGenerator.ResolveRetryStages(LayoutRetryPolicy.TrimOptional);

            CollectionAssert.AreEqual(
                new[] { LayoutRetryStage.Full, LayoutRetryStage.TrimBranches, LayoutRetryStage.DisableOptionalInteractive, LayoutRetryStage.MandatoryOnly },
                stages);
        }

        [Test]
        public void RetryLadder_FailFastOrNonePolicy_ReturnsSingleFullStage()
        {
            CollectionAssert.AreEqual(
                new[] { LayoutRetryStage.Full },
                LayoutPacingMissionGenerator.ResolveRetryStages(LayoutRetryPolicy.FailFast));
            CollectionAssert.AreEqual(
                new[] { LayoutRetryStage.Full },
                LayoutPacingMissionGenerator.ResolveRetryStages(LayoutRetryPolicy.None));
        }

        [Test]
        public void RetryLadder_IsStableAcrossRepeatedCalls()
        {
            // The ladder is the single source consumed by both MissionGenerator (V1 facade) and
            // LayoutPacingMissionGenerator (V2). There is no platform fork, so the same ordered stages
            // are produced on every platform/run; here we assert call-to-call stability of that source.
            IReadOnlyList<LayoutRetryStage> first =
                LayoutPacingMissionGenerator.ResolveRetryStages(LayoutRetryPolicy.TrimOptional);
            IReadOnlyList<LayoutRetryStage> second =
                LayoutPacingMissionGenerator.ResolveRetryStages(LayoutRetryPolicy.TrimOptional);

            CollectionAssert.AreEqual(first, second);
        }

        // --- H6: deterministic fallback room selection ----------------------------------------------

        [Test]
        public void Fallback_PicksLowestRoomId_RegardlessOfPoolBuildOrder()
        {
            // Doorless rooms fail the main path's connectable-door requirement, forcing the fallback
            // path. Fallback used to iterate the pool in build order, so the chosen room depended on
            // RoomDatabase enumeration order (Resources/AssetDatabase) and could differ per platform.
            IRoomTemplateDescriptor RoomB()
            {
                return MakeDoorlessPassage("passage_b");
            }

            IRoomTemplateDescriptor RoomA()
            {
                return MakeDoorlessPassage("passage_a");
            }

            IRoomTemplateDescriptor RoomC()
            {
                return MakeDoorlessPassage("passage_c");
            }

            RoomSelector selector = new(TestInteractiveRoomKinds.Standard);
            RoomNode node = new("n_explore", RoomKind.Passage, 2);

            string forwardOrder = selector.Select(
                node,
                new FakeMissionConfig(),
                new FakeTileset(),
                new FakeRoomDatabase(new[] { RoomA(), RoomB(), RoomC() }),
                new FirstRollRng())?.RoomId;

            string reversedOrder = selector.Select(
                node,
                new FakeMissionConfig(),
                new FakeTileset(),
                new FakeRoomDatabase(new[] { RoomC(), RoomB(), RoomA() }),
                new FirstRollRng())?.RoomId;

            Assert.AreEqual("passage_a", forwardOrder, "Fallback should pick the lowest RoomId.");
            Assert.AreEqual(forwardOrder, reversedOrder,
                "Fallback selection must be independent of the pool build order.");
        }

        [Test]
        public void RoomDatabase_OrdersRoomsByRoomId_RegardlessOfInsertionOrder()
        {
            RedoSchool.LevelGeneration.Generation.Infrastructure.RoomDatabase forward =
                new(new[] { MakeDoorlessPassage("passage_a"), MakeDoorlessPassage("passage_b"), MakeDoorlessPassage("passage_c") });
            RedoSchool.LevelGeneration.Generation.Infrastructure.RoomDatabase reversed =
                new(new[] { MakeDoorlessPassage("passage_c"), MakeDoorlessPassage("passage_b"), MakeDoorlessPassage("passage_a") });

            string[] forwardIds = ToRoomIds(forward.GetRoomsForTileset("test"));
            string[] reversedIds = ToRoomIds(reversed.GetRoomsForTileset("test"));

            CollectionAssert.AreEqual(new[] { "passage_a", "passage_b", "passage_c" }, forwardIds);
            CollectionAssert.AreEqual(forwardIds, reversedIds);
        }

        private static FakeRoomTemplate MakeDoorlessPassage(string roomId)
        {
            return new FakeRoomTemplate(
                roomId,
                RoomKind.Passage,
                0,
                1,
                System.Array.Empty<IDoorSocketDescriptor>());
        }

        private static string[] ToRoomIds(IReadOnlyList<IRoomTemplateDescriptor> rooms)
        {
            string[] ids = new string[rooms.Count];
            for (int i = 0; i < rooms.Count; i++)
            {
                ids[i] = rooms[i].RoomId;
            }

            return ids;
        }
    }
}
