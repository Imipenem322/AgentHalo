using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaPen = System.Windows.Media.Pen;
using MediaPoint = System.Windows.Point;

namespace CodexHalo
{
public sealed class HaloVisual : FrameworkElement
    {
        private struct VisualSnapshot
        {
            public MediaColor Color;
            public double Powered;
            public double Breath;
            public double Intensity;
            public double BodyWidth;
            public double CoreWhite;
            public double GlowGain;
        }

        private readonly Stopwatch clock;
        private HaloState state;
        private HaloState previousState;
        private DateTime stateChangedUtc;
        private MediaColor transitionFromColor;
        private double transitionStartSeconds;
        private double transitionDuration;
        private VisualSnapshot transitionFromVisual;
        private VisualSnapshot renderedVisual;
        private bool hasRenderedFrame;
        private bool isRendering;
        private double testTime;
        private double testSinceState;
        private bool useTestTime;
        private long frameCount;
        private double lastAnimationSeconds;
        private TimeSpan lastRenderingTime;
        private double frameIntervalSumMs;
        private double frameIntervalMaxMs;
        private long frameIntervalCount;
        private long slowFrameCount;
        private long renderingCallbackCount;
        private long duplicateRenderingTimeCount;
        private int gen0AtReset;
        private int gen1AtReset;
        private int gen2AtReset;
        private double outerPhase;
        private double innerPhase;
        private double outerVelocity;
        private double gapSeparation;
        private bool gapRepelling;
        private double gapRepulsionElapsed;
        private double gapRepulsionStart;
        private double gapRepulsionDuration;
        private int gapRepulsionCount;
        private double smallGapAnchor;
        private double smallGapDriftElapsed;
        private double smallGapInertiaOffset;
        private double smallGapInertiaVelocity;
        private double energy;
        private bool steadyDone;
        private ErrorPresentation errorPresentation;

        public HaloVisual()
        {
            clock = Stopwatch.StartNew();
            state = HaloState.Idle;
            previousState = HaloState.Idle;
            stateChangedUtc = DateTime.UtcNow;
            transitionFromColor = StateColor(HaloState.Idle);
            transitionStartSeconds = -10;
            transitionDuration = 1;
            renderedVisual.Color = transitionFromColor;
            energy = TargetEnergy(HaloState.Idle);
            outerPhase = 97;
            gapSeparation = GeneratedHaloSpec.MaximumGapSeparation;
            innerPhase = outerPhase + gapSeparation;
            smallGapAnchor = innerPhase;
            SnapsToDevicePixels = false;
            Focusable = false;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public HaloState State
        {
            get { return state; }
        }

        public double MeasuredFps
        {
            get
            {
                return frameIntervalSumMs <= 0 ? 0 :
                    frameIntervalCount * 1000.0 / frameIntervalSumMs;
            }
        }

        public string PerformanceSummary
        {
            get
            {
                double averageMs = frameIntervalCount <= 0 ? 0 :
                    frameIntervalSumMs / frameIntervalCount;
                return String.Format(CultureInfo.InvariantCulture,
                    "{0:F1} FPS\nAverage frame: {1:F2} ms\nWorst frame: {2:F2} ms\n" +
                    "Frames over 25 ms: {3}\nWPF render tier: {4}\nGC collections: {5}/{6}/{7}\n" +
                    "Rendering callbacks: {8}\nDuplicate rendering times: {9}",
                    MeasuredFps, averageMs, frameIntervalMaxMs, slowFrameCount,
                    RenderCapability.Tier >> 16,
                    GC.CollectionCount(0) - gen0AtReset,
                    GC.CollectionCount(1) - gen1AtReset,
                    GC.CollectionCount(2) - gen2AtReset,
                    renderingCallbackCount, duplicateRenderingTimeCount);
            }
        }

        public void ResetPerformanceMetrics()
        {
            frameCount = 0;
            frameIntervalSumMs = 0;
            frameIntervalMaxMs = 0;
            frameIntervalCount = 0;
            slowFrameCount = 0;
            renderingCallbackCount = 0;
            duplicateRenderingTimeCount = 0;
            lastRenderingTime = TimeSpan.Zero;
            gen0AtReset = GC.CollectionCount(0);
            gen1AtReset = GC.CollectionCount(1);
            gen2AtReset = GC.CollectionCount(2);
        }

        public void SetState(HaloState value)
        {
            if (state != value)
            {
                double now = clock.Elapsed.TotalSeconds;
                CaptureTransitionStart();
                previousState = state;
                state = value;
                stateChangedUtc = DateTime.UtcNow;
                transitionStartSeconds = now;
                transitionDuration = TransitionDuration(state);
            }
            InvalidateVisual();
        }

        public void SetSteadyDone(bool value)
        {
            if (steadyDone != value)
            {
                double now = clock.Elapsed.TotalSeconds;
                CaptureTransitionStart();
                steadyDone = value;
                previousState = state;
                stateChangedUtc = DateTime.UtcNow;
                transitionStartSeconds = now;
                transitionDuration = value ? 1.45 : 1.15;
                InvalidateVisual();
            }
        }

        private void CaptureTransitionStart()
        {
            double localTime = Math.Max(0,
                (DateTime.UtcNow - stateChangedUtc).TotalSeconds);
            transitionFromVisual = hasRenderedFrame ? renderedVisual :
                TargetVisual(state, localTime);
            transitionFromColor = transitionFromVisual.Color;
        }

        public void SetErrorPresentation(ErrorPresentation value)
        {
            if (errorPresentation != value)
            {
                double now = clock.Elapsed.TotalSeconds;
                CaptureTransitionStart();
                errorPresentation = value;
                previousState = state;
                stateChangedUtc = DateTime.UtcNow;
                transitionStartSeconds = now;
                transitionDuration = value == ErrorPresentation.Flashing ? 0.82 : 1.24;
                InvalidateVisual();
            }
        }

        public void SetTestTime(double seconds)
        {
            useTestTime = true;
            testTime = seconds;
            testSinceState = seconds;
            previousState = state;
            transitionFromColor = StateColor(state);
            transitionFromVisual = TargetVisual(state, seconds);
            transitionStartSeconds = -10;
            stateChangedUtc = DateTime.UtcNow.AddSeconds(-seconds);
            InvalidateVisual();
        }

        public void SetTestTransition(HaloState from, HaloState to, double progress,
            double absoluteTime)
        {
            useTestTime = true;
            previousState = from;
            state = to;
            transitionFromColor = StateColor(from);
            transitionFromVisual = TargetVisual(from, absoluteTime);
            transitionDuration = TransitionDuration(to);
            transitionStartSeconds = absoluteTime - transitionDuration *
                Clamp(progress, 0, 1);
            testTime = absoluteTime;
            testSinceState = transitionDuration * Clamp(progress, 0, 1);
            stateChangedUtc = DateTime.UtcNow.AddSeconds(
                -transitionDuration * Clamp(progress, 0, 1));
            InvalidateVisual();
        }

        public void SetTestSteadyGreenTransition(double progress)
        {
            useTestTime = true;
            previousState = HaloState.Done;
            state = HaloState.Done;
            steadyDone = true;
            transitionFromColor = StateColor(HaloState.Done);
            transitionFromVisual = TargetVisual(HaloState.Done, 0);
            transitionFromVisual.Powered = 0.82;
            transitionFromVisual.Breath = 0.90;
            transitionDuration = 1.45;
            testTime = 4;
            transitionStartSeconds = testTime - transitionDuration *
                Clamp(progress, 0, 1);
            testSinceState = transitionDuration * Clamp(progress, 0, 1);
            stateChangedUtc = DateTime.UtcNow.AddSeconds(-testSinceState);
            InvalidateVisual();
        }

        public void SetTestSteadyGreenToThinking(double progress)
        {
            useTestTime = true;
            previousState = HaloState.Done;
            state = HaloState.Thinking;
            steadyDone = false;
            transitionFromColor = StateColor(HaloState.Done);
            transitionFromVisual = TargetVisual(HaloState.Done, 0);
            transitionFromVisual.Powered = 0;
            transitionFromVisual.Breath = 0.34;
            transitionDuration = TransitionDuration(HaloState.Thinking);
            testTime = 4;
            transitionStartSeconds = testTime - transitionDuration *
                Clamp(progress, 0, 1);
            testSinceState = transitionDuration * Clamp(progress, 0, 1);
            stateChangedUtc = DateTime.UtcNow.AddSeconds(-testSinceState);
            InvalidateVisual();
        }

        public void SetTestErrorPresentationTransition(ErrorPresentation from,
            ErrorPresentation to, double progress)
        {
            useTestTime = true;
            previousState = HaloState.Error;
            state = HaloState.Error;
            errorPresentation = from;
            transitionFromVisual = TargetVisual(HaloState.Error, 0.18);
            transitionFromColor = StateColor(HaloState.Error);
            errorPresentation = to;
            transitionDuration = to == ErrorPresentation.Flashing ? 0.82 : 1.24;
            testTime = 4;
            transitionStartSeconds = testTime - transitionDuration *
                Clamp(progress, 0, 1);
            testSinceState = transitionDuration * Clamp(progress, 0, 1);
            stateChangedUtc = DateTime.UtcNow.AddSeconds(-testSinceState);
            InvalidateVisual();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!isRendering)
            {
                CompositionTarget.Rendering += OnRendering;
                isRendering = true;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (isRendering)
            {
                CompositionTarget.Rendering -= OnRendering;
                isRendering = false;
            }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            renderingCallbackCount++;
            double now = clock.Elapsed.TotalSeconds;
            RenderingEventArgs rendering = e as RenderingEventArgs;
            double animationDelta;
            if (rendering != null && lastRenderingTime != TimeSpan.Zero)
            {
                if (rendering.RenderingTime == lastRenderingTime)
                {
                    duplicateRenderingTimeCount++;
                    return;
                }
                animationDelta = (rendering.RenderingTime - lastRenderingTime).TotalSeconds;
            }
            else
            {
                animationDelta = lastAnimationSeconds <= 0 ? 1.0 / 60.0 :
                    now - lastAnimationSeconds;
            }
            if (rendering != null)
            {
                lastRenderingTime = rendering.RenderingTime;
            }
            lastAnimationSeconds = now;
            animationDelta = Clamp(animationDelta, 0.001, 0.08);
            AdvanceAnimation(animationDelta, now);

            if (frameCount > 0)
            {
                double intervalMs = animationDelta * 1000;
                frameIntervalSumMs += intervalMs;
                frameIntervalCount++;
                frameIntervalMaxMs = Math.Max(frameIntervalMaxMs, intervalMs);
                if (intervalMs > 25)
                {
                    slowFrameCount++;
                }
            }
            frameCount++;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            double t = useTestTime ? testTime : clock.Elapsed.TotalSeconds;
            double sinceState = useTestTime ? testSinceState :
                Math.Max(0, (DateTime.UtcNow - stateChangedUtc).TotalSeconds);
            double transition = TransitionProgress(t);
            MediaPoint center = new MediaPoint(width / 2.0, height / 2.0);
            double scale = Math.Min(width, height) / 112.0;
            dc.PushTransform(new ScaleTransform(scale, scale, center.X, center.Y));

            HaloState visualState = state;
            MediaColor color = AnimatedColor(t);
            double displayEnergy = useTestTime
                ? Lerp(TargetEnergy(previousState), TargetEnergy(visualState), transition)
                : energy;
            double displayOuterPhase = outerPhase;
            double displayInnerPhase = innerPhase;
            if (useTestTime)
            {
                TestGapPhases(previousState, state, t, transition,
                    out displayOuterPhase, out displayInnerPhase);
            }

            DrawPureRing(dc, center, color, displayEnergy, displayOuterPhase,
                displayInnerPhase, sinceState, transition);
            hasRenderedFrame = true;
            dc.Pop();
        }

        private void DrawPureRing(DrawingContext dc, MediaPoint center, MediaColor color,
            double displayEnergy, double gapA, double gapB, double sinceState,
            double transition)
        {
            HaloState visualState = state;
            double localStateTime = useTestTime && transitionStartSeconds < 0
                ? sinceState : Math.Max(0, sinceState - transitionDuration);
            VisualSnapshot target = TargetVisual(visualState, localStateTime);
            target.Color = color;
            VisualSnapshot visual = TransitionVisual(transitionFromVisual, target,
                transition);
            double completionFlash = state == HaloState.Done
                && !steadyDone && transition >= 0.999
                    ? CompletionDoubleFlash(localStateTime) : 0;
            double intensity = Clamp(visual.Intensity + displayEnergy * 0.18 +
                completionFlash * 0.5, 0, 1.32);
            double radius = 35.8 + completionFlash * 0.45;
            double bodyWidth = visual.BodyWidth + completionFlash * 0.65;
            double powered = visual.Powered;
            powered = Clamp(powered + completionFlash * 0.82, 0, 1);
            renderedVisual = visual;
            renderedVisual.Color = color;
            renderedVisual.Powered = powered;
            MediaColor dimColor = AdjustSaturation(color, 0.88);
            MediaColor emissionColor = AdjustSaturation(color,
                0.92 + 0.36 * powered);
            MediaColor glowColor = MixColor(emissionColor,
                MediaColor.FromRgb(242, 248, 249), 0.18 + 0.08 * powered);
            double glowGain = visual.GlowGain;
            StreamGeometry[] ringGeometry = CreateDynamicRingGeometry(center,
                radius, gapA, gapB);
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(emissionColor,
                    Alpha((12 + 39 * powered) * intensity * glowGain)), 19.5));
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(emissionColor,
                    Alpha((22 + 52 * powered) * intensity * glowGain)), 14.5));
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(emissionColor,
                    Alpha((38 + 70 * powered) * intensity * glowGain)), 11.2));
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(glowColor,
                    Alpha(82 * powered * intensity * glowGain)), 9.8));

            MediaColor darkMaterial = MixColor(dimColor,
                MediaColor.FromRgb(18, 24, 26), 0.46);
            MediaColor litMaterial = MixColor(emissionColor,
                MediaColor.FromRgb(250, 253, 252), 0.56);
            MediaColor poweredMaterial = MixColor(darkMaterial, litMaterial,
                0.24 + 0.76 * powered);
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(darkMaterial, Alpha(242 * intensity)),
                    bodyWidth + 1.15));
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(poweredMaterial,
                    Alpha((182 + 73 * powered) * intensity)), bodyWidth));
            MediaColor poweredCore = MixColor(emissionColor,
                MediaColor.FromRgb(253, 255, 255), visual.CoreWhite);
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(poweredCore,
                    Alpha((5 + 235 * powered) * intensity)),
                    bodyWidth - 2.25));
            DrawDynamicRing(dc, ringGeometry,
                NewPen(WithAlpha(MediaColor.FromRgb(255, 255, 255),
                    Alpha(205 * powered * intensity)), 1.65));
        }

        private VisualSnapshot TargetVisual(HaloState value, double localTime)
        {
            VisualSnapshot result = new VisualSnapshot();
            result.Color = StateColor(value);
            result.Breath = StateBreath(value, localTime);
            result.Powered = TargetPowered(value, localTime);
            result.Intensity = 0.50 + result.Breath * 0.18;
            result.BodyWidth = 8.6;
            result.CoreWhite = CoreWhiteFor(value);
            result.GlowGain = GlowGainFor(value);
            if (value == HaloState.Done && steadyDone)
            {
                result.Powered = 0;
                result.Breath = 0.34;
                result.Intensity = 0.56;
            }
            else if (value == HaloState.Attention)
            {
                double pulse = AttentionPulse(localTime);
                result.Powered = 0.10 + 0.90 * pulse;
                result.Breath = 0.28 + 0.72 * pulse;
                result.Intensity = 0.56 + 0.18 * pulse;
                result.BodyWidth = 8.6 + 0.30 * pulse;
            }
            else if (value == HaloState.Error)
            {
                double pulse = ErrorPulse(localTime, errorPresentation);
                result.Powered = errorPresentation == ErrorPresentation.Bright
                    ? 1.0 : errorPresentation == ErrorPresentation.Dim ? 0 : pulse;
                result.Breath = errorPresentation == ErrorPresentation.Dim ? 0.10 : pulse;
                result.Intensity = errorPresentation == ErrorPresentation.Dim
                    ? 0.52 : 0.62 + 0.12 * pulse;
                result.BodyWidth = 8.6 + 0.25 * pulse;
            }
            return result;
        }

        private static VisualSnapshot TransitionVisual(VisualSnapshot from,
            VisualSnapshot to, double progress)
        {
            VisualSnapshot result = new VisualSnapshot();
            double scalarProgress = SmootherStep(Clamp((progress - 0.34) / 0.66, 0, 1));
            result.Color = to.Color;
            result.Powered = TransitionLight(from.Powered, to.Powered, progress);
            result.Breath = Lerp(from.Breath, to.Breath, scalarProgress);
            result.Intensity = Lerp(from.Intensity, to.Intensity, scalarProgress);
            result.BodyWidth = Lerp(from.BodyWidth, to.BodyWidth, scalarProgress);
            result.CoreWhite = Lerp(from.CoreWhite, to.CoreWhite, scalarProgress);
            result.GlowGain = Lerp(from.GlowGain, to.GlowGain, scalarProgress);
            return result;
        }

        private static double TargetPowered(HaloState value, double localTime)
        {
            SharedStateParameters parameters = GeneratedHaloSpec.State(value);
            if (parameters.PoweredMaximum > 0)
            {
                return LivingBreath(localTime, parameters.BreathPeriod,
                    parameters.PoweredMaximum, parameters.PoweredMinimum,
                    parameters.BrightShare);
            }
            return 0;
        }

        private static double CoreWhiteFor(HaloState value)
        {
            return GeneratedHaloSpec.State(value).CoreWhite;
        }

        private static double GlowGainFor(HaloState value)
        {
            return GeneratedHaloSpec.State(value).GlowGain;
        }

        private static StreamGeometry[] CreateDynamicRingGeometry(MediaPoint center,
            double radius, double gapA, double gapB)
        {
            // The visible clearances account for the thick rounded arc caps.
            // Both remain unmistakable at 112 px while retaining unequal sizes.
            const double gapASize = 30;
            const double gapBSize = 22;
            double aEnd = gapA + gapASize / 2;
            double bStart = gapB - gapBSize / 2;
            double bEnd = gapB + gapBSize / 2;
            double aStart = gapA - gapASize / 2;
            return new StreamGeometry[]
            {
                CreateArcGeometry(center, radius, aEnd,
                    PositiveModulo(bStart - aEnd, 360)),
                CreateArcGeometry(center, radius, bEnd,
                    PositiveModulo(aStart - bEnd, 360))
            };
        }

        private static StreamGeometry CreateArcGeometry(MediaPoint center,
            double radius, double startDegrees, double sweepDegrees)
        {
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                AddArcFigure(context, center, radius, startDegrees,
                    sweepDegrees);
            }
            geometry.Freeze();
            return geometry;
        }

        private static void AddArcFigure(StreamGeometryContext context,
            MediaPoint center, double radius, double startDegrees,
            double sweepDegrees)
        {
            if (sweepDegrees <= 0.001)
            {
                return;
            }
            MediaPoint start = PointOnCircle(center, radius, startDegrees);
            MediaPoint end = PointOnCircle(center, radius,
                startDegrees + sweepDegrees);
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new System.Windows.Size(radius, radius), 0,
                sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }

        private static void DrawDynamicRing(DrawingContext dc,
            StreamGeometry[] geometry, MediaPen pen)
        {
            for (int i = 0; i < geometry.Length; i++)
            {
                dc.DrawGeometry(null, pen, geometry[i]);
            }
        }

        private void AdvanceAnimation(double delta, double now)
        {
            double targetOrbitVelocity = TargetGapVelocityA(state) *
                GapVelocityEnvelopeA(state, now);
            outerVelocity = Damp(outerVelocity, targetOrbitVelocity, delta, 2.1);
            energy = Damp(energy, TargetEnergy(state), delta, 4.2);
            outerPhase += outerVelocity * delta;

            if (gapRepelling)
            {
                gapRepulsionElapsed += delta;
                double progress = Clamp(gapRepulsionElapsed /
                    Math.Max(0.01, gapRepulsionDuration), 0, 1);
                gapSeparation = Lerp(gapRepulsionStart,
                    GeneratedHaloSpec.MaximumGapSeparation,
                    MagneticRepulsionEase(progress));
                innerPhase = outerPhase + gapSeparation;
                if (progress >= 1)
                {
                    gapRepelling = false;
                    gapSeparation = GeneratedHaloSpec.MaximumGapSeparation;
                    innerPhase = outerPhase + gapSeparation;
                    smallGapAnchor = innerPhase;
                    smallGapDriftElapsed = 0;
                    smallGapInertiaOffset = 0;
                    smallGapInertiaVelocity =
                        RepulsionExitVelocityFromOrbit(outerVelocity);
                }
            }
            else
            {
                smallGapDriftElapsed += delta;
                smallGapInertiaVelocity *= Math.Exp(
                    -SmallGapInertiaDamping(state) * delta);
                smallGapInertiaOffset += smallGapInertiaVelocity * delta;
                innerPhase = smallGapAnchor +
                    smallGapInertiaOffset +
                    SmallGapDriftOffset(state, smallGapDriftElapsed,
                        gapRepulsionCount);
                gapSeparation = PositiveModulo(innerPhase - outerPhase, 360);
                if (gapSeparation <= 41.5 || gapSeparation > 300)
                {
                    gapSeparation = GeneratedHaloSpec.MinimumGapSeparation;
                    innerPhase = outerPhase + gapSeparation;
                    gapRepelling = true;
                    gapRepulsionElapsed = 0;
                    gapRepulsionStart = gapSeparation;
                    gapRepulsionDuration =
                        RepulsionDurationFromOrbit(outerVelocity);
                    gapRepulsionCount++;
                    smallGapInertiaOffset = 0;
                    smallGapInertiaVelocity = 0;
                }
            }

            if (outerPhase > 36000)
            {
                outerPhase -= 36000;
                innerPhase -= 36000;
            }
        }

        private static MediaPoint PointOnCircle(MediaPoint center, double radius, double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            return new MediaPoint(center.X + Math.Cos(radians) * radius,
                center.Y + Math.Sin(radians) * radius);
        }

        private static MediaPen NewPen(MediaColor color, double width)
        {
            MediaPen pen = new MediaPen(new SolidColorBrush(color), width);
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            return pen;
        }

        private MediaColor AnimatedColor(double time)
        {
            double progress = TransitionProgress(time);
            double colorProgress = SmootherStep(Clamp((progress - 0.18) / 0.56, 0, 1));
            return MixColor(transitionFromColor, StateColor(state), colorProgress);
        }

        private static double TransitionLight(double from, double to, double progress)
        {
            double low = GeneratedHaloSpec.TransitionLowPowered;
            if (progress < GeneratedHaloSpec.TransitionDimEnd)
            {
                return Lerp(from, Math.Min(from, low),
                    SmootherStep(progress / GeneratedHaloSpec.TransitionDimEnd));
            }
            if (progress < GeneratedHaloSpec.TransitionColorBlendEnd)
            {
                return Math.Min(from, low);
            }
            return Lerp(Math.Min(from, low), to,
                SmootherStep((progress - GeneratedHaloSpec.TransitionColorBlendEnd) /
                    (1 - GeneratedHaloSpec.TransitionColorBlendEnd)));
        }

        private double TransitionProgress(double time)
        {
            if (transitionDuration <= 0)
            {
                return 1;
            }
            return SmootherStep(Clamp((time - transitionStartSeconds) /
                transitionDuration, 0, 1));
        }

        private static double TransitionDuration(HaloState to)
        {
            return GeneratedHaloSpec.TransitionDuration(to);
        }

        private static double TargetGapVelocityA(HaloState value)
        {
            return GeneratedHaloSpec.State(value).OrbitVelocity;
        }

        private static double RepulsionDurationFromOrbit(double orbitVelocity)
        {
            double speed = Clamp(Math.Abs(orbitVelocity),
                GeneratedHaloSpec.RepulsionSpeedMinimum,
                GeneratedHaloSpec.RepulsionSpeedMaximum);
            return Clamp(GeneratedHaloSpec.RepulsionDurationFactor *
                Math.Sqrt(GeneratedHaloSpec.RepulsionReferenceSpeed / speed),
                GeneratedHaloSpec.RepulsionDurationMinimum,
                GeneratedHaloSpec.RepulsionDurationMaximum);
        }

        private static double SmallGapDriftOffset(HaloState value, double time,
            int cycle)
        {
            SharedStateParameters parameters = GeneratedHaloSpec.State(value);
            double amplitude = parameters.DriftAmplitude;
            double period = parameters.DriftPeriod;
            double direction = cycle % 2 == 0 ? 1 : -1;
            double primary = Math.Sin(time * Math.PI * 2 / period);
            double secondary = 0.22 * (Math.Sin(time * Math.PI * 2 /
                (period * 0.43) + 0.8) - Math.Sin(0.8));
            return direction * amplitude * (primary + secondary);
        }

        private static double RepulsionExitVelocityFromOrbit(double orbitVelocity)
        {
            return Clamp(Math.Abs(orbitVelocity) * GeneratedHaloSpec.ExitVelocityScale,
                GeneratedHaloSpec.ExitVelocityMinimum,
                GeneratedHaloSpec.ExitVelocityMaximum);
        }

        private static double SmallGapInertiaDamping(HaloState value)
        {
            return GeneratedHaloSpec.State(value).InertiaDamping;
        }

        private static double MagneticRepulsionEase(double value)
        {
            value = Clamp(value, 0, 1);
            double smoothPush = SmootherStep(value);
            double magneticBias = Math.Sin(value * Math.PI) * 0.055;
            return Clamp(smoothPush + magneticBias, 0, 1);
        }

        private static double GapVelocityEnvelopeA(HaloState value, double time)
        {
            double period = GeneratedHaloSpec.State(value).EnvelopePeriod;
            double primary = SoftWave(time / period);
            double secondary = SoftWave(time / (period * 0.47) + 0.29);
            return 0.18 + 0.92 * Math.Pow(primary, 1.65) + 0.22 * secondary;
        }

        private static void TestGapPhases(HaloState from, HaloState to,
            double time, double transition, out double gapA, out double gapB)
        {
            double velocity = Lerp(TargetGapVelocityA(from),
                TargetGapVelocityA(to), transition);
            double cycleDuration = Lerp(CatchCycleDuration(from),
                CatchCycleDuration(to), transition);
            double cycle = PositiveModulo(time, cycleDuration);
            double cycleStart = time - cycle;
            gapA = TestOrbitPhase(velocity, time);
            double cycleStartA = TestOrbitPhase(velocity, cycleStart);
            double representativeOrbitVelocity = velocity * 0.72;
            double repelDuration =
                RepulsionDurationFromOrbit(representativeOrbitVelocity);
            double repelStart = cycleDuration - repelDuration;
            if (cycle < repelStart)
            {
                HaloState dominant = transition >= 0.5 ? to : from;
                double inertiaVelocity =
                    RepulsionExitVelocityFromOrbit(representativeOrbitVelocity);
                double damping = Lerp(SmallGapInertiaDamping(from),
                    SmallGapInertiaDamping(to), transition);
                double inertiaOffset = inertiaVelocity / damping *
                    (1 - Math.Exp(-damping * cycle));
                double drift = SmallGapDriftOffset(dominant, cycle,
                    (int)Math.Floor(time / cycleDuration));
                gapB = cycleStartA + GeneratedHaloSpec.MaximumGapSeparation +
                    inertiaOffset + drift;
            }
            else
            {
                double repelProgress = (cycle - repelStart) / repelDuration;
                double separation = Lerp(GeneratedHaloSpec.MinimumGapSeparation,
                    GeneratedHaloSpec.MaximumGapSeparation,
                    MagneticRepulsionEase(repelProgress));
                gapB = gapA + separation;
            }
        }

        private static double TestOrbitPhase(double velocity, double time)
        {
            return 97 + velocity * time *
                (0.76 + 0.18 * Math.Sin(time * 0.62)) +
                5 * Math.Sin(time * 0.31);
        }

        private static double CatchCycleDuration(HaloState value)
        {
            switch (value)
            {
                case HaloState.Working: return 2.65;
                case HaloState.Thinking: return 3.8;
                case HaloState.Error: return 3.3;
                case HaloState.Attention: return 4.0;
                case HaloState.Done: return 5.4;
                default: return 5.8;
            }
        }

        public static double DiagnosticGapSeparation(double phase)
        {
            return Lerp(GeneratedHaloSpec.MinimumGapSeparation,
                GeneratedHaloSpec.MaximumGapSeparation,
                MagneticRepulsionEase(Clamp(phase, 0, 1)));
        }

        public static double DiagnosticRepulsionDuration(double orbitVelocity)
        {
            return RepulsionDurationFromOrbit(orbitVelocity);
        }

        public static double DiagnosticBreath(HaloState value, double time)
        {
            return StateBreath(value, time);
        }

        public static double DiagnosticPowered(HaloState value, double time)
        {
            return TargetPowered(value, time);
        }

        public static double DiagnosticAttentionPulse(double time)
        {
            return AttentionPulse(time);
        }

        public static double DiagnosticBrightDuration(HaloState value)
        {
            SharedStateParameters parameters = GeneratedHaloSpec.State(value);
            return parameters.PoweredMaximum > 0
                ? parameters.BreathPeriod * parameters.BrightShare : 0;
        }

        public static double DiagnosticCoreWhite(HaloState value)
        {
            return CoreWhiteFor(value);
        }

        public static double DiagnosticTransitionLight(double from, double to,
            double progress)
        {
            return TransitionLight(from, to, SmootherStep(progress));
        }

        private static double CompletionDoubleFlash(double sinceState)
        {
            double first = Math.Exp(-Math.Pow((sinceState - 0.28) / 0.14, 2));
            double second = Math.Exp(-Math.Pow((sinceState - 0.92) / 0.18, 2));
            return Clamp(first + second * 0.90, 0, 1);
        }

        private static double TargetEnergy(HaloState value)
        {
            switch (value)
            {
                case HaloState.Thinking: return 0.86;
                case HaloState.Working: return 1.0;
                case HaloState.Done: return 0.68;
                case HaloState.Attention: return 0.98;
                case HaloState.Error: return 1.0;
                default: return 0.34;
            }
        }

        private static double StateBreath(HaloState value, double time)
        {
            if (value == HaloState.Attention)
            {
                return 0.18 + 0.82 * AttentionPulse(time);
            }
            if (value == HaloState.Error)
            {
                return ErrorPulse(time, ErrorPresentation.Flashing);
            }
            SharedStateParameters parameters = GeneratedHaloSpec.State(value);
            if (value == HaloState.Idle)
            {
                return parameters.VisualMinimum +
                    (parameters.VisualMaximum - parameters.VisualMinimum) *
                    SoftWave(time / parameters.BreathPeriod);
            }
            return LivingBreath(time, parameters.BreathPeriod,
                parameters.VisualMaximum, parameters.VisualMinimum,
                parameters.BrightShare);
        }

        public static MediaColor StateColor(HaloState state)
        {
            SharedStateParameters parameters = GeneratedHaloSpec.State(state);
            return MediaColor.FromRgb(parameters.Red, parameters.Green, parameters.Blue);
        }

        private static MediaColor WithAlpha(MediaColor color, byte alpha)
        {
            return MediaColor.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static MediaColor MixColor(MediaColor from, MediaColor to, double amount)
        {
            amount = Clamp(amount, 0, 1);
            double r = LinearToSrgb(Lerp(SrgbToLinear(from.R / 255.0),
                SrgbToLinear(to.R / 255.0), amount));
            double g = LinearToSrgb(Lerp(SrgbToLinear(from.G / 255.0),
                SrgbToLinear(to.G / 255.0), amount));
            double b = LinearToSrgb(Lerp(SrgbToLinear(from.B / 255.0),
                SrgbToLinear(to.B / 255.0), amount));
            return MediaColor.FromRgb(Alpha(r * 255), Alpha(g * 255), Alpha(b * 255));
        }

        private static MediaColor AdjustSaturation(MediaColor color, double multiplier)
        {
            double hue;
            double saturation;
            double lightness;
            RgbToHsl(color, out hue, out saturation, out lightness);
            return HslToRgb(hue, Clamp(saturation * multiplier, 0, 1), lightness);
        }

        private static void RgbToHsl(MediaColor color, out double hue,
            out double saturation, out double lightness)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;
            double maximum = Math.Max(r, Math.Max(g, b));
            double minimum = Math.Min(r, Math.Min(g, b));
            double delta = maximum - minimum;
            lightness = (maximum + minimum) / 2;
            if (delta < 0.000001)
            {
                hue = 0;
                saturation = 0;
                return;
            }
            saturation = delta / (1 - Math.Abs(2 * lightness - 1));
            if (maximum == r)
            {
                hue = 60 * PositiveModulo((g - b) / delta, 6);
            }
            else if (maximum == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }
        }

        private static MediaColor HslToRgb(double hue, double saturation,
            double lightness)
        {
            double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            double segment = hue / 60;
            double x = chroma * (1 - Math.Abs(PositiveModulo(segment, 2) - 1));
            double r1 = 0;
            double g1 = 0;
            double b1 = 0;
            if (segment < 1)
            {
                r1 = chroma; g1 = x;
            }
            else if (segment < 2)
            {
                r1 = x; g1 = chroma;
            }
            else if (segment < 3)
            {
                g1 = chroma; b1 = x;
            }
            else if (segment < 4)
            {
                g1 = x; b1 = chroma;
            }
            else if (segment < 5)
            {
                r1 = x; b1 = chroma;
            }
            else
            {
                r1 = chroma; b1 = x;
            }
            double match = lightness - chroma / 2;
            return MediaColor.FromRgb(Alpha((r1 + match) * 255),
                Alpha((g1 + match) * 255), Alpha((b1 + match) * 255));
        }

        private static double SrgbToLinear(double value)
        {
            return value <= 0.04045 ? value / 12.92 :
                Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private static double LinearToSrgb(double value)
        {
            value = Clamp(value, 0, 1);
            return value <= 0.0031308 ? value * 12.92 :
                1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
        }

        private static double AttentionPulse(double time)
        {
            double cycle = PositiveModulo(time, GeneratedHaloSpec.AttentionPeriod) /
                GeneratedHaloSpec.AttentionPeriod;
            double first = SmoothPulse(cycle, GeneratedHaloSpec.AttentionFirstCenter,
                GeneratedHaloSpec.AttentionFirstWidth) *
                GeneratedHaloSpec.AttentionFirstStrength;
            double second = SmoothPulse(cycle, GeneratedHaloSpec.AttentionSecondCenter,
                GeneratedHaloSpec.AttentionSecondWidth) *
                GeneratedHaloSpec.AttentionSecondStrength;
            double livingBase = GeneratedHaloSpec.AttentionLivingBase +
                GeneratedHaloSpec.AttentionLivingAmplitude *
                SoftWave(cycle + GeneratedHaloSpec.AttentionLivingPhase);
            return Clamp(livingBase + first + second, 0, 1);
        }

        private static double ErrorPulse(double time, ErrorPresentation presentation)
        {
            if (presentation == ErrorPresentation.Bright)
                return GeneratedHaloSpec.ErrorBrightPower;
            if (presentation == ErrorPresentation.Dim)
                return GeneratedHaloSpec.ErrorDimPower;
            double cycle = PositiveModulo(time, GeneratedHaloSpec.ErrorFlashPeriod);
            double first = Math.Exp(-Math.Pow(
                (cycle - GeneratedHaloSpec.ErrorFirstCenter) /
                GeneratedHaloSpec.ErrorFirstWidth, 2));
            double second = Math.Exp(-Math.Pow(
                (cycle - GeneratedHaloSpec.ErrorSecondCenter) /
                GeneratedHaloSpec.ErrorSecondWidth, 2));
            return Clamp(first + second, 0, 1);
        }

        private static double LivingBreath(double time, double period,
            double maximum, double minimum, double brightShare)
        {
            double phase = PositiveModulo(time, period) / period;
            double center = brightShare + (1 - brightShare) * 0.46;
            double distance = Math.Abs(phase - center);
            distance = Math.Min(distance, 1 - distance);
            double width = Math.Max(0.075, (1 - brightShare) * 0.46);
            double dip = Math.Exp(-Math.Pow(distance / width, 4));
            double micro = 0.018 * Math.Sin(phase * Math.PI * 2) +
                0.009 * Math.Sin(phase * Math.PI * 4 + 0.8);
            return Clamp(maximum - (maximum - minimum) * dip + micro,
                minimum, maximum);
        }

        private static double SmoothPulse(double phase, double center, double width)
        {
            double distance = Math.Abs(phase - center);
            distance = Math.Min(distance, 1 - distance);
            double normalized = Clamp(1 - distance / width, 0, 1);
            return SmootherStep(normalized);
        }

        private static double SoftWave(double phase)
        {
            double cycle = PositiveModulo(phase, 1);
            double triangle = cycle < 0.5 ? cycle * 2 : (1 - cycle) * 2;
            return SmootherStep(triangle);
        }

        private static double PositiveModulo(double value, double modulus)
        {
            double result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static double Damp(double current, double target, double delta, double response)
        {
            return target + (current - target) * Math.Exp(-response * delta);
        }

        private static double SmootherStep(double value)
        {
            value = Clamp(value, 0, 1);
            return value * value * value * (value * (value * 6 - 15) + 10);
        }

        private static double Lerp(double from, double to, double amount)
        {
            return from + (to - from) * amount;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static byte Alpha(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
        }
    }
}

