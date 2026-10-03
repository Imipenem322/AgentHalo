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
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        private readonly Border agentSelector;
        private readonly TextBlock fiveHourLabel;
        private readonly TextBlock fiveHourValue;
        private readonly TextBlock fiveHourReset;
        private readonly Grid fiveHourRow;
        private readonly TextBlock longTermLabel;
        private readonly TextBlock longTermValue;
        private readonly TextBlock longTermReset;
        private readonly Grid longTermRow;
        private readonly ContextBatteryMeter contextMeter;
        private readonly Button firstSegmentButton;
        private readonly Button secondSegmentButton;
        private readonly TextBlock firstSegmentText;
        private readonly TextBlock secondSegmentText;
        private readonly Grid splitAgentSelector;
        private readonly Button dropdownAgentButton;
        private readonly TextBlock dropdownAgentText;
        private readonly ContextMenu agentMenu;
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
        private readonly StackPanel deepSeekGroup;
        private readonly TextBlock deepSeekTaskLabel;
        private readonly TextBlock deepSeekTaskValue;
        private readonly TextBlock deepSeekModelLabel;
        private readonly TextBlock deepSeekModelValue;
        private readonly RoundedMeter fiveHourBar;
        private readonly RoundedMeter longTermBar;
        private string longTermLabelKey;
        private AgentProviderSnapshot currentSnapshot;
        private AgentKind selectedAgent;
        private bool agentSwitchingEnabled = true;
        private readonly List<AgentProviderDescriptor> enabledAgentDescriptors =
            new List<AgentProviderDescriptor>();
        private AgentProviderDescriptor firstSegmentAgent;
        private AgentProviderDescriptor secondSegmentAgent;

        internal event Action<AgentKind> AgentFocusRequested;

        internal AgentKind SelectedAgentForDiagnostics
        {
            get { return selectedAgent; }
        }

        internal bool QuotaVisibleForDiagnostics
        {
            get { return quotaGroup.Visibility == Visibility.Visible; }
        }

        internal bool InformationVisibleForDiagnostics
        {
            get { return infoGroup.Visibility == Visibility.Visible; }
        }

        internal bool ContextVisibleForDiagnostics
        {
            get { return contextMeter.Visibility == Visibility.Visible; }
        }

        internal bool DeepSeekTaskVisibleForDiagnostics
        {
            get { return deepSeekGroup.Visibility == Visibility.Visible; }
        }

        internal string DeepSeekTaskTitleForDiagnostics
        {
            get { return deepSeekTaskValue.Text; }
        }

        internal string DeepSeekModelNameForDiagnostics
        {
            get { return deepSeekModelValue.Text; }
        }

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
            statusRow.ColumnDefinitions.Add(new ColumnDefinition
                { Width = GridLength.Auto });
            headline = NewText("OFFLINE", 20,
                MediaColor.FromRgb(40, 52, 60), FontWeights.Bold);
            headline.VerticalAlignment = VerticalAlignment.Center;
            statusRow.Children.Add(headline);

            agentSelector = new Border
            {
                Width = 84,
                Height = 24,
                Margin = new Thickness(5, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(
                    MediaColor.FromArgb(70, 150, 177, 191)),
                Background = new SolidColorBrush(
                    MediaColor.FromArgb(82, 231, 239, 244)),
                ClipToBounds = true
            };
            Grid selectorContent = new Grid();
            splitAgentSelector = new Grid();
            splitAgentSelector.ColumnDefinitions.Add(new ColumnDefinition());
            splitAgentSelector.ColumnDefinitions.Add(new ColumnDefinition());
            Style segmentStyle = CreateAgentSegmentStyle();
            firstSegmentText = NewText(L10n.Instance["agent.codex"], 10.5,
                MediaColor.FromRgb(64, 83, 94), FontWeights.SemiBold, true);
            firstSegmentText.HorizontalAlignment = HorizontalAlignment.Center;
            firstSegmentText.VerticalAlignment = VerticalAlignment.Center;
            firstSegmentButton = CreateAgentSegmentButton(firstSegmentText,
                segmentStyle, L10n.Instance["agent.codex"]);
            firstSegmentButton.Click += delegate
            {
                if (firstSegmentAgent != null)
                {
                    RequestAgentFocus(firstSegmentAgent.Kind);
                }
            };
            splitAgentSelector.Children.Add(firstSegmentButton);

            secondSegmentText = NewText(String.Empty, 10.5,
                MediaColor.FromRgb(64, 83, 94), FontWeights.SemiBold, true);
            secondSegmentText.HorizontalAlignment = HorizontalAlignment.Center;
            secondSegmentText.VerticalAlignment = VerticalAlignment.Center;
            secondSegmentButton = CreateAgentSegmentButton(
                secondSegmentText, segmentStyle, String.Empty);
            secondSegmentButton.Click += delegate
            {
                if (secondSegmentAgent != null)
                {
                    RequestAgentFocus(secondSegmentAgent.Kind);
                }
            };
            Grid.SetColumn(secondSegmentButton, 1);
            splitAgentSelector.Children.Add(secondSegmentButton);
            Border selectorDivider = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
                Background = new SolidColorBrush(
                    MediaColor.FromArgb(58, 142, 166, 178)),
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(selectorDivider, 2);
            splitAgentSelector.Children.Add(selectorDivider);
            KeyboardNavigation.SetTabNavigation(splitAgentSelector,
                KeyboardNavigationMode.Cycle);

            StackPanel dropdownContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dropdownAgentText = NewText(String.Empty, 10.5,
                MediaColor.FromRgb(34, 84, 113), FontWeights.SemiBold, true);
            dropdownContent.Children.Add(dropdownAgentText);
            System.Windows.Shapes.Path dropdownChevron =
                new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 0,0 L 4,4 L 8,0"),
                    Width = 8,
                    Height = 4,
                    Stretch = Stretch.Fill,
                    Stroke = new SolidColorBrush(
                        MediaColor.FromRgb(64, 83, 94)),
                    StrokeThickness = 1.25,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
            dropdownContent.Children.Add(dropdownChevron);
            dropdownAgentButton = CreateAgentSegmentButton(dropdownContent,
                segmentStyle, L10n.Instance["menu.current_agent"]);
            agentMenu = new ContextMenu();
            dropdownAgentButton.ContextMenu = agentMenu;
            dropdownAgentButton.Click += delegate
            {
                agentMenu.PlacementTarget = dropdownAgentButton;
                agentMenu.Placement = PlacementMode.Bottom;
                agentMenu.IsOpen = true;
            };
            selectorContent.Children.Add(splitAgentSelector);
            selectorContent.Children.Add(dropdownAgentButton);
            agentSelector.Child = selectorContent;
            Grid.SetColumn(agentSelector, 1);
            statusRow.Children.Add(agentSelector);

            contextMeter = new ContextBatteryMeter
            {
                Width = 46,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(contextMeter, 2);
            statusRow.Children.Add(contextMeter);
            content.Children.Add(statusRow);

            subtitle = NewText(L10n.Instance["status.offline"], 13,
                MediaColor.FromRgb(103, 117, 126), FontWeights.Normal, true);
            subtitle.Margin = new Thickness(0, 1, 0, 13);
            content.Children.Add(subtitle);

            longTermLabelKey = "quota.weekly";
            quotaGroup = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            fiveHourRow = CreateQuotaRow(L10n.Instance["quota.5h"],
                out fiveHourLabel, out fiveHourReset, out fiveHourValue,
                out fiveHourBar);
            quotaGroup.Children.Add(fiveHourRow);
            longTermRow = CreateQuotaRow(L10n.Instance[longTermLabelKey],
                out longTermLabel, out longTermReset, out longTermValue,
                out longTermBar);
            longTermRow.Margin = new Thickness(0, 11, 0, 0);
            quotaGroup.Children.Add(longTermRow);

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

            deepSeekGroup = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            Grid deepSeekTaskRow = CreateDeepSeekRow(
                "details.deepseek.task_label", out deepSeekTaskLabel,
                out deepSeekTaskValue);
            deepSeekTaskValue.TextWrapping = TextWrapping.Wrap;
            deepSeekTaskValue.TextTrimming = TextTrimming.CharacterEllipsis;
            deepSeekTaskValue.MaxHeight = 32;
            deepSeekTaskValue.LineHeight = 15;
            deepSeekGroup.Children.Add(deepSeekTaskRow);
            Grid deepSeekModelRow = CreateDeepSeekRow(
                "details.deepseek.main_model_label", out deepSeekModelLabel,
                out deepSeekModelValue);
            deepSeekModelValue.TextWrapping = TextWrapping.NoWrap;
            deepSeekModelValue.TextTrimming = TextTrimming.CharacterEllipsis;
            deepSeekModelValue.MaxHeight = 18;
            deepSeekModelRow.Margin = new Thickness(0, 11, 0, 0);
            deepSeekGroup.Children.Add(deepSeekModelRow);

            dataLayer = new Grid();
            dataLayer.Height = 80;
            dataLayer.MinHeight = 80;
            dataLayer.VerticalAlignment = VerticalAlignment.Top;
            dataLayer.Children.Add(quotaGroup);
            dataLayer.Children.Add(infoGroup);
            dataLayer.Children.Add(deepSeekGroup);
            content.Children.Add(dataLayer);
            UpdateAgentSelector(AgentKind.Codex);

            Grid layers = new Grid();
            layers.Children.Add(content);
            shell.Child = layers;
            Content = shell;
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

        internal void UpdateContent(AgentProviderSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Aggregate == null)
            {
                return;
            }
            currentSnapshot = snapshot;
            AggregateSnapshot aggregate = snapshot.Aggregate;
            List<SessionSnapshot> sessions = snapshot.Sessions
                ?? new List<SessionSnapshot>();
            headline.Text = aggregate.Label;
            MediaColor accent = HaloVisual.StateColor(aggregate.State);
            headline.Foreground = new SolidColorBrush(accent);
            AgentDetailsSnapshot details = snapshot.Details;
            if (IsDeepSeekTask(snapshot))
            {
                bool paused = String.Equals(aggregate.Label, "PAUSED",
                    StringComparison.OrdinalIgnoreCase);
                bool providerSaysPaused = String.Equals(
                    details.StatusDetailKey, "status.paused",
                    StringComparison.Ordinal);
                subtitle.Text = paused && !providerSaysPaused
                    ? L10n.Instance["status.paused"]
                    : L10n.Instance[LocalizedKeyOrDefault(
                        details.StatusDetailKey, "status.deepseek.unknown")];
            }
            else
            {
                subtitle.Text = FriendlyStatusDetail(aggregate, sessions);
            }
            UpdateAgentSelector(aggregate.FocusedAgent);
            RefreshSupplementalData(snapshot);
        }

        internal void SetAgentSwitchingEnabled(bool enabled)
        {
            agentSwitchingEnabled = enabled;
            ApplyAgentSwitchingState();
        }

        internal void SetEnabledAgents(IEnumerable<AgentKind> enabledAgents)
        {
            List<AgentProviderDescriptor> descriptors =
                new List<AgentProviderDescriptor>();
            if (enabledAgents != null)
            {
                foreach (AgentKind agent in enabledAgents)
                {
                    descriptors.Add(CreateSelectorDescriptor(agent));
                }
            }
            SetEnabledAgentDescriptors(descriptors);
        }

        internal void SetEnabledAgentDescriptors(
            IEnumerable<AgentProviderDescriptor> descriptors)
        {
            List<AgentProviderDescriptor> next =
                new List<AgentProviderDescriptor>();
            if (descriptors != null)
            {
                next.AddRange(descriptors
                    .Where(delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor != null;
                    })
                    .OrderBy(delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor.Order;
                    }));
            }
            if (!SameAgentOptions(enabledAgentDescriptors, next))
            {
                enabledAgentDescriptors.Clear();
                enabledAgentDescriptors.AddRange(next);
                RebuildAgentMenu();
            }
            UpdateAgentSelector(selectedAgent);
            ApplyAgentSwitchingState();
        }

        private static bool SameAgentOptions(
            IList<AgentProviderDescriptor> current,
            IList<AgentProviderDescriptor> next)
        {
            if (current.Count != next.Count)
            {
                return false;
            }
            for (int index = 0; index < current.Count; index++)
            {
                AgentProviderDescriptor left = current[index];
                AgentProviderDescriptor right = next[index];
                if (left.Kind != right.Kind || left.Order != right.Order ||
                    !String.Equals(left.Key, right.Key,
                        StringComparison.Ordinal) ||
                    !String.Equals(left.DisplayNameKey,
                        right.DisplayNameKey, StringComparison.Ordinal) ||
                    !String.Equals(left.DisplayName, right.DisplayName,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static AgentProviderDescriptor CreateSelectorDescriptor(
            AgentKind kind)
        {
            string key;
            string name;
            switch (kind)
            {
                case AgentKind.DeepSeekHarness:
                    key = "agent.deepseek-harness";
                    name = "DeepSeek Harness";
                    break;
                default:
                    key = "agent.codex";
                    name = "Codex";
                    break;
            }
            return new AgentProviderDescriptor
            {
                Kind = kind,
                Order = kind == AgentKind.DeepSeekHarness ? 1 : 0,
                DisplayName = name,
                DisplayNameKey = key
            };
        }

        private static string FullAgentName(
            AgentProviderDescriptor descriptor)
        {
            string translated = String.IsNullOrWhiteSpace(
                descriptor.DisplayNameKey) ? String.Empty :
                L10n.Instance[descriptor.DisplayNameKey];
            return String.IsNullOrEmpty(translated) ||
                String.Equals(translated, descriptor.DisplayNameKey,
                    StringComparison.Ordinal)
                ? descriptor.DisplayName : translated;
        }

        private static string ShortAgentName(
            AgentProviderDescriptor descriptor)
        {
            string key = descriptor.DisplayNameKey + ".short";
            string translated = L10n.Instance[key];
            return String.Equals(translated, key, StringComparison.Ordinal)
                ? FullAgentName(descriptor) : translated;
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
                return String.IsNullOrWhiteSpace(aggregate.Detail)
                    ? L10n.Instance["status.offline"] : aggregate.Detail;
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

        private void RefreshSupplementalData(AgentProviderSnapshot snapshot)
        {
            AgentDetailsSnapshot details = snapshot == null
                ? null : snapshot.Details;
            AggregateSnapshot aggregate = snapshot == null
                ? null : snapshot.Aggregate;
            dataLayer.Height = IsDeepSeekTask(snapshot) ? Double.NaN : 80;
            deepSeekGroup.Visibility = Visibility.Collapsed;
            if (details != null &&
                details.Mode == AgentDetailsMode.DeepSeekTask)
            {
                ApplyDeepSeekTaskDetails(details);
                return;
            }
            if (IsOfflineAggregate(aggregate))
            {
                ApplyOfflinePlaceholders(details);
                return;
            }
            contextMeter.Visibility = Visibility.Visible;
            RefreshDetails(details);
        }

        private void ApplyDeepSeekTaskDetails(AgentDetailsSnapshot details)
        {
            quotaGroup.Visibility = Visibility.Collapsed;
            infoGroup.Visibility = Visibility.Collapsed;
            contextMeter.Visibility = Visibility.Collapsed;

            string taskTitle = String.IsNullOrWhiteSpace(details.TaskTitle)
                ? L10n.Instance[LocalizedKeyOrDefault(details.TaskTitleKey,
                    "details.deepseek.title.no_task")]
                : details.TaskTitle;
            string modelName = String.IsNullOrWhiteSpace(details.ModelName)
                ? L10n.Instance[LocalizedKeyOrDefault(details.ModelNameKey,
                    "details.deepseek.model.not_fetched")]
                : details.ModelName;
            deepSeekTaskValue.Text = taskTitle;
            deepSeekTaskValue.ToolTip = taskTitle;
            AutomationProperties.SetName(deepSeekTaskValue,
                deepSeekTaskLabel.Text);
            AutomationProperties.SetHelpText(deepSeekTaskValue, taskTitle);

            deepSeekModelValue.Text = modelName;
            string modelSource = String.IsNullOrWhiteSpace(
                details.ModelSourceKey) ? String.Empty :
                L10n.Instance[details.ModelSourceKey];
            deepSeekModelValue.ToolTip = String.IsNullOrEmpty(modelSource)
                ? modelName : modelName + Environment.NewLine + modelSource;
            AutomationProperties.SetName(deepSeekModelValue,
                deepSeekModelLabel.Text);
            AutomationProperties.SetHelpText(deepSeekModelValue,
                deepSeekModelValue.ToolTip.ToString());
            deepSeekGroup.Visibility = Visibility.Visible;
        }

        private static string LocalizedKeyOrDefault(string key,
            string fallbackKey)
        {
            return String.IsNullOrWhiteSpace(key) ? fallbackKey : key;
        }

        private static bool IsDeepSeekTask(AgentProviderSnapshot snapshot)
        {
            return snapshot != null && snapshot.Details != null &&
                snapshot.Details.Mode == AgentDetailsMode.DeepSeekTask;
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

        private void ApplyOfflinePlaceholders(AgentDetailsSnapshot details)
        {
            if (details != null &&
                details.Mode == AgentDetailsMode.Information)
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
                RefreshQuota(details == null ? null : details.Usage);
            }
            // Drop the context pill rather than echoing a percentage from the
            // session that just went offline. Done after RefreshQuota since
            // that path resets context-pill visibility.
            contextMeter.Visibility = Visibility.Collapsed;
        }

        private void RefreshDetails(AgentDetailsSnapshot details)
        {
            if (details != null &&
                details.Mode == AgentDetailsMode.Information)
            {
                ApplyInformationDetails(details);
                return;
            }
            RefreshQuota(details == null ? null : details.Usage);
        }

        private void ApplyInformationDetails(AgentDetailsSnapshot details)
        {
            quotaGroup.Visibility = Visibility.Collapsed;
            infoGroup.Visibility = Visibility.Visible;
            infoProjectRow.Visibility = Visibility.Visible;
            infoProjectSeparator.Visibility = Visibility.Visible;
            infoModelSeparator.Visibility = Visibility.Visible;
            infoModelRow.Margin = new Thickness(0);
            infoTokenRow.Margin = new Thickness(0);
            infoProjectValue.Text = details.HasProject
                ? details.ProjectName : L10n.Instance["quota.no_data"];
            infoModelValue.Text = details.HasModel
                ? details.ModelName : L10n.Instance["quota.no_data"];
            infoTokenValue.Text = details.HasTokenUsage
                ? "↑ " + FormatCompactNumber(details.InputTokens) +
                  "  ·  ↓ " + FormatCompactNumber(details.OutputTokens)
                : L10n.Instance["quota.no_data"];
            SetContextPercent(details.HasContext,
                details.ContextUsedPercent);
        }

        private void RefreshQuota(UsageMetrics metrics)
        {
            quotaGroup.Visibility = Visibility.Visible;
            infoGroup.Visibility = Visibility.Collapsed;
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

        private void RequestAgentFocus(AgentKind agent)
        {
            if (!agentSwitchingEnabled || !enabledAgentDescriptors.Any(
                    delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor.Kind == agent;
                    }))
            {
                return;
            }
            Action<AgentKind> handler = AgentFocusRequested;
            if (handler != null)
            {
                handler(agent);
            }
        }

        private void ApplyAgentSwitchingState()
        {
            bool canSwitch = enabledAgentDescriptors.Count > 1;
            bool useDropdown = enabledAgentDescriptors.Count > 2;
            agentSelector.Visibility = canSwitch
                ? Visibility.Visible : Visibility.Collapsed;
            splitAgentSelector.Visibility = useDropdown
                ? Visibility.Collapsed : Visibility.Visible;
            dropdownAgentButton.Visibility = useDropdown
                ? Visibility.Visible : Visibility.Collapsed;
            firstSegmentButton.IsEnabled = agentSwitchingEnabled &&
                firstSegmentAgent != null;
            secondSegmentButton.IsEnabled = agentSwitchingEnabled &&
                secondSegmentAgent != null;
            dropdownAgentButton.IsEnabled = agentSwitchingEnabled;
            foreach (object item in agentMenu.Items)
            {
                ((MenuItem)item).IsEnabled = agentSwitchingEnabled;
            }
        }

        private void UpdateAgentSelector(AgentKind agent)
        {
            selectedAgent = agent;
            firstSegmentAgent = enabledAgentDescriptors.Count == 2
                ? enabledAgentDescriptors[0] : null;
            secondSegmentAgent = enabledAgentDescriptors.Count == 2
                ? enabledAgentDescriptors[1] : null;
            SetSegmentOption(firstSegmentButton, firstSegmentText,
                firstSegmentAgent, agent);
            SetSegmentOption(secondSegmentButton, secondSegmentText,
                secondSegmentAgent, agent);

            AgentProviderDescriptor selected = enabledAgentDescriptors
                .FirstOrDefault(delegate(AgentProviderDescriptor descriptor)
                {
                    return descriptor.Kind == agent;
                });
            string name = selected == null
                ? L10n.Instance[AgentDisplayNameKey(agent)]
                : FullAgentName(selected);
            dropdownAgentText.Text = selected == null
                ? L10n.Instance[AgentShortNameKey(agent)]
                : ShortAgentName(selected);
            AutomationProperties.SetName(dropdownAgentButton, name);
            ToolTipService.SetToolTip(dropdownAgentButton, name);

            foreach (MenuItem item in agentMenu.Items)
            {
                item.IsChecked = (AgentKind)item.Tag == agent;
            }
            ApplyAgentSwitchingState();
        }

        private void RebuildAgentMenu()
        {
            agentMenu.Items.Clear();
            foreach (AgentProviderDescriptor descriptor in
                enabledAgentDescriptors)
            {
                MenuItem item = new MenuItem
                {
                    Header = FullAgentName(descriptor),
                    IsCheckable = true,
                    IsChecked = descriptor.Kind == selectedAgent,
                    Tag = descriptor.Kind,
                    IsEnabled = agentSwitchingEnabled
                };
                AutomationProperties.SetName(item,
                    FullAgentName(descriptor));
                item.Click += delegate(object sender, RoutedEventArgs e)
                {
                    RequestAgentFocus((AgentKind)((MenuItem)sender).Tag);
                };
                agentMenu.Items.Add(item);
            }
        }

        private static void SetSegmentOption(Button button, TextBlock text,
            AgentProviderDescriptor descriptor, AgentKind selected)
        {
            if (descriptor == null)
            {
                text.Text = String.Empty;
                button.Tag = false;
                ToolTipService.SetToolTip(button, null);
                AutomationProperties.SetName(button, String.Empty);
                return;
            }
            string name = FullAgentName(descriptor);
            text.Text = ShortAgentName(descriptor);
            text.Foreground = new SolidColorBrush(
                descriptor.Kind == selected
                    ? MediaColor.FromRgb(34, 84, 113)
                    : MediaColor.FromRgb(78, 94, 103));
            button.Tag = descriptor.Kind == selected;
            ToolTipService.SetToolTip(button, name);
            AutomationProperties.SetName(button, name);
        }

        private static string AgentDisplayNameKey(AgentKind agent)
        {
            switch (agent)
            {
                case AgentKind.DeepSeekHarness:
                    return "agent.deepseek-harness";
                default: return "agent.codex";
            }
        }

        private static string AgentShortNameKey(AgentKind agent)
        {
            return AgentDisplayNameKey(agent) + ".short";
        }

        private static Button CreateAgentSegmentButton(UIElement content,
            Style style, string accessibleName)
        {
            Button button = new Button
            {
                Content = content,
                Style = style,
                Focusable = true,
                IsTabStop = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = false
            };
            AutomationProperties.SetName(button, accessibleName);
            ToolTipService.SetToolTip(button, accessibleName);
            return button;
        }

        private static Style CreateAgentSegmentStyle()
        {
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty,
                System.Windows.Media.Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty,
                System.Windows.Media.Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty,
                new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty,
                new Thickness(0)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty,
                new Thickness(0)));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, true));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty,
                Cursors.Hand));

            FrameworkElementFactory border =
                new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background")
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderThicknessProperty,
                new Binding("BorderThickness")
                {
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.TemplatedParent)
                });
            FrameworkElementFactory presenter =
                new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            presenter.SetBinding(ContentPresenter.ContentProperty,
                new Binding("Content")
                {
                    RelativeSource = new RelativeSource(
                        RelativeSourceMode.TemplatedParent)
                });
            border.AppendChild(presenter);
            ControlTemplate template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            Trigger selected = new Trigger
            {
                Property = FrameworkElement.TagProperty,
                Value = true
            };
            selected.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(MediaColor.FromArgb(175, 205, 230, 245))));
            style.Triggers.Add(selected);

            Trigger hovered = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true
            };
            hovered.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(MediaColor.FromArgb(145, 218, 235, 245))));
            style.Triggers.Add(hovered);

            Trigger focused = new Trigger
            {
                Property = UIElement.IsKeyboardFocusedProperty,
                Value = true
            };
            focused.Setters.Add(new Setter(Control.BorderBrushProperty,
                new SolidColorBrush(MediaColor.FromRgb(73, 129, 160))));
            style.Triggers.Add(focused);

            Trigger disabled = new Trigger
            {
                Property = UIElement.IsEnabledProperty,
                Value = false
            };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42));
            disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty,
                Cursors.Arrow));
            style.Triggers.Add(disabled);
            return style;
        }

        private void SetContextPercent(bool available, double value)
        {
            contextMeter.IsAvailable = available;
            contextMeter.Value = available
                ? Math.Max(0, Math.Min(100, Math.Round(value))) : 0;
        }

        private void ApplyQuotaMetrics(UsageMetrics metrics)
        {
            QuotaDisplaySet display = QuotaDisplaySelector.Select(metrics);
            fiveHourLabel.Text = L10n.Instance["quota.5h"];
            longTermLabelKey = display.LongTerm.LabelKey;
            longTermLabel.Text = L10n.Instance[longTermLabelKey];
            fiveHourRow.Visibility = Visibility.Visible;
            longTermRow.Visibility = Visibility.Visible;
            ApplyQuota(display.FiveHour.HasQuota,
                display.FiveHour.UsedPercent, display.FiveHour.ResetUtc,
                fiveHourValue, fiveHourReset, fiveHourBar);
            ApplyQuota(display.LongTerm.HasQuota,
                display.LongTerm.UsedPercent, display.LongTerm.ResetUtc,
                longTermValue, longTermReset, longTermBar);
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

        private static Grid CreateDeepSeekRow(string labelKey,
            out TextBlock label, out TextBlock value)
        {
            Grid row = new Grid();
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            label = NewText(L10n.Instance[labelKey], 12,
                MediaColor.FromRgb(99, 112, 120), FontWeights.Normal, true);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            value = NewText(String.Empty, 12,
                MediaColor.FromRgb(48, 60, 68), FontWeights.SemiBold, true);
            value.HorizontalAlignment = HorizontalAlignment.Left;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.Margin = new Thickness(0, 5, 0, 0);
            Grid.SetRow(value, 1);
            row.Children.Add(value);
            return row;
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
            RebuildAgentMenu();
            UpdateAgentSelector(selectedAgent);
            fiveHourLabel.Text = L10n.Instance["quota.5h"];
            longTermLabel.Text = L10n.Instance[longTermLabelKey];
            infoProjectTitle.Text = L10n.Instance["metadata.project"];
            infoModelTitle.Text = L10n.Instance["metadata.model"];
            infoTokenTitle.Text = L10n.Instance["metadata.tokens"];
            deepSeekTaskLabel.Text =
                L10n.Instance["details.deepseek.task_label"];
            deepSeekModelLabel.Text =
                L10n.Instance["details.deepseek.main_model_label"];
            if (currentSnapshot != null)
                UpdateContent(currentSnapshot);
        }
    }
}
