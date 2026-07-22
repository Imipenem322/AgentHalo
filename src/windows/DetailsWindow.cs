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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
public sealed class RoundedMeter : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register("Value", typeof(double), typeof(RoundedMeter),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value
        {
            get { return (double)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        protected override System.Windows.Size MeasureOverride(
            System.Windows.Size availableSize)
        {
            return new System.Windows.Size(
                Double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, 4);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double width = Math.Max(0, ActualWidth);
            double height = Math.Max(0, ActualHeight);
            double radius = height / 2;
            drawingContext.DrawRoundedRectangle(
                new SolidColorBrush(MediaColor.FromArgb(92, 184, 202, 211)), null,
                new Rect(0, 0, width, height), radius, radius);
            double fill = width * Math.Max(0, Math.Min(100, Value)) / 100.0;
            if (fill > 0)
            {
                drawingContext.DrawRoundedRectangle(
                    new SolidColorBrush(MediaColor.FromRgb(64, 105, 132)), null,
                    new Rect(0, 0, Math.Max(height, fill), height), radius, radius);
            }
        }
    }

public sealed class ContextBatteryMeter : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register("Value", typeof(double),
                typeof(ContextBatteryMeter),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsAvailableProperty =
            DependencyProperty.Register("IsAvailable", typeof(bool),
                typeof(ContextBatteryMeter),
                new FrameworkPropertyMetadata(false,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value
        {
            get { return (double)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public bool IsAvailable
        {
            get { return (bool)GetValue(IsAvailableProperty); }
            set { SetValue(IsAvailableProperty, value); }
        }

        protected override System.Windows.Size MeasureOverride(
            System.Windows.Size availableSize)
        {
            return new System.Windows.Size(
                Double.IsInfinity(availableSize.Width) ? 46 : availableSize.Width,
                Double.IsInfinity(availableSize.Height) ? 24 : availableSize.Height);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = Math.Max(1, ActualWidth);
            double height = Math.Max(1, ActualHeight);
            double radius = Math.Min(8, height * 0.34);
            Rect rect = new Rect(0.5, 0.5, width - 1, height - 1);
            double value = Math.Max(0, Math.Min(100, Value));
            MediaColor fillColor = MediaColor.FromRgb(224, 240, 252);

            dc.DrawRoundedRectangle(new SolidColorBrush(fillColor),
                new MediaPen(new SolidColorBrush(
                    MediaColor.FromArgb(72, 174, 205, 224)), 1),
                rect, radius, radius);

            string text = IsAvailable
                ? Math.Min(99, (int)Math.Round(value)).ToString(
                    CultureInfo.InvariantCulture) + "%"
                : "--";
            FormattedText formatted = new FormattedText(text,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new System.Windows.Media.FontFamily(
                    "Segoe UI Variable Display, Segoe UI"),
                    FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                15.2, new SolidColorBrush(MediaColor.FromRgb(64, 105, 132)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            formatted.TextAlignment = TextAlignment.Center;
            dc.DrawText(formatted, new MediaPoint(width / 2,
                (height - formatted.Height) / 2 - 0.5));
        }
    }

public sealed class DetailsWindow : Window
    {
        public const double DesignWidth = 320;

        private readonly TextBlock headline;
        private readonly TextBlock subtitle;
        private readonly Border shell;
        private readonly TextBlock quotaLabel;
        private readonly TextBlock quotaValue;
        private readonly TextBlock quotaReset;
        private readonly Grid quotaRow;
        private readonly ContextBatteryMeter contextMeter;
        private readonly StackPanel quotaGroup;
        private readonly StackPanel infoGroup;
        private readonly Grid dataLayer;
        private readonly Grid infoProjectRow;
        private readonly Grid infoModelRow;
        private readonly Grid infoTokenRow;
        private readonly Border infoProjectSeparator;
        private readonly Border infoModelSeparator;
        private readonly TextBlock infoProjectTitle;
        private readonly TextBlock infoModelTitle;
        private readonly TextBlock infoTokenTitle;
        private readonly TextBlock infoProjectValue;
        private readonly TextBlock infoModelValue;
        private readonly TextBlock infoTokenValue;
        private readonly RoundedMeter quotaBar;
        private readonly DispatcherTimer quotaTimer;
        private string quotaLabelKey;
        private UsageMetrics previewMetrics;
        private CodexCustomApiMetrics previewCodexCustomMetrics;
        private AggregateSnapshot currentAggregate;
        private List<SessionSnapshot> currentSessions;

        public DetailsWindow()
        {
            Width = DesignWidth;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            SourceInitialized += OnSourceInitialized;

            shell = new Border();
            shell.CornerRadius = new CornerRadius(16);
            shell.Padding = new Thickness(17, 13, 17, 10);
            shell.Background = CreateFallbackGlassBrush();
            shell.BorderBrush = new SolidColorBrush(
                MediaColor.FromArgb(46, 183, 199, 207));
            shell.BorderThickness = new Thickness(1);
            shell.Margin = new Thickness(9);
            shell.Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Direction = 270,
                Opacity = 0.12,
                Color = MediaColor.FromRgb(151, 174, 184)
            };

            StackPanel content = new StackPanel();
            Grid statusRow = new Grid();
            statusRow.Height = 32;
            statusRow.ColumnDefinitions.Add(new ColumnDefinition());
            statusRow.ColumnDefinitions.Add(new ColumnDefinition
                { Width = GridLength.Auto });
            headline = NewText("OFFLINE", 20,
                MediaColor.FromRgb(40, 52, 60), FontWeights.Bold);
            headline.VerticalAlignment = VerticalAlignment.Center;
            statusRow.Children.Add(headline);
            contextMeter = new ContextBatteryMeter
            {
                Width = 46,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(contextMeter, 1);
            statusRow.Children.Add(contextMeter);
            content.Children.Add(statusRow);

            subtitle = NewText(L10n.Instance["status.offline_codex"], 13,
                MediaColor.FromRgb(103, 117, 126), FontWeights.Normal, true);
            subtitle.Margin = new Thickness(0, 1, 0, 13);
            content.Children.Add(subtitle);

            quotaLabelKey = "quota.current_available";
            quotaGroup = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            quotaRow = CreateQuotaRow(L10n.Instance[quotaLabelKey], out quotaLabel,
                out quotaReset, out quotaValue, out quotaBar);
            quotaGroup.Children.Add(quotaRow);

            // These generic rows are used by Codex custom API/provider mode.
            infoGroup = new StackPanel
            {
                Visibility = Visibility.Collapsed
            };
            infoProjectRow = CreateInfoRow(L10n.Instance["metadata.project"], out infoProjectTitle, out infoProjectValue);
            infoGroup.Children.Add(infoProjectRow);
            infoProjectSeparator = CreateInfoSeparator();
            infoGroup.Children.Add(infoProjectSeparator);
            infoModelRow = CreateInfoRow(L10n.Instance["metadata.model"], out infoModelTitle, out infoModelValue);
            infoGroup.Children.Add(infoModelRow);
            infoModelSeparator = CreateInfoSeparator();
            infoGroup.Children.Add(infoModelSeparator);
            infoTokenRow = CreateInfoRow(L10n.Instance["metadata.tokens"], out infoTokenTitle, out infoTokenValue);
            infoTokenValue.FontSize = 11.5;
            infoTokenValue.FontWeight = FontWeights.SemiBold;
            infoGroup.Children.Add(infoTokenRow);

            dataLayer = new Grid();
            dataLayer.VerticalAlignment = VerticalAlignment.Top;
            dataLayer.Children.Add(quotaGroup);
            dataLayer.Children.Add(infoGroup);
            content.Children.Add(dataLayer);

            Grid layers = new Grid();
            layers.Children.Add(content);
            shell.Child = layers;
            Content = shell;
            Closed += delegate
            {
                quotaTimer.Stop();
                CodexUsageMonitor.Instance.Updated -= OnCodexUsageUpdated;
            };
            quotaTimer = new DispatcherTimer();
            quotaTimer.Interval = TimeSpan.FromSeconds(3);
            quotaTimer.Tick += delegate
            {
                if (IsVisible)
                {
                    RefreshSupplementalData();
                }
            };
            quotaTimer.Start();
            CodexUsageMonitor.Instance.Updated += OnCodexUsageUpdated;

            L10n.Instance.LanguageChanged += (s, ev) =>
            {
                Dispatcher.Invoke(() => RefreshAllText());
            };
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, style | 0x08000000 | 0x00000080);
        }

        public void UpdateContent(AggregateSnapshot aggregate, List<SessionSnapshot> sessions)
        {
            currentAggregate = aggregate;
            currentSessions = sessions ?? new List<SessionSnapshot>();
            headline.Text = aggregate.Label;
            MediaColor accent = HaloVisual.StateColor(aggregate.State);
            headline.Foreground = new SolidColorBrush(accent);
            subtitle.Text = FriendlyStatusDetail(aggregate, sessions);
            RefreshSupplementalData();
        }

        private static string FriendlyStatusDetail(AggregateSnapshot aggregate,
            List<SessionSnapshot> sessions)
        {
            if (aggregate.State == HaloState.Idle)
            {
                if (String.Equals(aggregate.Label, "PAUSED",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return L10n.Instance["status.paused"];
                }
                return L10n.Instance["status.offline_codex"];
            }
            if (String.Equals(aggregate.Label, "STANDBY",
                StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(aggregate.Detail))
            {
                return aggregate.Detail;
            }
            SessionSnapshot active = sessions.FirstOrDefault(delegate(SessionSnapshot session)
            {
                return session.Active;
            });
            string action = active == null ? String.Empty : active.Action;
            if (action.IndexOf("Writing answer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                action.IndexOf("Generating response", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.writing_answer"];
            }
            if (action.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.running_command"];
            }
            if (action.IndexOf("Editing", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.editing_files"];
            }
            if (action.IndexOf("Search", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.searching"];
            }
            if (action.IndexOf("Compressing context", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.compressing_context"];
            }
            if (action.IndexOf("Context compacted", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.context_compacted"];
            }
            if (action.IndexOf("Awaiting permission", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.awaiting_permission"];
            }
            if (action.IndexOf("Reviewing result", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L10n.Instance["status.reviewing_result"];
            }
            switch (aggregate.State)
            {
                case HaloState.Thinking: return L10n.Instance["status.thinking"];
                case HaloState.Working: return L10n.Instance["status.working"];
                case HaloState.Done: return L10n.Instance["status.done"];
                case HaloState.Attention: return L10n.Instance["status.attention"];
                case HaloState.Error:
                    return String.IsNullOrEmpty(aggregate.Detail)
                        ? L10n.Instance["status.error"] : aggregate.Detail;
                default:
                    return String.IsNullOrEmpty(aggregate.Detail)
                        ? L10n.Instance["status.unknown"] : aggregate.Detail;
            }
        }

        private void RefreshSupplementalData()
        {
            if (IsOfflineAggregate(currentAggregate))
            {
                ApplyOfflinePlaceholders();
                return;
            }
            contextMeter.Visibility = Visibility.Visible;
            RefreshCodexDetails();
        }

        private static bool IsOfflineAggregate(AggregateSnapshot aggregate)
        {
            // Mirrors the macOS check: an idle ring labeled OFFLINE means we
            // have no live session, so any project/model/token/context values
            // would be stale carry-over from the previous run.
            return aggregate != null
                && aggregate.State == HaloState.Idle
                && String.Equals(aggregate.Label, "OFFLINE",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyOfflinePlaceholders()
        {
            CodexCustomApiMetrics codexMetrics = ReadCodexCustomMetrics();
            if (codexMetrics != null && codexMetrics.IsCustomApi)
            {
                quotaGroup.Visibility = Visibility.Collapsed;
                infoGroup.Visibility = Visibility.Visible;
                infoProjectRow.Visibility = Visibility.Visible;
                infoProjectSeparator.Visibility = Visibility.Visible;
                infoModelSeparator.Visibility = Visibility.Visible;
                infoModelRow.Margin = new Thickness(0);
                infoTokenRow.Margin = new Thickness(0);
                infoProjectValue.Text = "--";
                infoModelValue.Text = "--";
                infoTokenValue.Text = "--";
            }
            else
            {
                RefreshQuota();
            }
            // Drop the context pill rather than echoing a percentage from the
            // session that just went offline. Done after RefreshQuota since
            // that path resets context-pill visibility.
            contextMeter.Visibility = Visibility.Collapsed;
        }

        private void RefreshCodexDetails()
        {
            CodexCustomApiMetrics metrics = ReadCodexCustomMetrics();
            if (metrics != null && metrics.IsCustomApi)
            {
                ApplyCodexCustomMetrics(metrics);
                return;
            }
            RefreshQuota();
        }

        private CodexCustomApiMetrics ReadCodexCustomMetrics()
        {
            if (previewCodexCustomMetrics != null)
            {
                return previewCodexCustomMetrics;
            }
            if (previewMetrics != null)
            {
                return new CodexCustomApiMetrics { IsCustomApi = false };
            }
            return CodexCustomApiMetricsReader.Read(currentSessions);
        }

        private void ApplyCodexCustomMetrics(CodexCustomApiMetrics metrics)
        {
            quotaGroup.Visibility = Visibility.Collapsed;
            infoGroup.Visibility = Visibility.Visible;
            infoProjectRow.Visibility = Visibility.Visible;
            infoProjectSeparator.Visibility = Visibility.Visible;
            infoModelSeparator.Visibility = Visibility.Visible;
            infoModelRow.Margin = new Thickness(0);
            infoTokenRow.Margin = new Thickness(0);
            infoProjectValue.Text = metrics.HasProject
                ? metrics.ProjectName : L10n.Instance["quota.no_data"];
            infoModelValue.Text = metrics.HasModel
                ? metrics.Model : L10n.Instance["quota.no_data"];
            infoTokenValue.Text = metrics.HasTokenUsage
                ? "↑ " + FormatCompactNumber(metrics.InputTokens) +
                  "  ·  ↓ " + FormatCompactNumber(metrics.OutputTokens)
                : L10n.Instance["quota.no_data"];
            SetContextPercent(metrics.HasContext, metrics.ContextUsedPercent);
        }

        private void RefreshQuota()
        {
            quotaGroup.Visibility = Visibility.Visible;
            infoGroup.Visibility = Visibility.Collapsed;
            UsageMetrics metrics;
            if (previewMetrics != null)
            {
                metrics = previewMetrics;
            }
            else if (!CodexUsageMonitor.Instance.TryRead(out metrics))
            {
                metrics = null;
            }
            if (metrics != null)
            {
                ApplyQuotaMetrics(metrics);
                SetContextPercent(metrics.HasContext, metrics.ContextUsedPercent);
            }
            else
            {
                ApplyQuotaMetrics(new UsageMetrics { ContextInputTokens = -1 });
                SetContextPercent(false, 0);
            }
        }

        private void OnCodexUsageUpdated()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (IsVisible && previewMetrics == null)
                {
                    RefreshCodexDetails();
                }
            }));
        }

        private void SetContextPercent(bool available, double value)
        {
            contextMeter.IsAvailable = available;
            contextMeter.Value = available
                ? Math.Max(0, Math.Min(100, Math.Round(value))) : 0;
        }

        public void SetPreviewMetrics(UsageMetrics metrics)
        {
            previewMetrics = metrics;
            RefreshQuota();
        }

        public void SetPreviewCodexCustomMetrics(CodexCustomApiMetrics metrics)
        {
            previewCodexCustomMetrics = metrics;
            RefreshCodexDetails();
        }

        private void ApplyQuotaMetrics(UsageMetrics metrics)
        {
            QuotaDisplaySelection selection = QuotaDisplaySelector.Select(metrics);
            quotaLabelKey = selection.LabelKey;
            quotaLabel.Text = L10n.Instance[quotaLabelKey];
            quotaRow.Visibility = Visibility.Visible;
            quotaRow.Margin = new Thickness(0);
            ApplyQuota(selection.HasQuota, selection.UsedPercent,
                selection.ResetUtc, quotaValue, quotaReset, quotaBar);
        }

        private static void ApplyQuota(bool available, double usedPercent,
            DateTime resetUtc, TextBlock value, TextBlock reset, RoundedMeter bar)
        {
            if (!available)
            {
                value.Text = L10n.Instance["quota.none_available"];
                reset.Text = String.Empty;
                reset.Visibility = Visibility.Collapsed;
                bar.Value = 0;
                return;
            }
            if (IsQuotaExpired(resetUtc, DateTime.UtcNow))
            {
                value.Text = L10n.Instance["quota.waiting_refresh"];
                reset.Text = String.Empty;
                reset.Visibility = Visibility.Collapsed;
                bar.Value = 0;
                return;
            }
            double remaining = Math.Max(0, Math.Min(100, 100 - usedPercent));
            value.Text = L10n.Instance.Format("quota.remaining", (int)Math.Round(remaining));
            reset.Text = FormatResetTime(resetUtc);
            reset.Visibility = String.IsNullOrEmpty(reset.Text)
                ? Visibility.Collapsed : Visibility.Visible;
            bar.Value = remaining;
        }

        public static bool IsQuotaExpired(DateTime resetUtc, DateTime nowUtc)
        {
            return resetUtc != DateTime.MinValue && nowUtc >= resetUtc.ToUniversalTime();
        }

        public static string FormatResetTime(DateTime resetUtc)
        {
            if (resetUtc == DateTime.MinValue)
            {
                return String.Empty;
            }
            DateTime local = resetUtc.ToLocalTime();
            var culture = new CultureInfo(L10n.Instance["date.culture"]);
            var format = local.Date == DateTime.Now.Date
                ? L10n.Instance["date.today_format"]
                : L10n.Instance["date.other_format"];
            return local.ToString(format, culture);
        }

        private static Grid CreateQuotaRow(string title, out TextBlock name,
            out TextBlock reset, out TextBlock value, out RoundedMeter bar)
        {
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid labels = new Grid();
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            labels.ColumnDefinitions.Add(new ColumnDefinition());
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            name = NewText(title, 12, MediaColor.FromRgb(99, 112, 120),
                FontWeights.Normal, true);
            labels.Children.Add(name);
            reset = NewText(String.Empty, 10.5, MediaColor.FromRgb(130, 145, 153),
                FontWeights.Normal, true);
            reset.Margin = new Thickness(6, 1, 6, 0);
            reset.VerticalAlignment = VerticalAlignment.Center;
            reset.Visibility = Visibility.Collapsed;
            Grid.SetColumn(reset, 1);
            labels.Children.Add(reset);
            value = NewText(L10n.Instance["quota.no_data"], 12, MediaColor.FromRgb(48, 60, 68),
                FontWeights.SemiBold, true);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 2);
            labels.Children.Add(value);
            grid.Children.Add(labels);
            bar = new RoundedMeter
            {
                Height = 4,
                Margin = new Thickness(0, 5, 0, 0)
            };
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);
            return grid;
        }

        private static Border CreateInfoSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(0, 5, 0, 5),
                Background = new SolidColorBrush(MediaColor.FromArgb(58, 174, 189, 198))
            };
        }

        private static Grid CreateInfoRow(string title, out TextBlock titleBlock, out TextBlock value)
        {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            titleBlock = NewText(title, 12, MediaColor.FromRgb(99, 112, 120),
                FontWeights.Normal, true);
            grid.Children.Add(titleBlock);
            value = NewText(L10n.Instance["quota.no_data"], 12, MediaColor.FromRgb(48, 60, 68),
                FontWeights.SemiBold, true);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            return grid;
        }


        private static string FormatCompactNumber(long value)
        {
            if (value >= 1000)
            {
                double thousands = value / 1000.0;
                return thousands >= 10
                    ? String.Format(CultureInfo.InvariantCulture, "{0:0}k", thousands)
                    : String.Format(CultureInfo.InvariantCulture, "{0:0.#}k", thousands);
            }
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static MediaBrush CreateFallbackGlassBrush()
        {
            System.Windows.Media.LinearGradientBrush brush =
                new System.Windows.Media.LinearGradientBrush();
            brush.StartPoint = new MediaPoint(0, 0);
            brush.EndPoint = new MediaPoint(0, 1);
            brush.GradientStops.Add(new GradientStop(
                MediaColor.FromArgb(244, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(
                MediaColor.FromArgb(235, 250, 252, 253), 0.58));
            brush.GradientStops.Add(new GradientStop(
                MediaColor.FromArgb(241, 255, 255, 255), 1));
            return brush;
        }

        private static TextBlock NewText(string text, double size, MediaColor color,
            FontWeight weight)
        {
            return NewText(text, size, color, weight, false);
        }

        private static TextBlock NewText(string text, double size, MediaColor color,
            FontWeight weight, bool chineseUi)
        {
            TextBlock block = new TextBlock
            {
                Text = text,
                FontFamily = new System.Windows.Media.FontFamily(
                    chineseUi ? "Microsoft YaHei UI" : "Segoe UI Variable Text"),
                FontSize = size,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            block.Language = System.Windows.Markup.XmlLanguage.GetLanguage(
                chineseUi ? "zh-CN" : "en-US");
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(block, TextRenderingMode.Auto);
            return block;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private void RefreshAllText()
        {
            quotaLabel.Text = L10n.Instance[quotaLabelKey];
            infoProjectTitle.Text = L10n.Instance["metadata.project"];
            infoModelTitle.Text = L10n.Instance["metadata.model"];
            infoTokenTitle.Text = L10n.Instance["metadata.tokens"];
            if (currentAggregate != null)
                UpdateContent(currentAggregate, currentSessions);
        }
    }
}
