using NUnit.Framework;
using Neoxider.LevelGeneration.Runtime.Application;

namespace Neoxider.LevelGeneration.Tests.Runtime
{
    /// <summary>
    /// Порядок показа подсказки маршрута (план b333 §2b): лента проявляется на полу от игрока к цели,
    /// держится, пока подсказка нужна и игрок не пошёл к цели, и гаснет; пунктир на миникарте ждёт конца
    /// проявления на полу. Время подаётся снаружи — кадры не нужны.
    /// </summary>
    [Category("LevelGeneration")]
    public sealed class RouteHintRevealTimelineTests
    {
        private static readonly RouteHintRevealTiming Timing = new(1.2f, 0.8f, 0.3f, 2f);
        private const float Remaining = 12f;

        /// <summary>Шаг за порог: время во float не обязано совпасть с границей фазы точно.</summary>
        private const float Margin = 0.01f;

        [Test]
        public void HintShown_FloorRevealsFirst_MinimapWaitsForTheFloor()
        {
            RouteHintRevealTimeline timeline = new();
            timeline.Advance(Visible(0f), Timing);

            RouteHintRevealFrame frame = timeline.Advance(Visible(Timing.RevealSeconds * 0.5f), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorRevealing, frame.Phase);
            Assert.AreEqual(0.5f, frame.FloorReveal, 0.001f, "The ribbon grows from the player to the target.");
            Assert.AreEqual(1f, frame.FloorAlpha);
            Assert.IsFalse(frame.MinimapVisible, "The minimap dashed line waits for the floor reveal to finish.");
        }

        [Test]
        public void HintShown_FirstStep_AsksTheViewToBuildTheRibbon()
        {
            RouteHintRevealTimeline timeline = new();

            RouteHintRevealFrame frame = timeline.Advance(Visible(0f), Timing);

            Assert.IsTrue(frame.StartedReveal, "A new reveal must rebuild the ribbon from the current path.");
            Assert.AreEqual(0f, frame.FloorReveal);
        }

        [Test]
        public void FloorRevealFinished_MinimapTurnsOn_FloorHolds()
        {
            RouteHintRevealTimeline timeline = new();
            timeline.Advance(Visible(0f), Timing);

            RouteHintRevealFrame frame = timeline.Advance(Visible(Timing.RevealSeconds), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, frame.Phase);
            Assert.AreEqual(1f, frame.FloorReveal);
            Assert.AreEqual(1f, frame.FloorAlpha);
            Assert.IsTrue(frame.MinimapVisible, "The minimap dashed line turns on when the floor reveal finishes.");
        }

        [Test]
        public void PlayerWalksTowardTarget_FloorFades_MinimapStays()
        {
            RouteHintRevealTimeline timeline = HoldingTimeline(out float holdStart);
            float walked = Remaining - Timing.ApproachMeters;

            timeline.Advance(Visible(holdStart + 0.1f, walked), Timing);
            RouteHintRevealFrame frame = timeline.Advance(Visible(holdStart + 0.1f + Timing.FadeSeconds * 0.5f,
                walked), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorFading, frame.Phase);
            Assert.AreEqual(0.5f, frame.FloorAlpha, 0.001f, "The ribbon fades once the student goes to the target.");
            Assert.IsTrue(frame.MinimapVisible, "The minimap keeps the route while the hint is due.");
        }

        [Test]
        public void PlayerWalksAllTheWayToTheTarget_WithoutApproachFade_NothingGoesOut()
        {
            RouteHintRevealTiming persistent = new(1.2f, 0.8f, 0.3f);
            RouteHintRevealTimeline timeline = new();
            timeline.Advance(Visible(0f), persistent);
            timeline.Advance(Visible(persistent.RevealSeconds), persistent);

            for (float remaining = Remaining; remaining >= 0f; remaining -= 0.5f)
            {
                float now = persistent.RevealSeconds + 1f + (Remaining - remaining) * 3f;
                RouteHintRevealFrame frame = timeline.Advance(Visible(now, remaining), persistent);

                Assert.AreEqual(RouteHintRevealPhase.FloorHolding, frame.Phase,
                    $"{remaining:0.0} m from the target the floor stars still hold: walking along the hint keeps it.");
                Assert.AreEqual(1f, frame.FloorAlpha);
                Assert.IsTrue(frame.MinimapVisible);
            }
        }

        [Test]
        public void ApproachFade_IsOffUnlessTheTimingAsksForIt()
        {
            Assert.AreEqual(0f, new RouteHintRevealTiming(1f, 1f, 1f).ApproachMeters);
        }

        [Test]
        public void PlayerWalksAway_FloorKeepsHoldingWhileHintIsDue()
        {
            RouteHintRevealTimeline timeline = HoldingTimeline(out float holdStart);

            RouteHintRevealFrame early = timeline.Advance(Visible(holdStart + 3f,
                Remaining + 5f), Timing);
            RouteHintRevealFrame late = timeline.Advance(Visible(holdStart + 60f,
                Remaining + 5f), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, early.Phase, "Walking away is not progress.");
            Assert.AreEqual(1f, early.FloorAlpha);
            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, late.Phase,
                "The ribbon holds as long as the hint is due: there is no hold timeout.");
            Assert.AreEqual(1f, late.FloorAlpha);
            Assert.IsTrue(late.MinimapVisible);
        }

        [Test]
        public void FloorFadeFinished_MinimapStaysAndRibbonIsGone()
        {
            RouteHintRevealTimeline timeline = HoldingTimeline(out float holdStart);
            float walked = Remaining - Timing.ApproachMeters;
            timeline.Advance(Visible(holdStart + 0.1f, walked), Timing);
            float fadeStart = holdStart + 0.1f;

            RouteHintRevealFrame frame = timeline.Advance(Visible(fadeStart + Timing.FadeSeconds + Margin,
                walked), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorDone, frame.Phase);
            Assert.AreEqual(0f, frame.FloorAlpha);
            Assert.IsTrue(frame.MinimapVisible);
        }

        [Test]
        public void NoFloorView_MinimapShowsTheRouteRightAway()
        {
            RouteHintRevealTimeline timeline = new();

            RouteHintRevealFrame frame = timeline.Advance(new RouteHintRevealInput(true, false, 1, Remaining, 0f),
                Timing);

            Assert.IsTrue(frame.MinimapVisible, "Without a floor ribbon the minimap must not wait for anything.");
            Assert.AreEqual(0f, frame.FloorAlpha);
            Assert.IsFalse(frame.StartedReveal);
        }

        [Test]
        public void HintHiddenDuringReveal_FloorFadesQuickly_MinimapStaysOff()
        {
            RouteHintRevealTimeline timeline = new();
            timeline.Advance(Visible(0f), Timing);
            timeline.Advance(Visible(Timing.RevealSeconds * 0.5f), Timing);

            RouteHintRevealFrame frame = timeline.Advance(Hidden(Timing.RevealSeconds * 0.5f), Timing);
            RouteHintRevealFrame gone = timeline.Advance(
                Hidden(Timing.RevealSeconds * 0.5f + Timing.HideFadeSeconds + Margin), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorFading, frame.Phase);
            Assert.AreEqual(0.5f, frame.FloorReveal, 0.001f, "The fading ribbon keeps its revealed part.");
            Assert.IsFalse(frame.MinimapVisible);
            Assert.AreEqual(RouteHintRevealPhase.Hidden, gone.Phase);
        }

        [Test]
        public void HintShownAgainAfterPanel_FloorRevealsFromThePlayerAgain()
        {
            RouteHintRevealTimeline timeline = HoldingTimeline(out float holdStart);
            timeline.Advance(Hidden(holdStart + 1f), Timing);
            timeline.Advance(Hidden(holdStart + 1f + Timing.HideFadeSeconds + Margin), Timing);

            RouteHintRevealFrame frame = timeline.Advance(Visible(holdStart + 5f), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorRevealing, frame.Phase);
            Assert.IsTrue(frame.StartedReveal);
            Assert.IsFalse(frame.MinimapVisible, "Back from the panel: floor first, minimap after it again.");
        }

        [Test]
        public void NewTargetWhileHolding_RestartsTheFloorReveal()
        {
            RouteHintRevealTimeline timeline = HoldingTimeline(out float holdStart);

            RouteHintRevealFrame frame = timeline.Advance(
                new RouteHintRevealInput(true, true, 2, Remaining, holdStart + 1f), Timing);

            Assert.AreEqual(RouteHintRevealPhase.FloorRevealing, frame.Phase);
            Assert.IsTrue(frame.StartedReveal, "A new target needs a new ribbon.");
            Assert.IsFalse(frame.MinimapVisible);
        }

        private static RouteHintRevealTimeline HoldingTimeline(out float holdStart)
        {
            RouteHintRevealTimeline timeline = new();
            timeline.Advance(Visible(0f), Timing);
            holdStart = Timing.RevealSeconds;
            RouteHintRevealFrame frame = timeline.Advance(Visible(holdStart), Timing);
            Assert.AreEqual(RouteHintRevealPhase.FloorHolding, frame.Phase, "Precondition: the ribbon is holding.");
            return timeline;
        }

        private static RouteHintRevealInput Visible(float now, float remaining = Remaining) =>
            new(true, true, 1, remaining, now);

        private static RouteHintRevealInput Hidden(float now) => new(false, true, 1, Remaining, now);
    }
}
