using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexHalo
{
internal sealed class CodexAgentProvider : IAgentProvider
    {
        internal const AgentCapability SupportedCapabilities =
            AgentCapability.Lifecycle |
            AgentCapability.ToolName |
            AgentCapability.Attention |
            AgentCapability.Usage |
            AgentCapability.ContextWindow |
            AgentCapability.WindowActivation;

        private readonly CodexSessionMonitor monitor;
        private readonly CodexUsageMonitor usageMonitor;
        private long usageActivationEpoch;
        private volatile bool active;
        private volatile bool disposed;

        public CodexAgentProvider()
        {
            monitor = new CodexSessionMonitor();
            usageMonitor = CodexUsageMonitor.Instance;
            monitor.Changed += OnMonitorChanged;
            usageMonitor.UpdatedForActivation += OnUsageUpdated;
        }

        public AgentKind Kind
        {
            get { return AgentKind.Codex; }
        }

        public AgentCapability Capabilities
        {
            get { return SupportedCapabilities; }
        }

        public event EventHandler Changed;

        public void Start()
        {
            if (disposed || active)
            {
                return;
            }
            try
            {
                monitor.Start();
                active = true;
                long activatedEpoch = usageMonitor.Activate();
                if (activatedEpoch == 0)
                {
                    throw new ObjectDisposedException("CodexUsageMonitor");
                }
                Interlocked.Exchange(ref usageActivationEpoch,
                    activatedEpoch);
            }
            catch
            {
                active = false;
                long failedEpoch = Interlocked.Exchange(
                    ref usageActivationEpoch, 0);
                try
                {
                    monitor.Stop();
                }
                catch
                {
                }
                try
                {
                    usageMonitor.Deactivate(failedEpoch);
                }
                catch
                {
                }
                throw;
            }
        }

        public void Stop()
        {
            if (disposed || !active)
            {
                return;
            }
            active = false;
            long stoppedEpoch = Interlocked.Exchange(
                ref usageActivationEpoch, 0);
            Exception failure = null;
            try
            {
                monitor.Stop();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            try
            {
                usageMonitor.Deactivate(stoppedEpoch);
            }
            catch (Exception ex)
            {
                if (failure == null)
                {
                    failure = ex;
                }
            }
            if (failure != null)
            {
                throw failure;
            }
        }

        public bool Refresh()
        {
            return active && !disposed;
        }

        public AgentProviderSnapshot Read(HaloSettings settings, DateTime nowUtc)
        {
            if (disposed)
            {
                throw new ObjectDisposedException("CodexAgentProvider");
            }
            HaloSettings effectiveSettings = settings ?? new HaloSettings();
            bool codexRunning = CodexRuntimeReader.IsRunning();
            AggregateSnapshot aggregate = monitor.GetAggregate(
                effectiveSettings, codexRunning);
            if (codexRunning && aggregate.Presence ==
                    AgentPresenceState.Standby)
            {
                string appFailure;
                DateTime appFailureUtc;
                bool hasAppFailure = CodexFailureReader.TryReadRecent(
                    out appFailure, out appFailureUtc);
                ApplyApplicationFailure(aggregate, effectiveSettings,
                    codexRunning, hasAppFailure, appFailure, appFailureUtc);
            }

            List<SessionSnapshot> sessions = monitor.GetAllRecent();
            UsageMetrics usage;
            if (!usageMonitor.TryReadCached(out usage))
            {
                usage = null;
            }
            CodexCustomApiMetrics customMetrics =
                CodexCustomApiMetricsReader.Read(sessions,
                    CodexProviderProfileReader.Read(), usageMonitor.Status);

            return new AgentProviderSnapshot
            {
                Aggregate = aggregate,
                Sessions = sessions,
                Details = CreateDetailsSnapshot(usage, customMetrics),
                Capabilities = SupportedCapabilities,
                Integration = new AgentIntegrationHealth
                {
                    State = AgentIntegrationState.NotRequired
                }
            };
        }

        internal static AgentDetailsSnapshot CreateDetailsSnapshot(
            UsageMetrics usage, CodexCustomApiMetrics customMetrics)
        {
            AgentDetailsSnapshot details = new AgentDetailsSnapshot
            {
                Agent = AgentKind.Codex,
                Mode = AgentDetailsMode.Quota,
                Usage = usage
            };
            if (customMetrics == null || !customMetrics.IsCustomApi)
            {
                return details;
            }
            details.Mode = AgentDetailsMode.Information;
            details.ProjectName = customMetrics.ProjectName;
            details.ModelName = customMetrics.Model;
            details.ProviderName = customMetrics.Provider;
            details.InputTokens = customMetrics.InputTokens;
            details.OutputTokens = customMetrics.OutputTokens;
            details.ContextInputTokens = customMetrics.ContextTokens;
            details.ContextWindowTokens = customMetrics.ContextWindowTokens;
            return details;
        }

        internal static void ApplyApplicationFailure(AggregateSnapshot aggregate,
            HaloSettings settings, bool codexRunning, bool hasAppFailure,
            string appFailure, DateTime appFailureUtc)
        {
            if (!codexRunning || aggregate == null ||
                aggregate.Presence != AgentPresenceState.Standby)
            {
                return;
            }
            if (!hasAppFailure || String.IsNullOrWhiteSpace(appFailure) ||
                appFailureUtc <= settings.GetAcknowledgedErrorUtc(
                    AgentKind.Codex))
            {
                return;
            }
            aggregate.State = HaloState.Error;
            aggregate.Label = CodexSessionMonitor.StateLabel(HaloState.Error);
            aggregate.Detail = appFailure;
            aggregate.TurnPhase = AgentTurnPhase.Failed;
            aggregate.Activity = AgentActivityKind.None;
            aggregate.EvidenceSource = AgentEvidenceSource.DiagnosticSqlite;
            aggregate.EvidenceKind = "application_failure";
            aggregate.AttentionReason = AgentAttentionReason.None;
            aggregate.FailureSeverity =
                AgentFailureSeverity.TransientApplication;
            if (aggregate.Sessions == null)
            {
                aggregate.Sessions = new List<SessionSnapshot>();
            }
            aggregate.Sessions.Add(new SessionSnapshot
            {
                ThreadId = "codex-app",
                ProjectName = "Codex",
                Agent = AgentKind.Codex,
                State = HaloState.Error,
                Action = appFailure,
                LastEventUtc = appFailureUtc,
                Active = false,
                TurnPhase = AgentTurnPhase.Failed,
                Activity = AgentActivityKind.None,
                EvidenceSource = AgentEvidenceSource.DiagnosticSqlite,
                EvidenceKind = "application_failure",
                FailureSeverity = AgentFailureSeverity.TransientApplication
            });
        }

        public bool IsForeground(IntPtr foregroundWindow)
        {
            try
            {
                if (foregroundWindow == IntPtr.Zero)
                {
                    return false;
                }
                uint processId;
                GetWindowThreadProcessId(foregroundWindow, out processId);
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return IsCodexProcess(process);
                }
            }
            catch
            {
                return false;
            }
        }

        public bool TryActivateWindow()
        {
            try
            {
                int currentProcessId;
                using (Process current = Process.GetCurrentProcess())
                {
                    currentProcessId = current.Id;
                }
                Process candidate = null;
                Process[] processes = Process.GetProcesses();
                foreach (Process process in processes)
                {
                    bool selected = false;
                    try
                    {
                        selected = candidate == null &&
                            process.Id != currentProcessId &&
                            process.MainWindowHandle != IntPtr.Zero &&
                            IsCodexProcess(process);
                        if (selected)
                        {
                            candidate = process;
                        }
                    }
                    catch
                    {
                    }
                    if (!selected)
                    {
                        process.Dispose();
                    }
                }
                if (candidate == null)
                {
                    return false;
                }
                try
                {
                    ShowWindow(candidate.MainWindowHandle, 9);
                    return SetForegroundWindow(candidate.MainWindowHandle);
                }
                finally
                {
                    candidate.Dispose();
                }
            }
            catch (Exception ex)
            {
                SettingsStorage.Log("Bring Codex forward failed: " + ex.Message);
                return false;
            }
        }

        private static bool IsCodexProcess(Process process)
        {
            return process.ProcessName.IndexOf("codex",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   process.MainWindowTitle.IndexOf("codex",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnMonitorChanged(object sender, EventArgs e)
        {
            RaiseChanged();
        }

        private void OnUsageUpdated(long activationEpoch)
        {
            if (activationEpoch == 0 || activationEpoch !=
                Interlocked.Read(ref usageActivationEpoch))
            {
                return;
            }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            if (!active || disposed)
            {
                return;
            }
            EventHandler handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            Exception failure = null;
            try
            {
                Stop();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            disposed = true;
            monitor.Changed -= OnMonitorChanged;
            usageMonitor.UpdatedForActivation -= OnUsageUpdated;
            try
            {
                monitor.Dispose();
            }
            catch (Exception ex)
            {
                if (failure == null)
                {
                    failure = ex;
                }
            }
            if (failure != null)
            {
                SettingsStorage.Log("Codex provider dispose failed: " +
                    failure.Message);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd,
            out uint processId);
    }
}
