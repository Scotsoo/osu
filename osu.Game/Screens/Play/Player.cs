// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics.Containers;
using osu.Game.Online.API;
using osu.Game.Overlays;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Screens.Ranking;
using osu.Game.Skinning;
using osu.Game.Storyboards.Drawables;

namespace osu.Game.Screens.Play
{
    public class Player : ScreenWithBeatmapBackground
    {
        protected override bool AllowBackButton => false; // handled by HoldForMenuButton

        public override float BackgroundParallaxAmount => 0.1f;

        public override bool HideOverlaysOnEnter => true;

        public override OverlayActivation InitialOverlayActivationMode => OverlayActivation.UserTriggered;

        public Action RestartRequested;

        public bool HasFailed { get; private set; }

        public bool AllowPause { get; set; } = true;
        public bool AllowLeadIn { get; set; } = true;
        public bool AllowResults { get; set; } = true;

        private Bindable<bool> mouseWheelDisabled;

        private readonly Bindable<bool> storyboardReplacesBackground = new Bindable<bool>();

        public int RestartCount;

        [Resolved]
        private ScoreManager scoreManager { get; set; }

        protected PausableGameplayContainer PausableGameplayContainer { get; private set; }

        private RulesetInfo ruleset;

        private IAPIProvider api;

        private SampleChannel sampleRestart;

        protected ScoreProcessor ScoreProcessor { get; private set; }
        protected RulesetContainer RulesetContainer { get; private set; }

        protected HUDOverlay HUDOverlay { get; private set; }
        private FailOverlay failOverlay;

        #region Storyboard

        private DrawableStoryboard storyboard;
        protected UserDimContainer StoryboardContainer { get; private set; }

        private void initializeStoryboard(bool asyncLoad)
        {
            if (StoryboardContainer == null || storyboard != null)
                return;

            if (!ShowStoryboard.Value)
                return;

            var beatmap = Beatmap.Value;

            storyboard = beatmap.Storyboard.CreateDrawable();
            storyboard.Masking = true;

            if (asyncLoad)
                LoadComponentAsync(storyboard, StoryboardContainer.Add);
            else
                StoryboardContainer.Add(storyboard);
        }

        #endregion

        protected virtual UserDimContainer CreateStoryboardContainer() => new UserDimContainer(true)
        {
            RelativeSizeAxes = Axes.Both,
            Alpha = 1,
            EnableUserDim = { Value = true }
        };

        public bool LoadedBeatmapSuccessfully => RulesetContainer?.Objects.Any() == true;

        protected GameplayClockContainer GameplayClockContainer { get; private set; }

        [BackgroundDependencyLoader]
        private void load(AudioManager audio, IAPIProvider api, OsuConfigManager config)
        {
            this.api = api;

            WorkingBeatmap working = loadBeatmap();

            if (working == null)
                return;

            sampleRestart = audio.Sample.Get(@"Gameplay/restart");

            mouseWheelDisabled = config.GetBindable<bool>(OsuSetting.MouseDisableWheel);

            ScoreProcessor = RulesetContainer.CreateScoreProcessor();
            if (!ScoreProcessor.Mode.Disabled)
                config.BindWith(OsuSetting.ScoreDisplayMode, ScoreProcessor.Mode);

            InternalChild = GameplayClockContainer = new GameplayClockContainer(working, AllowLeadIn, RulesetContainer.GameplayStartTime);

            GameplayClockContainer.Children = new Drawable[]
            {
                PausableGameplayContainer = new PausableGameplayContainer
                {
                    Retries = RestartCount,
                    OnRetry = restart,
                    OnQuit = performUserRequestedExit,
                    RequestResume = completion =>
                    {
                        GameplayClockContainer.Start();
                        completion();
                    },
                    RequestPause = GameplayClockContainer.Stop,
                    IsPaused = { BindTarget = GameplayClockContainer.IsPaused },
                    CheckCanPause = () => CanPause,
                    Children = new[]
                    {
                        StoryboardContainer = CreateStoryboardContainer(),
                        new ScalingContainer(ScalingMode.Gameplay)
                        {
                            Child = new LocalSkinOverrideContainer(working.Skin)
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = RulesetContainer
                            }
                        },
                        new BreakOverlay(working.Beatmap.BeatmapInfo.LetterboxInBreaks, ScoreProcessor)
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Breaks = working.Beatmap.Breaks
                        },
                        // display the cursor above some HUD elements.
                        RulesetContainer.Cursor?.CreateProxy() ?? new Container(),
                        HUDOverlay = new HUDOverlay(ScoreProcessor, RulesetContainer, working)
                        {
                            HoldToQuit = { Action = performUserRequestedExit },
                            PlayerSettingsOverlay = { PlaybackSettings = { UserPlaybackRate = { BindTarget = GameplayClockContainer.UserPlaybackRate } } },
                            KeyCounter = { Visible = { BindTarget = RulesetContainer.HasReplayLoaded } },
                            RequestSeek = GameplayClockContainer.Seek,
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre
                        },
                        new SkipOverlay(RulesetContainer.GameplayStartTime)
                        {
                            RequestSeek = GameplayClockContainer.Seek
                        },
                    }
                },
                failOverlay = new FailOverlay
                {
                    OnRetry = restart,
                    OnQuit = performUserRequestedExit,
                },
                new HotkeyRetryOverlay
                {
                    Action = () =>
                    {
                        if (!this.IsCurrentScreen()) return;

                        fadeOut(true);
                        restart();
                    },
                }
            };

            // bind clock into components that require it
            RulesetContainer.IsPaused.BindTo(GameplayClockContainer.IsPaused);

            // load storyboard as part of player's load if we can
                initializeStoryboard(false);

            // Bind ScoreProcessor to ourselves
            ScoreProcessor.AllJudged += onCompletion;
            ScoreProcessor.Failed += onFail;

            foreach (var mod in Beatmap.Value.Mods.Value.OfType<IApplicableToScoreProcessor>())
                mod.ApplyToScoreProcessor(ScoreProcessor);
        }

        private WorkingBeatmap loadBeatmap()
        {
            WorkingBeatmap working = Beatmap.Value;
            if (working is DummyWorkingBeatmap)
                return null;

            try
            {
                var beatmap = working.Beatmap;

                if (beatmap == null)
                    throw new InvalidOperationException("Beatmap was not loaded");

                ruleset = Ruleset.Value ?? beatmap.BeatmapInfo.Ruleset;
                var rulesetInstance = ruleset.CreateInstance();

                try
                {
                    RulesetContainer = rulesetInstance.CreateRulesetContainerWith(working);
                }
                catch (BeatmapInvalidForRulesetException)
                {
                    // we may fail to create a RulesetContainer if the beatmap cannot be loaded with the user's preferred ruleset
                    // let's try again forcing the beatmap's ruleset.
                    ruleset = beatmap.BeatmapInfo.Ruleset;
                    rulesetInstance = ruleset.CreateInstance();
                    RulesetContainer = rulesetInstance.CreateRulesetContainerWith(Beatmap.Value);
                }

                if (!RulesetContainer.Objects.Any())
                {
                    Logger.Log("Beatmap contains no hit objects!", level: LogLevel.Error);
                    return null;
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not load beatmap sucessfully!");
                //couldn't load, hard abort!
                return null;
            }

            return working;
        }

        private void performUserRequestedExit()
        {
            if (!this.IsCurrentScreen()) return;

            this.Exit();
        }

        private void restart()
        {
            if (!this.IsCurrentScreen()) return;

            sampleRestart?.Play();
            ValidForResume = false;
            RestartRequested?.Invoke();
            this.Exit();
        }

        private ScheduledDelegate onCompletionEvent;

        private void onCompletion()
        {
            // Only show the completion screen if the player hasn't failed
            if (ScoreProcessor.HasFailed || onCompletionEvent != null)
                return;

            ValidForResume = false;

            if (!AllowResults) return;

            using (BeginDelayedSequence(1000))
            {
                onCompletionEvent = Schedule(delegate
                {
                    if (!this.IsCurrentScreen()) return;

                    var score = CreateScore();
                    if (RulesetContainer.ReplayScore == null)
                        scoreManager.Import(score);

                    this.Push(CreateResults(score));

                    onCompletionEvent = null;
                });
            }
        }

        protected virtual ScoreInfo CreateScore()
        {
            var score = RulesetContainer.ReplayScore?.ScoreInfo ?? new ScoreInfo
            {
                Beatmap = Beatmap.Value.BeatmapInfo,
                Ruleset = ruleset,
                Mods = Beatmap.Value.Mods.Value.ToArray(),
                User = api.LocalUser.Value,
            };

            ScoreProcessor.PopulateScore(score);

            return score;
        }

        private bool onFail()
        {
            if (Beatmap.Value.Mods.Value.OfType<IApplicableFailOverride>().Any(m => !m.AllowFail))
                return false;

            GameplayClockContainer.Stop();

            HasFailed = true;
            failOverlay.Retries = RestartCount;
            failOverlay.Show();
            return true;
        }

        public override void OnEntering(IScreen last)
        {
            base.OnEntering(last);

            if (!LoadedBeatmapSuccessfully)
                return;

            Alpha = 0;
            this
                .ScaleTo(0.7f)
                .ScaleTo(1, 750, Easing.OutQuint)
                .Delay(250)
                .FadeIn(250);

            ShowStoryboard.ValueChanged += _ => initializeStoryboard(true);

            Background.EnableUserDim.Value = true;

            Background.StoryboardReplacesBackground.BindTo(storyboardReplacesBackground);
            StoryboardContainer.StoryboardReplacesBackground.BindTo(storyboardReplacesBackground);

            storyboardReplacesBackground.Value = Beatmap.Value.Storyboard.ReplacesBackground && Beatmap.Value.Storyboard.HasDrawable;

            GameplayClockContainer.Restart();

            PausableGameplayContainer.Alpha = 0;
            PausableGameplayContainer.FadeIn(750, Easing.OutQuint);
        }

        public override void OnSuspending(IScreen next)
        {
            fadeOut();
            base.OnSuspending(next);
        }

        public bool CanPause => AllowPause && ValidForResume && !HasFailed && !RulesetContainer.HasReplayLoaded.Value
                                && (PausableGameplayContainer?.IsPaused.Value == false || PausableGameplayContainer?.IsResuming == true);

        public override bool OnExiting(IScreen next)
        {
            if (onCompletionEvent != null)
            {
                // Proceed to result screen if beatmap already finished playing
                onCompletionEvent.RunTask();
                return true;
            }

            if (LoadedBeatmapSuccessfully && CanPause)
            {
                PausableGameplayContainer?.Pause();
                return true;
            }

            GameplayClockContainer.ResetLocalAdjustments();

            fadeOut();
            return base.OnExiting(next);
        }

        private void fadeOut(bool instant = false)
        {
            float fadeOutDuration = instant ? 0 : 250;
            this.FadeOut(fadeOutDuration);

            Background.EnableUserDim.Value = false;
            storyboardReplacesBackground.Value = false;
        }

        protected override bool OnScroll(ScrollEvent e) => mouseWheelDisabled.Value && !PausableGameplayContainer.IsPaused.Value;

        protected virtual Results CreateResults(ScoreInfo score) => new SoloResults(score);
    }
}
