using System;

namespace RedoSchool.LevelGeneration.Runtime.Application
{
    /// <summary>Фаза показа подсказки маршрута: сначала путь на полу, потом пунктир на миникарте.</summary>
    public enum RouteHintRevealPhase
    {
        /// <summary>Подсказки нет: ни ленты на полу, ни пунктира.</summary>
        Hidden = 0,

        /// <summary>Лента проявляется на полу от игрока к цели; миникарта ещё ждёт.</summary>
        FloorRevealing = 1,

        /// <summary>Лента проявилась и держится; пунктир на миникарте включён.</summary>
        FloorHolding = 2,

        /// <summary>The ribbon fades when the student approaches the target or the hint is hidden.</summary>
        FloorFading = 3,

        /// <summary>Лента погасла, пунктир на миникарте остаётся, пока подсказка нужна.</summary>
        FloorDone = 4
    }

    /// <summary>Почему гаснет лента на полу.</summary>
    public enum RouteHintFloorFadeReason
    {
        None = 0,

        /// <summary>
        /// Ученик подошёл к цели ближе заданного порога. Включается только ненулевым
        /// <see cref="RouteHintRevealTiming.ApproachMeters"/>: по умолчанию подсказка держится, пока ученик не
        /// продвинулся в комнату на пути (решение владельца 2026-10-16), а не гаснет от ходьбы по ней.
        /// </summary>
        ApproachedTarget = 1,

        /// <summary>Подсказка больше не нужна или ученика занял интерфейс: гаснет вместе с миникартой.</summary>
        HintHidden = 2
    }

    /// <summary>Длительности и пороги показа ленты; значения приходят из ScriptableObject подсказки.</summary>
    public readonly struct RouteHintRevealTiming
    {
        public readonly float RevealSeconds;
        public readonly float FadeSeconds;
        public readonly float HideFadeSeconds;

        /// <summary>
        /// На сколько метров приблизиться к цели, чтобы лента на полу погасла. Ноль (по умолчанию) — ходьба по пути
        /// подсказку не гасит: она уходит только когда решение о подсказке снято (прогресс, панель).
        /// </summary>
        public readonly float ApproachMeters;

        public RouteHintRevealTiming(float revealSeconds, float fadeSeconds,
            float hideFadeSeconds, float approachMeters = 0f)
        {
            RevealSeconds = Math.Max(0.01f, revealSeconds);
            FadeSeconds = Math.Max(0.01f, fadeSeconds);
            HideFadeSeconds = Math.Max(0.01f, hideFadeSeconds);
            ApproachMeters = Math.Max(0f, approachMeters);
        }
    }

    /// <summary>Входы одного шага показа: что решила подсказка и где сейчас ученик.</summary>
    public readonly struct RouteHintRevealInput
    {
        /// <summary>Подсказка нужна прямо сейчас: решение «показать», путь проложен, ученик в мире.</summary>
        public readonly bool HintVisible;

        /// <summary>Есть чем рисовать ленту на полу. Нет — пунктир на миникарте включается сразу.</summary>
        public readonly bool FloorAvailable;

        /// <summary>Номер пути: смена цели начинает проявление ленты заново.</summary>
        public readonly int PathVersion;

        /// <summary>Сколько метров осталось ученику до цели по пути подсказки.</summary>
        public readonly float RemainingMeters;

        /// <summary>Scaled game time in seconds; reveal and approach fading pause with gameplay.</summary>
        public readonly float Now;

        /// <summary>Unscaled time in seconds; hiding a hint must finish even while gameplay is paused.</summary>
        public readonly float UnscaledNow;

        public RouteHintRevealInput(bool hintVisible, bool floorAvailable, int pathVersion, float remainingMeters,
            float now, float? unscaledNow = null)
        {
            HintVisible = hintVisible;
            FloorAvailable = floorAvailable;
            PathVersion = pathVersion;
            RemainingMeters = Math.Max(0f, remainingMeters);
            Now = now;
            UnscaledNow = unscaledNow ?? now;
        }
    }

    /// <summary>Состояние показа между кадрами; меняет его только <see cref="DecideRouteHintRevealUseCase"/>.</summary>
    public readonly struct RouteHintRevealState
    {
        public readonly RouteHintRevealPhase Phase;
        public readonly float PhaseStartedAt;
        public readonly int PathVersion;

        /// <summary>Остаток пути в момент начала проявления: от него меряется «пошёл к цели».</summary>
        public readonly float StartRemainingMeters;

        public readonly RouteHintFloorFadeReason FadeReason;
        public readonly float FadeFromAlpha;
        public readonly float FadeFromReveal;

        public RouteHintRevealState(RouteHintRevealPhase phase, float phaseStartedAt, int pathVersion,
            float startRemainingMeters, RouteHintFloorFadeReason fadeReason, float fadeFromAlpha,
            float fadeFromReveal)
        {
            Phase = phase;
            PhaseStartedAt = phaseStartedAt;
            PathVersion = pathVersion;
            StartRemainingMeters = startRemainingMeters;
            FadeReason = fadeReason;
            FadeFromAlpha = fadeFromAlpha;
            FadeFromReveal = fadeFromReveal;
        }
    }

    /// <summary>Что рисовать в этом кадре.</summary>
    public readonly struct RouteHintRevealFrame
    {
        public readonly RouteHintRevealPhase Phase;

        /// <summary>Доля ленты от игрока к цели, которая уже проявилась (0…1, без сглаживания).</summary>
        public readonly float FloorReveal;

        /// <summary>Непрозрачность ленты и звёздочек (0…1).</summary>
        public readonly float FloorAlpha;

        /// <summary>Пунктиру на миникарте можно показываться: проявление на полу закончилось.</summary>
        public readonly bool MinimapVisible;

        /// <summary>В этом шаге началось новое проявление — вьюшке пора построить ленту по текущему пути.</summary>
        public readonly bool StartedReveal;

        public RouteHintRevealFrame(RouteHintRevealPhase phase, float floorReveal, float floorAlpha,
            bool minimapVisible, bool startedReveal)
        {
            Phase = phase;
            FloorReveal = floorReveal;
            FloorAlpha = floorAlpha;
            MinimapVisible = minimapVisible;
            StartedReveal = startedReveal;
        }
    }

    /// <summary>
    /// Порядок показа подсказки маршрута (владелец 2026-10-14, план b333 §2b): лента проявляется на полу от
    /// игрока к цели → держится, пока подсказка нужна и ученик не пошёл к цели → гаснет; пунктир на
    /// миникарте включается, когда проявление на полу закончилось, и остаётся, пока подсказка нужна.
    /// Решает только «когда»; КУДА — <see cref="DecideRouteHintUseCase"/>, КАКОЙ ЛИНИЕЙ —
    /// <see cref="PlanRouteHintPathUseCase"/>. Без состояния: состояние держит
    /// <see cref="RouteHintRevealTimeline"/>, время приходит снаружи.
    /// </summary>
    public static class DecideRouteHintRevealUseCase
    {
        public static RouteHintRevealState Execute(RouteHintRevealState state, RouteHintRevealInput input,
            RouteHintRevealTiming timing)
        {
            if (!input.FloorAvailable)
            {
                return input.HintVisible
                    ? new RouteHintRevealState(RouteHintRevealPhase.FloorDone, input.Now, input.PathVersion,
                        input.RemainingMeters, RouteHintFloorFadeReason.None, 0f, 1f)
                    : default;
            }

            float elapsed = PhaseElapsed(state, input);
            switch (state.Phase)
            {
                case RouteHintRevealPhase.FloorRevealing:
                    if (!input.HintVisible)
                    {
                        return Fade(state, input.UnscaledNow, RouteHintFloorFadeReason.HintHidden, 1f,
                            Clamp01(elapsed / timing.RevealSeconds));
                    }

                    if (input.PathVersion != state.PathVersion)
                    {
                        return StartReveal(input);
                    }

                    return elapsed >= timing.RevealSeconds
                        ? new RouteHintRevealState(RouteHintRevealPhase.FloorHolding, input.Now, state.PathVersion,
                            state.StartRemainingMeters, RouteHintFloorFadeReason.None, 1f, 1f)
                        : state;

                case RouteHintRevealPhase.FloorHolding:
                    if (!input.HintVisible)
                    {
                        return Fade(state, input.UnscaledNow, RouteHintFloorFadeReason.HintHidden, 1f, 1f);
                    }

                    if (input.PathVersion != state.PathVersion)
                    {
                        return StartReveal(input);
                    }

                    if (state.StartRemainingMeters - input.RemainingMeters >= timing.ApproachMeters
                        && timing.ApproachMeters > 0f)
                    {
                        return Fade(state, input.Now, RouteHintFloorFadeReason.ApproachedTarget, 1f, 1f);
                    }

                    return state;

                case RouteHintRevealPhase.FloorFading:
                    return AdvanceFade(state, input, timing, elapsed);

                case RouteHintRevealPhase.FloorDone:
                    if (!input.HintVisible)
                    {
                        return default;
                    }

                    return input.PathVersion != state.PathVersion ? StartReveal(input) : state;

                default:
                    return input.HintVisible ? StartReveal(input) : default;
            }
        }

        /// <summary>Что рисовать при данном состоянии. Пунктир миникарты дополнительно требует HintVisible.</summary>
        public static RouteHintRevealFrame Evaluate(RouteHintRevealState state, RouteHintRevealInput input,
            RouteHintRevealTiming timing, bool startedReveal)
        {
            float elapsed = PhaseElapsed(state, input);
            switch (state.Phase)
            {
                case RouteHintRevealPhase.FloorRevealing:
                    return new RouteHintRevealFrame(state.Phase, Clamp01(elapsed / timing.RevealSeconds), 1f, false,
                        startedReveal);

                case RouteHintRevealPhase.FloorHolding:
                    return new RouteHintRevealFrame(state.Phase, 1f, 1f, input.HintVisible, startedReveal);

                case RouteHintRevealPhase.FloorFading:
                    float fade = Clamp01(elapsed / FadeDuration(state.FadeReason, timing));
                    bool minimap = input.HintVisible && state.FadeReason != RouteHintFloorFadeReason.HintHidden;
                    return new RouteHintRevealFrame(state.Phase, state.FadeFromReveal, state.FadeFromAlpha * (1f - fade),
                        minimap, startedReveal);

                case RouteHintRevealPhase.FloorDone:
                    return new RouteHintRevealFrame(state.Phase, 1f, 0f, input.HintVisible, startedReveal);

                default:
                    return new RouteHintRevealFrame(RouteHintRevealPhase.Hidden, 0f, 0f, false, startedReveal);
            }
        }

        private static RouteHintRevealState AdvanceFade(RouteHintRevealState state, RouteHintRevealInput input,
            RouteHintRevealTiming timing, float elapsed)
        {
            bool fadeFinished = elapsed >= FadeDuration(state.FadeReason, timing);
            if (state.FadeReason == RouteHintFloorFadeReason.HintHidden)
            {
                // Подсказку убрали (ученик в панели, вошёл в новую комнату): после угасания показ начнётся с
                // проявления заново, если подсказка снова нужна.
                if (!fadeFinished)
                {
                    return state;
                }

                return input.HintVisible ? StartReveal(input) : default;
            }

            if (!input.HintVisible)
            {
                float alpha = state.FadeFromAlpha * (1f - Clamp01(elapsed / FadeDuration(state.FadeReason, timing)));
                return Fade(state, input.UnscaledNow, RouteHintFloorFadeReason.HintHidden, alpha, state.FadeFromReveal);
            }

            if (input.PathVersion != state.PathVersion)
            {
                return StartReveal(input);
            }

            return fadeFinished
                ? new RouteHintRevealState(RouteHintRevealPhase.FloorDone, input.Now, state.PathVersion,
                    state.StartRemainingMeters, RouteHintFloorFadeReason.None, 0f, 1f)
                : state;
        }

        private static RouteHintRevealState StartReveal(RouteHintRevealInput input) =>
            new(RouteHintRevealPhase.FloorRevealing, input.Now, input.PathVersion, input.RemainingMeters,
                RouteHintFloorFadeReason.None, 1f, 0f);

        private static RouteHintRevealState Fade(RouteHintRevealState state, float now,
            RouteHintFloorFadeReason reason, float fromAlpha, float fromReveal) =>
            new(RouteHintRevealPhase.FloorFading, now, state.PathVersion, state.StartRemainingMeters, reason,
                Clamp01(fromAlpha), Clamp01(fromReveal));

        private static float PhaseElapsed(RouteHintRevealState state, RouteHintRevealInput input) =>
            (state.Phase == RouteHintRevealPhase.FloorFading
             && state.FadeReason == RouteHintFloorFadeReason.HintHidden ? input.UnscaledNow : input.Now)
            - state.PhaseStartedAt;

        private static float FadeDuration(RouteHintFloorFadeReason reason, RouteHintRevealTiming timing) =>
            reason == RouteHintFloorFadeReason.HintHidden ? timing.HideFadeSeconds : timing.FadeSeconds;

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
    }

    /// <summary>
    /// Состояние показа подсказки одной миссии: держит <see cref="RouteHintRevealState"/> между кадрами и
    /// отдаёт кадр. Решение принимает <see cref="DecideRouteHintRevealUseCase"/>; время приходит снаружи,
    /// поэтому порядок показа проверяется без сцены.
    /// </summary>
    public sealed class RouteHintRevealTimeline
    {
        private RouteHintRevealState _state;

        public RouteHintRevealPhase Phase => _state.Phase;
        public RouteHintRevealState State => _state;

        public RouteHintRevealFrame Advance(RouteHintRevealInput input, RouteHintRevealTiming timing)
        {
            RouteHintRevealState previous = _state;
            _state = DecideRouteHintRevealUseCase.Execute(_state, input, timing);
            bool startedReveal = _state.Phase == RouteHintRevealPhase.FloorRevealing
                                 && (previous.Phase != RouteHintRevealPhase.FloorRevealing
                                     || previous.PathVersion != _state.PathVersion
                                     || !previous.PhaseStartedAt.Equals(_state.PhaseStartedAt));
            return DecideRouteHintRevealUseCase.Evaluate(_state, input, timing, startedReveal);
        }

        public void Reset() => _state = default;
    }
}
