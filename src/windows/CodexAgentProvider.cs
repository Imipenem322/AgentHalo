using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly string codexDataRoot;
        private readonly object lifecycleGate = new object();
        private readonly Func<string[]> processProbe;
        private readonly Timer processTimer;
        private volatile bool codexRunning;
        private volatile bool processStateKnown;
        private string[] processIdentities = new string[0];
        private volatile bool collecting;
        private CodexCustomApiMetrics lastCustomMetrics;
        private long usageActivationEpoch;
        private volatile bool active;
        private volatile bool disposed;

        public CodexAgentProvider()
            : this(new CodexSessionMonitor(AgentHaloPaths.CodexDataRootDirectory()),
                CodexUsageMonitor.Instance, ProbeCodexProcess)
        {
        }

        internal CodexAgentProvider(CodexSessionMonitor sessionMonitor,
            CodexUsageMonitor quotaMonitor, Func<string[]> probe)
        {
            codexDataRoot = AgentHaloPaths.CodexDataRootDirectory();
            monitor = sessionMonitor;
            usageMonitor = quotaMonitor;
            processProbe = probe;
            processTimer = new Timer(PollProcess, null,
                Timeout.Infinite, Timeout.Infinite);
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
            lock (lifecycleGate)
            {
                if (disposed || active)
                {
                    return;
                }
                try
                {
                    active = true;
                    ApplyProcessProbeLocked(processProbe());
                    UpdateCollectionLocked();
                    processTimer.Change(collecting ? 1000 : 3000,
                        collecting ? 1000 : 3000);
                }
                catch
                {
                    active = false;
                    try { StopCollectionLocked(); }
                    catch { }
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (lifecycleGate)
            {
                if (disposed || !active) return;
                active = false;
                processTimer.Change(Timeout.Infinite, Timeout.Infinite);
                StopCollectionLocked();
            }
        }

        private static string[] ProbeCodexProcess()
        {
            string[] identities;
            return CodexRuntimeReader.TryReadProcessIdentities(out identities)
                ? identities : null;
        }

        private void PollProcess(object state)
        {
            bool changed = false;
            try
            {
                lock (lifecycleGate)
                {
                    if (!active || disposed) return;
                    string[] identities = processProbe();
                    if (identities == null) return;
                    changed = ApplyProcessProbeLocked(identities);
                    UpdateCollectionLocked();
                    if (changed)
                    {
                        processTimer.Change(codexRunning ? 1000 : 3000,
                            codexRunning ? 1000 : 3000);
                    }
                }
            }
            catch (Exception ex)
            {
                SettingsStorage.Log("Codex process monitoring failed: " + ex.Message);
            }
            if (changed) RaiseChanged();
        }

        private bool ApplyProcessProbeLocked(string[] identities)
        {
            if (identities == null) return false;
            bool running = identities.Length > 0;
            bool restarted = processStateKnown && codexRunning && running &&
                !processIdentities.Intersect(identities, StringComparer.Ordinal).Any();
            bool starting = running && (!processStateKnown || !codexRunning);
            bool changed = !processStateKnown || codexRunning != running || restarted;
            if (restarted)
            {
                monitor.SetCodexRunning(false, EarliestProcessStartUtc(identities));
                StopCollectionLocked();
            }
            else if (starting)
            {
                monitor.SetCodexRunning(true, EarliestProcessStartUtc(identities));
            }
            processStateKnown = true;
            processIdentities = identities;
            codexRunning = running;
            return changed;
        }

        private static DateTime EarliestProcessStartUtc(string[] identities)
        {
            DateTime earliest = DateTime.MaxValue;
            foreach (string identity in identities)
            {
                int separator = identity.IndexOf(':');
                long ticks;
                if (separator >= 0 && Int64.TryParse(identity.Substring(separator + 1),
                    out ticks) && ticks >= DateTime.MinValue.Ticks &&
                    ticks <= DateTime.MaxValue.Ticks)
                {
                    DateTime started = new DateTime(ticks, DateTimeKind.Utc);
                    if (started < earliest) earliest = started;
                }
            }
            return earliest == DateTime.MaxValue ? DateTime.UtcNow : earliest;
        }

        private void UpdateCollectionLocked()
        {
            if (processStateKnown) monitor.SetCodexRunning(codexRunning);
            if (processStateKnown && !codexRunning)
            {
                if (collecting) StopCollectionLocked();
                return;
            }
            if (collecting) return;
            monitor.Start();
            long activatedEpoch = usageMonitor.Activate();
            if (activatedEpoch == 0)
            {
                monitor.Stop();
                throw new ObjectDisposedException("CodexUsageMonitor");
            }
            Interlocked.Exchange(ref usageActivationEpoch, activatedEpoch);
            collecting = true;
        }

        private void StopCollectionLocked()
        {
            collecting = false;
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
            bool running = codexRunning;
            List<SessionSnapshot> sessions;
            AggregateSnapshot aggregate = monitor.ReadSnapshot(
                effectiveSettings, running, nowUtc, out sessions);
            if (!processStateKnown && !effectiveSettings.Paused)
            {
                aggregate.State = HaloState.Idle;
                aggregate.Label = "UNKNOWN";
                aggregate.Detail = L10n.Instance["status.unknown"];
                aggregate.Presence = AgentPresenceState.Standby;
                aggregate.TurnPhase = AgentTurnPhase.None;
                aggregate.Activity = AgentActivityKind.None;
                aggregate.AttentionReason = AgentAttentionReason.None;
                aggregate.FailureSeverity = AgentFailureSeverity.None;
                aggregate.EvidenceSource = AgentEvidenceSource.Process;
                aggregate.EvidenceKind = "process_unknown";
            }
            if (collecting && running && aggregate.Presence ==
                    AgentPresenceState.Standby)
            {
                string appFailure;
                DateTime appFailureUtc;
                bool hasAppFailure = CodexFailureReader.TryReadRecent(
                    codexDataRoot, out appFailure, out appFailureUtc);
                ApplyApplicationFailure(aggregate, effectiveSettings,
                    running, hasAppFailure, appFailure, appFailureUtc);
            }

            UsageMetrics usage;
            if (!usageMonitor.TryReadCached(out usage))
            {
                usage = null;
            }
            if (collecting)
            {
                lastCustomMetrics = CodexCustomApiMetricsReader.Read(sessions,
                    CodexProviderProfileReader.Read(Path.Combine(codexDataRoot,
                        "config.toml")), usageMonitor.Status);
            }

            return new AgentProviderSnapshot
            {
                Aggregate = aggregate,
                Sessions = sessions,
                Details = CreateDetailsSnapshot(usage, lastCustomMetrics),
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
            if (active && processStateKnown && !codexRunning) return false;
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
            if (collecting) RaiseChanged();
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
            processTimer.Dispose();
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
