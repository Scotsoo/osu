﻿// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System;
using OpenTK;
using osu.Game.Rulesets.Objects.Types;
using System.Collections.Generic;
using osu.Game.Rulesets.Objects;
using System.Linq;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Osu.Judgements;

namespace osu.Game.Rulesets.Osu.Objects
{
    public class Slider : OsuHitObject, IHasCurve
    {
        /// <summary>
        /// Scoring distance with a speed-adjusted beat length of 1 second.
        /// </summary>
        private const float base_scoring_distance = 100;

        public event Action<Vector2[]> ControlPointsChanged;

        public double EndTime => StartTime + this.SpanCount() * Path.Distance / Velocity;
        public double Duration => EndTime - StartTime;

        public Vector2 StackedPositionAt(double t) => StackedPosition + this.CurvePositionAt(t);
        public override Vector2 EndPosition => Position + this.CurvePositionAt(1);

        public override int ComboIndex
        {
            get => base.ComboIndex;
            set
            {
                base.ComboIndex = value;
                foreach (var n in NestedHitObjects.OfType<IHasComboInformation>())
                    n.ComboIndex = value;
            }
        }

        public override int IndexInCurrentCombo
        {
            get => base.IndexInCurrentCombo;
            set
            {
                base.IndexInCurrentCombo = value;
                foreach (var n in NestedHitObjects.OfType<IHasComboInformation>())
                    n.IndexInCurrentCombo = value;
            }
        }

        public SliderPath Path { get; } = new SliderPath();

        public Vector2[] ControlPoints
        {
            get => Path.ControlPoints;
            set
            {
                if (Path.ControlPoints == value)
                    return;
                Path.ControlPoints = value;

                ControlPointsChanged?.Invoke(value);

                if (TailCircle != null)
                    TailCircle.Position = EndPosition;
            }
        }

        public PathType PathType
        {
            get { return Path.PathType; }
            set { Path.PathType = value; }
        }

        public double Distance
        {
            get { return Path.Distance; }
            set { Path.Distance = value; }
        }

        public override Vector2 Position
        {
            get => base.Position;
            set
            {
                base.Position = value;

                if (HeadCircle != null)
                    HeadCircle.Position = value;

                if (TailCircle != null)
                    TailCircle.Position = EndPosition;
            }
        }

        public double? LegacyLastTickOffset { get; set; }

        /// <summary>
        /// The position of the cursor at the point of completion of this <see cref="Slider"/> if it was hit
        /// with as few movements as possible. This is set and used by difficulty calculation.
        /// </summary>
        internal Vector2? LazyEndPosition;

        /// <summary>
        /// The distance travelled by the cursor upon completion of this <see cref="Slider"/> if it was hit
        /// with as few movements as possible. This is set and used by difficulty calculation.
        /// </summary>
        internal float LazyTravelDistance;

        public List<List<SampleInfo>> NodeSamples { get; set; } = new List<List<SampleInfo>>();

        public int RepeatCount { get; set; }

        /// <summary>
        /// The length of one span of this <see cref="Slider"/>.
        /// </summary>
        public double SpanDuration => Duration / this.SpanCount();

        /// <summary>
        /// Velocity of this <see cref="Slider"/>.
        /// </summary>
        public double Velocity { get; private set; }

        /// <summary>
        /// Spacing between <see cref="SliderTick"/>s of this <see cref="Slider"/>.
        /// </summary>
        public double TickDistance { get; private set; }

        /// <summary>
        /// An extra multiplier that affects the number of <see cref="SliderTick"/>s generated by this <see cref="Slider"/>.
        /// An increase in this value increases <see cref="TickDistance"/>, which reduces the number of ticks generated.
        /// </summary>
        public double TickDistanceMultiplier = 1;

        public HitCircle HeadCircle;
        public SliderTailCircle TailCircle;

        protected override void ApplyDefaultsToSelf(ControlPointInfo controlPointInfo, BeatmapDifficulty difficulty)
        {
            base.ApplyDefaultsToSelf(controlPointInfo, difficulty);

            TimingControlPoint timingPoint = controlPointInfo.TimingPointAt(StartTime);
            DifficultyControlPoint difficultyPoint = controlPointInfo.DifficultyPointAt(StartTime);

            double scoringDistance = base_scoring_distance * difficulty.SliderMultiplier * difficultyPoint.SpeedMultiplier;

            Velocity = scoringDistance / timingPoint.BeatLength;
            TickDistance = scoringDistance / difficulty.SliderTickRate * TickDistanceMultiplier;
        }

        protected override void CreateNestedHitObjects()
        {
            base.CreateNestedHitObjects();

            createSliderEnds();
            createTicks();
            createRepeatPoints();

            if (LegacyLastTickOffset != null)
                TailCircle.StartTime = Math.Max(StartTime + Duration / 2, TailCircle.StartTime - LegacyLastTickOffset.Value);
        }

        private void createSliderEnds()
        {
            HeadCircle = new SliderCircle
            {
                StartTime = StartTime,
                Position = Position,
                Samples = getNodeSamples(0),
                SampleControlPoint = SampleControlPoint,
                IndexInCurrentCombo = IndexInCurrentCombo,
                ComboIndex = ComboIndex,
            };

            TailCircle = new SliderTailCircle
            {
                StartTime = EndTime,
                Position = EndPosition,
                IndexInCurrentCombo = IndexInCurrentCombo,
                ComboIndex = ComboIndex,
            };

            AddNested(HeadCircle);
            AddNested(TailCircle);
        }

        private void createTicks()
        {
            var length = Path.Distance;
            var tickDistance = MathHelper.Clamp(TickDistance, 0, length);

            if (tickDistance == 0) return;

            var minDistanceFromEnd = Velocity * 0.01;

            var spanCount = this.SpanCount();

            for (var span = 0; span < spanCount; span++)
            {
                var spanStartTime = StartTime + span * SpanDuration;
                var reversed = span % 2 == 1;

                for (var d = tickDistance; d <= length; d += tickDistance)
                {
                    if (d > length - minDistanceFromEnd)
                        break;

                    var distanceProgress = d / length;
                    var timeProgress = reversed ? 1 - distanceProgress : distanceProgress;

                    var firstSample = Samples.FirstOrDefault(s => s.Name == SampleInfo.HIT_NORMAL)
                                      ?? Samples.FirstOrDefault(); // TODO: remove this when guaranteed sort is present for samples (https://github.com/ppy/osu/issues/1933)
                    var sampleList = new List<SampleInfo>();

                    if (firstSample != null)
                        sampleList.Add(new SampleInfo
                        {
                            Bank = firstSample.Bank,
                            Volume = firstSample.Volume,
                            Name = @"slidertick",
                        });

                    AddNested(new SliderTick
                    {
                        SpanIndex = span,
                        SpanStartTime = spanStartTime,
                        StartTime = spanStartTime + timeProgress * SpanDuration,
                        Position = Position + Path.PositionAt(distanceProgress),
                        StackHeight = StackHeight,
                        Scale = Scale,
                        Samples = sampleList
                    });
                }
            }
        }

        private void createRepeatPoints()
        {
            for (int repeatIndex = 0, repeat = 1; repeatIndex < RepeatCount; repeatIndex++, repeat++)
            {
                AddNested(new RepeatPoint
                {
                    RepeatIndex = repeatIndex,
                    SpanDuration = SpanDuration,
                    StartTime = StartTime + repeat * SpanDuration,
                    Position = Position + Path.PositionAt(repeat % 2),
                    StackHeight = StackHeight,
                    Scale = Scale,
                    Samples = getNodeSamples(1 + repeatIndex)
                });
            }
        }

        private List<SampleInfo> getNodeSamples(int nodeIndex)
        {
            if (nodeIndex < NodeSamples.Count)
                return NodeSamples[nodeIndex];
            return Samples;
        }

        public override Judgement CreateJudgement() => new OsuJudgement();
    }
}
