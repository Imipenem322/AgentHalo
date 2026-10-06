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
public sealed class HaloWindow : Window
    {
        private const double HaloSize = 112;
        private const int ExtendedWindowStyleIndex = -20;
        private const int ExtendedStyleTopmost = 0x00000008;
        private const int ExtendedStyleToolWindow = 0x00000080;
        private const int ExtendedStyleNoActivate = 0x08000000;
        private const uint SetWindowPosNoSize = 0x0001;
        private const uint SetWindowPosNoMove = 0x0002;
        private const uint SetWindowPosNoActivate = 0x0010;
        private const uint SetWindowPosNoOwnerZOrder = 0x0200;
        private static readonly IntPtr TopmostWindowOrder = new IntPtr(-1);
        private static readonly int[] HaloScalePresets = { 75, 100, 125 };
        private static readonly TimeSpan RuntimeStateRefreshInterval =
            TimeSpan.FromSeconds(1);
        private readonly HaloSettings settings;
        private readonly AgentProviderCatalog providerCatalog;
        private readonly AgentMonitorCoordinator coordinator;
        private readonly DeepSeekHarnessSetup deepSeekSetup;
        private readonly HaloVisual visual;
        private readonly DetailsWindow details;
        private readonly Forms.NotifyIcon tray;
        private readonly DispatcherTimer foregroundTimer;
        private readonly DispatcherTimer hoverHideTimer;
        private readonly DispatcherTimer performanceTimer;
        private AggregateSnapshot aggregate;
        private AggregateSnapshot displayAggregate;
        private AgentProviderSnapshot providerSnapshot;
        private MediaPoint dragStart;
        private MediaPoint windowStart;
        private bool dragging;
        private bool moved;
        private bool closing;
        private HaloState? demoState;
        private ErrorPresentation? demoErrorPresentation;
        private bool codexWasForeground;
        private IntPtr windowHandle;
        private IntPtr topmostGuardForeground;
        private DateTime nextTopmostGuardUtc = DateTime.MinValue;
        private DateTime activeErrorUtc;
        private DateTime errorDimmedUtc;
        private DateTime nextRuntimeStateRefreshUtc = DateTime.MinValue;
        private DateTime nextDeepSeekSetupUtc = DateTime.MinValue;
        private string deepSeekSetupError;
        private ErrorPresentation errorPresentation = ErrorPresentation.Flashing;

        public HaloWindow(HaloSettings appSettings)
            : this(appSettings, null)
        {
        }

        internal HaloWindow(HaloSettings appSettings,
            DeepSeekHarnessSetup automaticDeepSeekSetup)
        {
            settings = appSettings;
            deepSeekSetup = automaticDeepSeekSetup;
            ConfigureLocalization(settings);
            providerCatalog = new AgentProviderCatalog();
            if (settings.NormalizeAgentSelection(providerCatalog))
            {
                SettingsStorage.Save(settings);
            }
            double initialSize = SizeForScale(settings.HaloScalePercent);
            Width = initialSize;
            Height = initialSize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = settings.AlwaysOnTop;
            Title = "Agent Halo";

            visual = new HaloVisual();
            Grid hitSurface = new Grid();
            hitSurface.Background = System.Windows.Media.Brushes.Transparent;
            Border centerHitSurface = new Border();
            centerHitSurface.Width = 64;
            centerHitSurface.Height = 64;
            centerHitSurface.CornerRadius = new CornerRadius(32);
            centerHitSurface.HorizontalAlignment = HorizontalAlignment.Center;
            centerHitSurface.VerticalAlignment = VerticalAlignment.Center;
            centerHitSurface.Background = new SolidColorBrush(
                MediaColor.FromArgb(1, 255, 255, 255));
            hitSurface.Children.Add(centerHitSurface);
            hitSurface.Children.Add(visual);
            Content = hitSurface;
            AgentKind initialFocus = providerCatalog.ParseOrDefault(
                settings.FocusedAgent);
            details = new DetailsWindow();
            details.SetEnabledAgentDescriptors(EnabledAgentDescriptors());
            details.SetAgentSwitchingEnabled(!settings.Paused);
            details.AgentFocusRequested += RequestFocusedAgent;
            coordinator = new AgentMonitorCoordinator(providerCatalog,
                initialFocus);
            coordinator.Changed += OnCoordinatorChanged;
            foregroundTimer = new DispatcherTimer(DispatcherPriority.Background);
            foregroundTimer.Interval = TimeSpan.FromMilliseconds(300);
            foregroundTimer.Tick += OnForegroundTick;
            hoverHideTimer = new DispatcherTimer();
            hoverHideTimer.Interval = TimeSpan.FromMilliseconds(220);
            hoverHideTimer.Tick += delegate
            {
                hoverHideTimer.Stop();
                if (!IsMouseOver && !details.IsMouseOver)
                {
                    details.Hide();
                }
            };
            string performanceLogPath = Environment.GetEnvironmentVariable(
                "AGENTHALO_PERF_LOG");
            if (!String.IsNullOrWhiteSpace(performanceLogPath))
            {
                performanceTimer = new DispatcherTimer(DispatcherPriority.Background);
                performanceTimer.Interval = TimeSpan.FromSeconds(5);
                performanceTimer.Tick += delegate
                {
                    File.WriteAllText(performanceLogPath,
                        DateTime.Now.ToString("o", CultureInfo.InvariantCulture) +
                        Environment.NewLine + visual.PerformanceSummary,
                        Encoding.UTF8);
                };
            }
            MouseEnter += delegate
            {
                hoverHideTimer.Stop();
                ShowHoverDetails();
            };
            MouseLeave += delegate
            {
                hoverHideTimer.Stop();
                hoverHideTimer.Start();
            };
            details.MouseEnter += delegate { hoverHideTimer.Stop(); };
            details.MouseLeave += delegate
            {
                hoverHideTimer.Stop();
                hoverHideTimer.Start();
            };

            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            MouseRightButtonUp += delegate
            {
                if (tray.ContextMenuStrip != null)
                {
                    tray.ContextMenuStrip.Show(Forms.Control.MousePosition);
                }
            };
            MouseDoubleClick += OnDoubleClick;
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closing += OnClosing;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            tray = new Forms.NotifyIcon();
            tray.Text = "Agent Halo";
            tray.Icon = CreateTrayIcon(DrawingColor.FromArgb(43, 200, 255));
            tray.Visible = true;
            tray.DoubleClick += delegate
            {
                Dispatcher.BeginInvoke(new Action(ToggleDetails));
            };
            BuildTrayMenu();

            L10n.Instance.LanguageChanged += (s, ev) =>
            {
                Dispatcher.Invoke(new Action(BuildTrayMenu));
            };
        }

        private void OnCoordinatorChanged(object sender,
            AgentCoordinatorChangedEventArgs e)
        {
            if (closing || e == null)
            {
                return;
            }
            Action apply = delegate
            {
                if (closing || !coordinator.IsCurrentGeneration(e.Kind,
                        e.Generation))
                {
                    return;
                }
                try
                {
                    if (e.FocusChanged)
                    {
                        ResetFocusedAgentPresentation();
                        BuildTrayMenu();
                    }
                    RefreshState();
                }
                catch (Exception ex)
                {
                    SettingsStorage.Log("Agent UI refresh failed: " +
                        ex.Message);
                }
            };
            if (Dispatcher.CheckAccess())
            {
                apply();
            }
            else if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(apply);
            }
        }

        private void ResetFocusedAgentPresentation()
        {
            aggregate = null;
            displayAggregate = null;
            providerSnapshot = null;
            activeErrorUtc = DateTime.MinValue;
            errorDimmedUtc = DateTime.MinValue;
            errorPresentation = ErrorPresentation.Flashing;
            codexWasForeground = coordinator.IsForeground(
                GetForegroundWindow());
        }

        public static bool IsValidScalePercent(int value)
        {
            return HaloScalePresets.Contains(value);
        }

        private static double SizeForScale(int percent)
        {
            return HaloSize * percent / 100.0;
        }

        public static double DiagnosticSizeForScale(int percent)
        {
            return SizeForScale(IsValidScalePercent(percent) ? percent : 100);
        }

        public static bool DiagnosticIsFrameVisible(
            System.Drawing.Rectangle frame,
            IEnumerable<System.Drawing.Rectangle> workingAreas)
        {
            return workingAreas.Any(delegate(System.Drawing.Rectangle area)
            {
                return area.IntersectsWith(frame);
            });
        }

        internal static DateTime NextRuntimeStateRefreshUtc(DateTime now)
        {
            return now.Add(RuntimeStateRefreshInterval);
        }

        internal static bool IsRuntimeStateRefreshDue(DateTime now,
            DateTime nextRefreshUtc)
        {
            return now >= nextRefreshUtc;
        }

        internal static bool ShouldRefreshRuntimeState(bool foregroundChanged,
            bool errorIsDimmed, DateTime now, DateTime nextRefreshUtc)
        {
            return foregroundChanged || errorIsDimmed ||
                IsRuntimeStateRefreshDue(now, nextRefreshUtc);
        }

        internal static bool ShouldShowGreenStandby(AggregateSnapshot snapshot,
            bool previewActive)
        {
            return !previewActive && snapshot != null &&
                snapshot.Presence == AgentPresenceState.Standby &&
                snapshot.TurnPhase == AgentTurnPhase.None &&
                String.Equals(snapshot.Label, "STANDBY",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RestorePosition();
            RecoverHaloIfOffscreen();
            coordinator.Start();
            CheckDeepSeekSetup(DateTime.UtcNow);
            RefreshState();
            IntPtr foregroundHandle = GetForegroundWindow();
            codexWasForeground = coordinator.IsForeground(foregroundHandle);
            CheckAndRestoreTopmost(foregroundHandle, true);
            foregroundTimer.Start();
            if (performanceTimer != null)
            {
                visual.ResetPerformanceMetrics();
                performanceTimer.Start();
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    RecoverHaloIfOffscreen();
                    CheckAndRestoreTopmost(GetForegroundWindow(), true);
                }));
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock &&
                !Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    CheckAndRestoreTopmost(GetForegroundWindow(), true);
                }));
            }
        }

        private void CheckDeepSeekSetup(DateTime now)
        {
            if (deepSeekSetup == null || settings.Paused ||
                deepSeekSetup.Ready && settings.DeepSeekHarnessSetupComplete ||
                now < nextDeepSeekSetupUtc)
            {
                return;
            }
            nextDeepSeekSetupUtc = now.AddSeconds(5);
            if (deepSeekSetup.TryInstall())
            {
                string previousFocus = settings.FocusedAgent;
                bool previouslyEnabled = settings.IsAgentEnabled(
                    AgentKind.DeepSeekHarness);
                if (DeepSeekHarnessSetup.EnableMonitoring(settings))
                {
                    AgentKind target = providerCatalog.ParseOrDefault(
                        settings.FocusedAgent);
                    // Let the coordinator commit focus and persist it together.
                    settings.FocusedAgent = previousFocus;
                    if (target == coordinator.FocusedKind)
                        SettingsStorage.Save(settings);
                    else
                        RequestFocusedAgent(target);
                    if (coordinator.FocusedKind != target)
                    {
                        settings.DeepSeekHarnessSetupComplete = false;
                        if (!previouslyEnabled)
                            settings.EnabledAgents.Remove("deepseek-harness");
                    }
                    details.SetEnabledAgentDescriptors(EnabledAgentDescriptors());
                    BuildTrayMenu();
                }
                return;
            }
            if (!String.IsNullOrEmpty(deepSeekSetup.Error) &&
                deepSeekSetupError != deepSeekSetup.Error)
            {
                deepSeekSetupError = deepSeekSetup.Error;
                tray.ShowBalloonTip(8000, "Agent Halo",
                    L10n.Instance.Format("deepseek.setup.error",
                        deepSeekSetupError), Forms.ToolTipIcon.Warning);
            }
        }

        private void OnForegroundTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            CheckDeepSeekSetup(now);
            IntPtr foregroundHandle = GetForegroundWindow();
            CheckAndRestoreTopmost(foregroundHandle, false);
            if (settings.Paused)
            {
                codexWasForeground = false;
                return;
            }
            bool focusedAgentIsForeground = coordinator.IsForeground(
                foregroundHandle);
            if (focusedAgentIsForeground && !codexWasForeground &&
                !demoState.HasValue &&
                aggregate != null && aggregate.State == HaloState.Done)
            {
                AcknowledgeCompleted();
            }
            if (aggregate != null && aggregate.State == HaloState.Error)
            {
                if (focusedAgentIsForeground)
                {
                    errorPresentation = ErrorPresentation.Bright;
                }
                else if (codexWasForeground)
                {
                    errorPresentation = ErrorPresentation.Dim;
                    errorDimmedUtc = DateTime.UtcNow;
                }
            }
            // Session files do not change when Codex exits, and a completed
            // session expires without a new lifecycle event. Re-evaluate the
            // aggregate periodically so both transitions reach the halo.
            if (ShouldRefreshRuntimeState(
                    focusedAgentIsForeground != codexWasForeground,
                    errorPresentation == ErrorPresentation.Dim,
                    now,
                    nextRuntimeStateRefreshUtc))
            {
                RefreshState();
            }
            codexWasForeground = focusedAgentIsForeground;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            windowHandle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(windowHandle, ExtendedWindowStyleIndex);
            SetWindowLong(windowHandle, ExtendedWindowStyleIndex,
                style | ExtendedStyleNoActivate | ExtendedStyleToolWindow);
        }

        private void CheckAndRestoreTopmost(IntPtr foregroundHandle, bool force)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (!force && !TopmostGuardPolicy.IsDue(
                    nowUtc, nextTopmostGuardUtc))
            {
                return;
            }

            nextTopmostGuardUtc = TopmostGuardPolicy.NextCheckUtc(nowUtc);
            bool foregroundChanged =
                foregroundHandle != topmostGuardForeground;
            topmostGuardForeground = foregroundHandle;
            if (!settings.AlwaysOnTop)
            {
                return;
            }

            bool nativeTopmost = windowHandle != IntPtr.Zero &&
                (GetWindowLong(windowHandle, ExtendedWindowStyleIndex) &
                    ExtendedStyleTopmost) != 0;
            bool d3dFullScreenActive = IsD3DFullScreenActive();

            if (!TopmostGuardPolicy.ShouldRestore(
                    true,
                    d3dFullScreenActive,
                    nativeTopmost,
                    foregroundChanged,
                    force))
            {
                return;
            }

            RestoreNativeTopmost(windowHandle);
            if (details.IsVisible)
            {
                details.Topmost = true;
                RestoreNativeTopmost(new WindowInteropHelper(details).Handle);
            }
        }

        private static void RestoreNativeTopmost(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }
            SetWindowPos(handle, TopmostWindowOrder, 0, 0, 0, 0,
                SetWindowPosNoSize | SetWindowPosNoMove |
                SetWindowPosNoActivate | SetWindowPosNoOwnerZOrder);
        }

        private static bool IsD3DFullScreenActive()
        {
            UserNotificationState state;
            return SHQueryUserNotificationState(out state) == 0 &&
                state == UserNotificationState.RunningD3DFullScreen;
        }

        private void RestorePosition()
        {
            Rect area = GetWorkAreaDip();
            if (settings.HasPosition)
            {
                Left = Math.Max(area.Left - Width + 24, Math.Min(settings.Left, area.Right - 24));
                Top = Math.Max(area.Top - Height + 24, Math.Min(settings.Top, area.Bottom - 24));
            }
            else
            {
                Left = area.Right - Width - 28;
                Top = area.Top + 110;
            }
        }

        private void RefreshState()
        {
            if (closing)
            {
                return;
            }
            DateTime nowUtc = DateTime.UtcNow;
            providerSnapshot = coordinator.Read(settings, nowUtc);
            aggregate = providerSnapshot.Aggregate;
            bool codexRunning = aggregate.Presence != AgentPresenceState.Offline;
            nextRuntimeStateRefreshUtc = NextRuntimeStateRefreshUtc(nowUtc);
            if (aggregate.State == HaloState.Error)
            {
                DateTime previousErrorUtc = activeErrorUtc;
                SessionSnapshot latestError = aggregate.Sessions
                    .Where(delegate(SessionSnapshot session)
                    {
                        return session.State == HaloState.Error;
                    })
                    .OrderByDescending(delegate(SessionSnapshot session)
                    {
                        return session.LastEventUtc;
                    }).FirstOrDefault();
                activeErrorUtc = latestError == null ? DateTime.UtcNow : latestError.LastEventUtc;
                if (activeErrorUtc <= settings.GetAcknowledgedErrorUtc(
                        aggregate.FocusedAgent))
                {
                    if (aggregate.FocusedAgent == AgentKind.DeepSeekHarness)
                    {
                        errorPresentation = ErrorPresentation.Flashing;
                        errorDimmedUtc = DateTime.MinValue;
                        providerSnapshot = coordinator.Read(settings, nowUtc);
                        aggregate = providerSnapshot.Aggregate;
                        codexRunning = aggregate.Presence !=
                            AgentPresenceState.Offline;
                        nextRuntimeStateRefreshUtc =
                            NextRuntimeStateRefreshUtc(nowUtc);
                    }
                    else
                    {
                        SetCodexIdlePresentation(aggregate, codexRunning);
                    }
                }
                else if (activeErrorUtc > previousErrorUtc)
                {
                    errorPresentation = coordinator.IsForeground(
                        GetForegroundWindow())
                        ? ErrorPresentation.Bright : ErrorPresentation.Flashing;
                }
                else if (coordinator.IsForeground(GetForegroundWindow()))
                {
                    errorPresentation = ErrorPresentation.Bright;
                }
                else if (errorPresentation != ErrorPresentation.Dim)
                {
                    errorPresentation = ErrorPresentation.Flashing;
                }
            }
            if (errorPresentation == ErrorPresentation.Dim &&
                DateTime.UtcNow - errorDimmedUtc >= TimeSpan.FromMinutes(1))
            {
                settings.AcknowledgeError(aggregate.FocusedAgent,
                    activeErrorUtc);
                SettingsStorage.Save(settings);
                errorPresentation = ErrorPresentation.Flashing;
                if (aggregate.FocusedAgent == AgentKind.DeepSeekHarness)
                {
                    errorDimmedUtc = DateTime.MinValue;
                    DateTime rereadUtc = DateTime.UtcNow;
                    providerSnapshot = coordinator.Read(settings, rereadUtc);
                    aggregate = providerSnapshot.Aggregate;
                    codexRunning = aggregate.Presence !=
                        AgentPresenceState.Offline;
                    nextRuntimeStateRefreshUtc =
                        NextRuntimeStateRefreshUtc(rereadUtc);
                }
                else
                {
                    SetCodexIdlePresentation(aggregate, codexRunning);
                }
            }
            if (demoState.HasValue)
            {
                aggregate.State = demoState.Value;
                aggregate.Label = CodexSessionMonitor.StateLabel(demoState.Value);
                aggregate.Detail = "Preview mode";
                aggregate.Presence = AgentPresenceState.Active;
            }
            bool showGreenStandby = ShouldShowGreenStandby(aggregate,
                demoState.HasValue);
            visual.SetSteadyDone(showGreenStandby);
            visual.SetErrorPresentation(demoErrorPresentation ?? errorPresentation);
            visual.SetState(aggregate.State);
            displayAggregate = aggregate;
            providerSnapshot.Aggregate = aggregate;
            tray.Text = ("Agent Halo · " + aggregate.Label).Substring(0,
                Math.Min(63, ("Agent Halo · " + aggregate.Label).Length));
            details.SetEnabledAgentDescriptors(EnabledAgentDescriptors());
            details.SetAgentSwitchingEnabled(!settings.Paused);
            if (!settings.Paused && deepSeekSetup != null &&
                deepSeekSetup.Ready && aggregate.FocusedAgent ==
                    AgentKind.DeepSeekHarness &&
                providerSnapshot.Integration.State ==
                    AgentIntegrationState.NotConfigured)
            {
                providerSnapshot.Details.StatusDetailKey =
                    nowUtc - deepSeekSetup.PreparedUtc < TimeSpan.FromSeconds(15)
                        ? "status.deepseek.connecting"
                        : "status.deepseek.start_or_restart";
            }
            details.UpdateContent(providerSnapshot);
        }

        private static void SetCodexIdlePresentation(AggregateSnapshot snapshot,
            bool codexRunning)
        {
            snapshot.TurnPhase = AgentTurnPhase.None;
            snapshot.Activity = AgentActivityKind.None;
            snapshot.EvidenceSource = AgentEvidenceSource.Process;
            snapshot.EvidenceKind = codexRunning
                ? "process_running" : "process_stopped";
            snapshot.AttentionReason = AgentAttentionReason.None;
            snapshot.FailureSeverity = AgentFailureSeverity.None;
            if (codexRunning)
            {
                snapshot.State = HaloState.Done;
                snapshot.Label = "STANDBY";
                snapshot.Detail = L10n.Instance["status.standby_codex"];
                snapshot.Presence = AgentPresenceState.Standby;
            }
            else
            {
                snapshot.State = HaloState.Idle;
                snapshot.Label = CodexSessionMonitor.StateLabel(HaloState.Idle);
                snapshot.Detail = L10n.Instance["status.offline_codex"];
                snapshot.Presence = AgentPresenceState.Offline;
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }
            dragStart = GetCursorDip();
            windowStart = new MediaPoint(Left, Top);
            dragging = true;
            moved = false;
            CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            MediaPoint current = GetCursorDip();
            double dx = current.X - dragStart.X;
            double dy = current.Y - dragStart.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 4)
            {
                moved = true;
            }
            Left = windowStart.X + dx;
            Top = windowStart.Y + dy;
            if (details.IsVisible)
            {
                PositionDetails();
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragging)
            {
                return;
            }
            dragging = false;
            ReleaseMouseCapture();
            if (moved)
            {
                SnapToEdges();
                SavePosition();
            }
        }

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            coordinator.TryActivateWindow();
            e.Handled = true;
        }

        private void ToggleDetails()
        {
            if (details.IsVisible)
            {
                details.Hide();
            }
            else
            {
                ShowOrRefreshDetails();
            }
        }

        private void ShowHoverDetails()
        {
            ShowOrRefreshDetails();
        }

        private void ShowOrRefreshDetails()
        {
            AggregateSnapshot detailsAggregate = displayAggregate ?? aggregate;
            if (detailsAggregate == null)
            {
                return;
            }
            if (details.Topmost != Topmost)
            {
                details.Topmost = Topmost;
            }
            providerSnapshot.Aggregate = detailsAggregate;
            details.UpdateContent(providerSnapshot);
            PositionDetails();
            if (!details.IsVisible)
            {
                details.Show();
                details.UpdateLayout();
            }
            PositionDetails();
            QueueDetailsReposition();
        }

        private void QueueDetailsReposition()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(PositionDetails));
            Dispatcher.BeginInvoke(DispatcherPriority.Render,
                new Action(PositionDetails));
        }

        private void PositionDetails()
        {
            Rect area = GetWorkAreaDip();
            double gap = 10;
            double proposedLeft = Left - details.Width - gap;
            if (proposedLeft < area.Left + 8)
            {
                proposedLeft = Left + Width + gap;
            }
            double detailHeight = GetDetailsHeightForPosition();
            details.Left = Math.Max(area.Left + 8,
                Math.Min(proposedLeft, area.Right - details.Width - 8));
            details.Top = Math.Max(area.Top + 8,
                Math.Min(Top + Height / 2 - detailHeight / 2,
                    area.Bottom - Math.Max(detailHeight, 230) - 8));
        }

        private double GetDetailsHeightForPosition()
        {
            if (details.ActualHeight > 0)
            {
                return details.ActualHeight;
            }
            if (details.DesiredSize.Height > 0)
            {
                return details.DesiredSize.Height;
            }
            if (!Double.IsNaN(details.Height) && details.Height > 0)
            {
                return details.Height;
            }
            return 230;
        }

        private void SnapToEdges()
        {
            Rect area = GetWorkAreaDip();
            const double threshold = 26;
            if (Math.Abs(Left - area.Left) < threshold)
            {
                Left = area.Left + 8;
            }
            if (Math.Abs((Left + Width) - area.Right) < threshold)
            {
                Left = area.Right - Width - 8;
            }
            if (Math.Abs(Top - area.Top) < threshold)
            {
                Top = area.Top + 8;
            }
            if (Math.Abs((Top + Height) - area.Bottom) < threshold)
            {
                Top = area.Bottom - Height - 8;
            }
        }

        private MediaPoint GetCursorDip()
        {
            System.Drawing.Point cursor = Forms.Control.MousePosition;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                System.Windows.Media.Matrix fromDevice =
                    source.CompositionTarget.TransformFromDevice;
                return fromDevice.Transform(new MediaPoint(cursor.X, cursor.Y));
            }
            return new MediaPoint(cursor.X, cursor.Y);
        }

        private Rect GetWorkAreaDip()
        {
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null)
            {
                return SystemParameters.WorkArea;
            }

            System.Windows.Media.Matrix toDevice =
                source.CompositionTarget.TransformToDevice;
            System.Windows.Media.Matrix fromDevice =
                source.CompositionTarget.TransformFromDevice;
            MediaPoint centerDip = new MediaPoint(
                Double.IsNaN(Left) ? SystemParameters.WorkArea.Right - 60 : Left + Width / 2,
                Double.IsNaN(Top) ? SystemParameters.WorkArea.Top + 160 : Top + Height / 2);
            MediaPoint centerDevice = toDevice.Transform(centerDip);
            Forms.Screen screen = Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)Math.Round(centerDevice.X), (int)Math.Round(centerDevice.Y)));
            System.Drawing.Rectangle work = screen.WorkingArea;
            MediaPoint topLeft = fromDevice.Transform(new MediaPoint(work.Left, work.Top));
            MediaPoint bottomRight = fromDevice.Transform(new MediaPoint(work.Right, work.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        private Rect GetPrimaryWorkAreaDip()
        {
            Forms.Screen primary = Forms.Screen.PrimaryScreen;
            if (primary == null)
            {
                return SystemParameters.WorkArea;
            }
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null)
            {
                return SystemParameters.WorkArea;
            }
            System.Windows.Media.Matrix fromDevice =
                source.CompositionTarget.TransformFromDevice;
            System.Drawing.Rectangle work = primary.WorkingArea;
            MediaPoint topLeft = fromDevice.Transform(
                new MediaPoint(work.Left, work.Top));
            MediaPoint bottomRight = fromDevice.Transform(
                new MediaPoint(work.Right, work.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        private void EscapeOffscreen()
        {
            MoveHaloToPrimaryScreen();
            Topmost = settings.AlwaysOnTop;
            Activate();
            CheckAndRestoreTopmost(GetForegroundWindow(), true);
        }

        private void MoveHaloToPrimaryScreen()
        {
            Rect area = GetPrimaryWorkAreaDip();
            Left = area.Right - Width - 28;
            Top = area.Top + 28;
            SavePosition();
        }

        private void RecoverHaloIfOffscreen()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            NativeRect nativeFrame;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out nativeFrame))
            {
                return;
            }
            System.Drawing.Rectangle frame = System.Drawing.Rectangle.FromLTRB(
                nativeFrame.Left, nativeFrame.Top, nativeFrame.Right, nativeFrame.Bottom);
            IEnumerable<System.Drawing.Rectangle> areas = Forms.Screen.AllScreens
                .Select(delegate(Forms.Screen screen) { return screen.WorkingArea; });
            if (!DiagnosticIsFrameVisible(frame, areas))
            {
                MoveHaloToPrimaryScreen();
            }
        }

        private void SavePosition()
        {
            settings.HasPosition = true;
            settings.Left = Left;
            settings.Top = Top;
            SettingsStorage.Save(settings);
        }

        private void ApplyHaloScale(int percent)
        {
            if (!IsValidScalePercent(percent))
            {
                percent = 100;
            }
            double centerX = Double.IsNaN(Left) ? 0 : Left + Width / 2;
            double centerY = Double.IsNaN(Top) ? 0 : Top + Height / 2;
            double size = SizeForScale(percent);
            Width = size;
            Height = size;
            if (!Double.IsNaN(Left) && !Double.IsNaN(Top))
            {
                Left = centerX - size / 2;
                Top = centerY - size / 2;
                Rect area = GetWorkAreaDip();
                Left = Math.Max(area.Left + 8,
                    Math.Min(Left, area.Right - Width - 8));
                Top = Math.Max(area.Top + 8,
                    Math.Min(Top, area.Bottom - Height - 8));
            }
            settings.HaloScalePercent = percent;
            SavePosition();
            PositionDetails();
        }

        private void AcknowledgeCompleted()
        {
            if (aggregate == null || aggregate.Sessions == null)
            {
                return;
            }
            foreach (SessionSnapshot session in aggregate.Sessions)
            {
                if (session.State == HaloState.Done)
                {
                    settings.Acknowledge(session.Agent, session.ThreadId,
                        session.CompletedUtc);
                }
            }
            SettingsStorage.Save(settings);
            RefreshState();
        }

        private IEnumerable<AgentProviderDescriptor> EnabledAgentDescriptors()
        {
            return providerCatalog.Descriptors.Where(
                delegate(AgentProviderDescriptor descriptor)
                {
                    return settings.IsAgentEnabled(descriptor.Kind,
                        providerCatalog);
                }).ToList();
        }

        private void RequestFocusedAgent(AgentKind targetKind)
        {
            if (closing || settings.Paused ||
                targetKind == coordinator.FocusedKind)
            {
                return;
            }
            try
            {
                AgentProviderSnapshot firstSnapshot;
                bool switched = coordinator.TrySwitch(targetKind, settings,
                    delegate
                    {
                        SettingsStorage.SaveAtomicOrThrow(settings);
                        return true;
                    }, out firstSnapshot);
                if (!switched)
                {
                    SettingsStorage.Log("Agent switch was rejected: " +
                        coordinator.LastSwitchError);
                }
            }
            catch (Exception ex)
            {
                SettingsStorage.Log("Agent switch failed: " + ex.Message);
            }
        }

        private void BuildTrayMenu()
        {
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            Forms.ToolStripMenuItem topmost = new Forms.ToolStripMenuItem(L10n.Instance["menu.always_on_top"]);
            topmost.Checked = settings.AlwaysOnTop;
            topmost.CheckOnClick = true;
            topmost.CheckedChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    settings.AlwaysOnTop = topmost.Checked;
                    Topmost = settings.AlwaysOnTop;
                    details.Topmost = Topmost;
                    CheckAndRestoreTopmost(GetForegroundWindow(), true);
                    SettingsStorage.Save(settings);
                }));
            };
            menu.Items.Add(topmost);

            Forms.ToolStripMenuItem startup = new Forms.ToolStripMenuItem(L10n.Instance["menu.launch_at_startup"]);
            startup.Checked = StartupManager.IsEnabled();
            startup.CheckOnClick = true;
            startup.CheckedChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    StartupManager.SetEnabled(startup.Checked);
                }));
            };
            menu.Items.Add(startup);

            Forms.ToolStripMenuItem pause = new Forms.ToolStripMenuItem(L10n.Instance["menu.pause_monitor"]);
            pause.Checked = settings.Paused;
            pause.CheckOnClick = true;
            pause.CheckedChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    settings.Paused = pause.Checked;
                    try
                    {
                        if (settings.Paused)
                        {
                            coordinator.Stop();
                        }
                        else
                        {
                            coordinator.Start();
                        }
                    }
                    catch (Exception ex)
                    {
                        settings.Paused = true;
                        SettingsStorage.Log("Agent monitoring toggle failed: " +
                            ex.Message);
                    }
                    details.SetAgentSwitchingEnabled(!settings.Paused);
                    RefreshState();
                    BuildTrayMenu();
                }));
            };
            menu.Items.Add(pause);

            Forms.ToolStripMenuItem currentAgent =
                new Forms.ToolStripMenuItem(
                    L10n.Instance["menu.current_agent"]);
            foreach (AgentProviderDescriptor descriptor in
                providerCatalog.Descriptors)
            {
                if (!settings.IsAgentEnabled(descriptor.Kind,
                        providerCatalog))
                {
                    continue;
                }
                AgentKind selectedKind = descriptor.Kind;
                string agentTitle = String.IsNullOrWhiteSpace(
                    descriptor.DisplayNameKey)
                    ? descriptor.DisplayName
                    : L10n.Instance[descriptor.DisplayNameKey];
                Forms.ToolStripMenuItem agentItem =
                    new Forms.ToolStripMenuItem(agentTitle);
                agentItem.AccessibleRole = Forms.AccessibleRole.RadioButton;
                agentItem.Checked = coordinator.FocusedKind == selectedKind;
                agentItem.Enabled = !settings.Paused;
                agentItem.Click += delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        RequestFocusedAgent(selectedKind);
                    }));
                };
                currentAgent.DropDownItems.Add(agentItem);
            }
            menu.Items.Add(currentAgent);

            // Language submenu
            var languageItem = new Forms.ToolStripMenuItem(L10n.Instance["menu.language"]);
            languageItem.DropDownItems.Add(CreateLanguageItem(null));  // Follow System
            languageItem.DropDownItems.Add(CreateLanguageItem("zh"));   // 中文
            languageItem.DropDownItems.Add(CreateLanguageItem("en"));   // English
            menu.Items.Add(languageItem);

            menu.Items.Add(L10n.Instance["menu.escape_offscreen"], null, delegate
            {
                Dispatcher.BeginInvoke(new Action(EscapeOffscreen));
            });

            Forms.ToolStripMenuItem sizeMenu =
                new Forms.ToolStripMenuItem(L10n.Instance["halo.size"]);
            foreach (int percent in HaloScalePresets)
            {
                int selectedPercent = percent;
                Forms.ToolStripMenuItem sizeItem = new Forms.ToolStripMenuItem(
                    percent.ToString(CultureInfo.InvariantCulture) + "%");
                sizeItem.Checked = settings.HaloScalePercent == percent;
                sizeItem.Click += delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ApplyHaloScale(selectedPercent);
                        foreach (Forms.ToolStripItem child in sizeMenu.DropDownItems)
                        {
                            Forms.ToolStripMenuItem candidate =
                                child as Forms.ToolStripMenuItem;
                            if (candidate != null)
                            {
                                candidate.Checked = candidate == sizeItem;
                            }
                        }
                    }));
                };
                sizeMenu.DropDownItems.Add(sizeItem);
            }
            menu.Items.Add(sizeMenu);

            Forms.ToolStripMenuItem preview = new Forms.ToolStripMenuItem(L10n.Instance["menu.preview_status"]);
            AddPreviewItem(preview, L10n.Instance["halo.live_status"], null);
            AddPreviewItem(preview, L10n.Instance["halo.thinking_preview"], HaloState.Thinking);
            AddPreviewItem(preview, L10n.Instance["halo.working_preview"], HaloState.Working);
            AddPreviewItem(preview, L10n.Instance["halo.done_preview"], HaloState.Done);
            AddPreviewItem(preview, L10n.Instance["halo.attention_preview"], HaloState.Attention);
            AddPreviewItem(preview, L10n.Instance["halo.error_flash_preview"], HaloState.Error,
                ErrorPresentation.Flashing);
            AddPreviewItem(preview, L10n.Instance["halo.error_bright_preview"], HaloState.Error,
                ErrorPresentation.Bright);
            AddPreviewItem(preview, L10n.Instance["halo.error_dim_preview"], HaloState.Error,
                ErrorPresentation.Dim);
            AddPreviewItem(preview, L10n.Instance["halo.idle_preview"], HaloState.Idle);
            menu.Items.Add(preview);

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(L10n.Instance["menu.quit"], null, delegate
            {
                Dispatcher.BeginInvoke(new Action(Close));
            });
            Win11MenuRenderer.Apply(menu);
            Forms.ContextMenuStrip previousMenu = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            if (previousMenu != null)
            {
                previousMenu.Dispose();
            }
        }

        private void AddPreviewItem(Forms.ToolStripMenuItem parent, string title,
            HaloState? preview)
        {
            AddPreviewItem(parent, title, preview, null);
        }

        private void AddPreviewItem(Forms.ToolStripMenuItem parent, string title,
            HaloState? preview, ErrorPresentation? presentation)
        {
            Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(title);
            item.Click += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    demoState = preview;
                    demoErrorPresentation = presentation;
                    RefreshState();
                }));
            };
            parent.DropDownItems.Add(item);
        }

        private Forms.ToolStripMenuItem CreateLanguageItem(string lang)
        {
            string title = lang != null
                ? L10n.Instance["menu.language." + lang]
                : L10n.Instance["menu.language.auto"];
            var item = new Forms.ToolStripMenuItem(title, null, OnLanguageSelected);
            item.Tag = lang;
            item.Checked = IsLanguageMenuItemChecked(lang, settings.Language);
            return item;
        }

        internal static void ConfigureLocalization(HaloSettings appSettings)
        {
            L10n.Instance.SetLanguage(appSettings == null ? null : appSettings.Language);
        }

        internal static bool IsLanguageMenuItemChecked(string itemLanguage,
            string savedLanguage)
        {
            return String.Equals(itemLanguage, savedLanguage,
                StringComparison.Ordinal);
        }

        private void OnLanguageSelected(object sender, EventArgs e)
        {
            var item = sender as Forms.ToolStripMenuItem;
            string lang = (item != null ? item.Tag : null) as string;
            settings.Language = lang;
            SettingsStorage.Save(settings);
            L10n.Instance.SetLanguage(lang);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            closing = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            foregroundTimer.Stop();
            hoverHideTimer.Stop();
            if (performanceTimer != null)
            {
                performanceTimer.Stop();
            }
            SavePosition();
            details.AgentFocusRequested -= RequestFocusedAgent;
            coordinator.Changed -= OnCoordinatorChanged;
            details.Close();
            coordinator.Dispose();
            tray.Visible = false;
            tray.Dispose();
        }

        private static System.Drawing.Icon CreateTrayIcon(DrawingColor color)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (System.Drawing.Pen glow = new System.Drawing.Pen(
                DrawingColor.FromArgb(80, color), 7))
            using (System.Drawing.Pen ring = new System.Drawing.Pen(color, 3))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(DrawingColor.Transparent);
                graphics.DrawArc(glow, 5, 5, 22, 22, -52, 140);
                graphics.DrawArc(glow, 5, 5, 22, 22, 106, 194);
                graphics.DrawArc(ring, 5, 5, 22, 22, -52, 140);
                graphics.DrawArc(ring, 5, 5, 22, 22, 106, 194);
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        private enum UserNotificationState
        {
            NotPresent = 1,
            Busy = 2,
            RunningD3DFullScreen = 3,
            PresentationMode = 4,
            AcceptsNotifications = 5,
            QuietTime = 6,
            App = 7
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(
            out UserNotificationState state);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
