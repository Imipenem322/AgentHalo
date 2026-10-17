using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexHalo
{
internal sealed class DeepSeekHarnessAgentProvider : IAgentProvider
    {
        private static readonly TimeSpan OfflineProbeInterval =
            TimeSpan.FromSeconds(3);
        internal const AgentCapability SupportedCapabilities =
            AgentCapability.Lifecycle | AgentCapability.ToolName |
            AgentCapability.Attention;

        private readonly object pollGate = new object();
        private readonly object notificationGate = new object();
        private readonly DeepSeekHarnessSnapshotReader snapshotReader;
        private Timer pollTimer;
        private Timer processProbeTimer;
        private bool active;
        private bool disposed;
        private bool offline;
        private bool awaitingSnapshotAfterRestart;
        private bool lastReadPaused;
        private string selectedRootId = String.Empty;
        private HaloSettings lastSettings = new HaloSettings();
        private AgentProviderSnapshot lastSnapshot;
        private DeepSeekHarnessReadResult lastRead;
        private DeepSeekHarnessReadResult offlineRead;

        public DeepSeekHarnessAgentProvider()
            : this(new DeepSeekHarnessSnapshotReader())
        {
        }

        internal DeepSeekHarnessAgentProvider(
            DeepSeekHarnessSnapshotReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }
            snapshotReader = reader;
        }

        public AgentKind Kind
        {
            get { return AgentKind.DeepSeekHarness; }
        }

        public AgentCapability Capabilities
        {
            get
            {
                lock (pollGate)
                {
                    return lastSnapshot == null
                        ? AgentCapability.None
                        : lastSnapshot.Capabilities;
                }
            }
        }

        public event EventHandler Changed;

        public void Start()
        {
            bool shouldPoll;
            lock (pollGate)
            {
                ThrowIfDisposed();
                if (active)
                {
                    return;
                }
                lastReadPaused = lastSettings.Paused;
                active = true;
                shouldPoll = !offline;
            }
            try
            {
                if (shouldPoll)
                {
                    Poll(false);
                }
            }
            finally
            {
                lock (pollGate)
                {
                    EnsureTimersLocked();
                }
            }
        }

        public void Stop()
        {
            lock (notificationGate)
            {
                lock (pollGate)
                {
                    if (!active)
                    {
                        return;
                    }
                    active = false;
                    EnsureTimersLocked();
                }
            }
        }

        public bool Refresh()
        {
            lock (pollGate)
            {
                if (!active || disposed)
                {
                    return false;
                }
                if (offline)
                {
                    return true;
                }
            }
            Poll(true);
            return true;
        }

        public AgentProviderSnapshot Read(HaloSettings settings,
            DateTime nowUtc)
        {
            lock (pollGate)
            {
                ThrowIfDisposed();
                bool wasPaused = lastReadPaused;
                if (settings != null)
                {
                    lastSettings = settings;
                }
                bool isPaused = lastSettings.Paused;
                lastReadPaused = isPaused;
                if (isPaused)
                {
                    lastSnapshot = DeepSeekHarnessSnapshotReducer.BuildPaused(
                        lastSnapshot, lastRead);
                }
                else
                {
                    if (active && wasPaused)
                    {
                        Poll(false, nowUtc);
                    }
                    lastSnapshot = BuildFromCache(lastSettings, nowUtc);
                }
                return lastSnapshot;
            }
        }

        public bool IsForeground(IntPtr foregroundWindow)
        {
            if (foregroundWindow == IntPtr.Zero)
            {
                return false;
            }

            uint ownerProcessId;
            if (GetWindowThreadProcessId(foregroundWindow,
                    out ownerProcessId) == 0 || ownerProcessId == 0)
            {
                return false;
            }

            List<DeepSeekHarnessInstanceSnapshot> instances;
            lock (pollGate)
            {
                instances = lastRead == null
                    ? new List<DeepSeekHarnessInstanceSnapshot>()
                    : new List<DeepSeekHarnessInstanceSnapshot>(
                        lastRead.Instances);
            }
            foreach (DeepSeekHarnessInstanceSnapshot instance in instances)
            {
                DeepSeekHarnessSnapshotDocument document = instance.Document;
                if (document.hostParentPid == ownerProcessId &&
                    IsSameVerifiedProcess((int)ownerProcessId,
                        document.hostExecutable, instance.ParentStartUtc))
                {
                    return true;
                }
            }
            return false;
        }

        public bool TryActivateWindow()
        {
            return false;
        }

        public void Dispose()
        {
            Stop();
            lock (notificationGate)
            {
                lock (pollGate)
                {
                    if (disposed)
                    {
                        return;
                    }
                    disposed = true;
                    EnsureTimersLocked();
                    Changed = null;
                    lastRead = null;
                    lastSnapshot = null;
                    offlineRead = null;
                }
            }
        }

        private void OnTimer(object state)
        {
            Poll(true);
        }

        private void OnProcessProbe(object state)
        {
            bool resumeSnapshotPolling = false;
            bool changed = false;
            lock (pollGate)
            {
                if (disposed || !active || (!offline &&
                        !awaitingSnapshotAfterRestart) ||
                    lastSettings.Paused)
                {
                    return;
                }

                DeepSeekHarnessProcessProbeResult result =
                    snapshotReader.ProbeForRestart();
                if (offline && result ==
                    DeepSeekHarnessProcessProbeResult.Found)
                {
                    offline = false;
                    awaitingSnapshotAfterRestart = true;
                    offlineRead = null;
                    resumeSnapshotPolling = true;
                }
                else if (offline && result ==
                    DeepSeekHarnessProcessProbeResult.Unknown)
                {
                    AgentProviderSnapshot previous = lastSnapshot;
                    lastRead = CopyReadResult(offlineRead);
                    if (lastRead != null)
                    {
                        lastRead.Broken = true;
                        lastRead.IntegrationState =
                            AgentIntegrationState.Broken;
                        lastRead.StatusDetailKey =
                            "status.deepseek.unknown";
                    }
                    lastSnapshot = BuildFromCache(lastSettings,
                        DateTime.UtcNow);
                    changed = DeepSeekHarnessSnapshotReducer.SnapshotChanged(
                        previous, lastSnapshot);
                }
                else if (offline)
                {
                    lastRead = offlineRead;
                    AgentProviderSnapshot previous = lastSnapshot;
                    lastSnapshot = BuildFromCache(lastSettings,
                        DateTime.UtcNow);
                    changed = DeepSeekHarnessSnapshotReducer.SnapshotChanged(
                        previous, lastSnapshot);
                }
                else if (awaitingSnapshotAfterRestart && result ==
                    DeepSeekHarnessProcessProbeResult.NotFound)
                {
                    awaitingSnapshotAfterRestart = false;
                    if (snapshotReader.CanStopSnapshotPolling(lastRead))
                    {
                        EnterOfflineLocked();
                    }
                    else
                    {
                        EnsureTimersLocked();
                    }
                }
            }

            if (resumeSnapshotPolling)
            {
                try
                {
                    Poll(true);
                }
                finally
                {
                    lock (pollGate)
                    {
                        EnsureTimersLocked();
                    }
                }
                return;
            }
            NotifyChanged(changed);
        }

        private void Poll(bool notify)
        {
            Poll(notify, DateTime.UtcNow);
        }

        private void Poll(bool notify, DateTime nowUtc)
        {
            bool changed;
            lock (pollGate)
            {
                if (disposed || !active)
                {
                    return;
                }
                if (offline)
                {
                    return;
                }
                if (lastSettings.Paused)
                {
                    return;
                }
                lastReadPaused = false;
                AgentProviderSnapshot previous = lastSnapshot;
                lastRead = snapshotReader.Read(nowUtc);
                if (awaitingSnapshotAfterRestart &&
                    lastRead.Instances.Count > 0)
                {
                    awaitingSnapshotAfterRestart = false;
                    EnsureTimersLocked();
                }
                if (!awaitingSnapshotAfterRestart &&
                    snapshotReader.CanStopSnapshotPolling(lastRead))
                {
                    DeepSeekHarnessProcessProbeResult probeResult =
                        snapshotReader.ProbeForRestart();
                    if (probeResult ==
                        DeepSeekHarnessProcessProbeResult.NotFound)
                    {
                        EnterOfflineLocked();
                    }
                    else
                    {
                        // Keep normal snapshot polling until this known DSH
                        // process pair either publishes a validated snapshot
                        // or the low-frequency probe confirms it is gone.
                        awaitingSnapshotAfterRestart = true;
                        EnsureTimersLocked();
                    }
                }
                lastSnapshot = BuildFromCache(lastSettings, nowUtc);
                changed = DeepSeekHarnessSnapshotReducer.SnapshotChanged(
                    previous, lastSnapshot);
            }

            if (!notify)
            {
                return;
            }
            NotifyChanged(changed);
        }

        private void NotifyChanged(bool changed)
        {
            if (!changed)
            {
                return;
            }
            lock (notificationGate)
            {
                EventHandler handler;
                lock (pollGate)
                {
                    if (disposed || !active)
                    {
                        return;
                    }
                    handler = Changed;
                }
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        private void EnterOfflineLocked()
        {
            offline = true;
            offlineRead = CopyReadResult(lastRead);
            EnsureTimersLocked();
        }

        private void EnsureTimersLocked()
        {
            bool running = active && !disposed;
            if (running && !offline)
            {
                if (pollTimer == null)
                {
                    pollTimer = new Timer(OnTimer, null, 1000, 1000);
                }
            }
            else if (pollTimer != null)
            {
                pollTimer.Dispose();
                pollTimer = null;
            }

            if (running && (offline || awaitingSnapshotAfterRestart))
            {
                if (processProbeTimer == null)
                {
                    int interval = (int)OfflineProbeInterval.TotalMilliseconds;
                    processProbeTimer = new Timer(OnProcessProbe, null,
                        interval, interval);
                }
            }
            else if (processProbeTimer != null)
            {
                processProbeTimer.Dispose();
                processProbeTimer = null;
            }
        }

        private static DeepSeekHarnessReadResult CopyReadResult(
            DeepSeekHarnessReadResult source)
        {
            if (source == null)
            {
                return null;
            }
            DeepSeekHarnessReadResult copy = new DeepSeekHarnessReadResult
            {
                IntegrationState = source.IntegrationState,
                StatusDetailKey = source.StatusDetailKey,
                LastEventUtc = source.LastEventUtc,
                FilesPresent = source.FilesPresent,
                HadStoppedInstance = source.HadStoppedInstance,
                Unsupported = source.Unsupported,
                Broken = source.Broken,
                UnverifiedStoppedInstance =
                    source.UnverifiedStoppedInstance,
                ConfirmedExitedInstances = source.ConfirmedExitedInstances
            };
            copy.Instances.AddRange(source.Instances);
            foreach (string instanceId in
                source.ConfirmedExitedInstanceIds)
            {
                copy.ConfirmedExitedInstanceIds.Add(instanceId);
            }
            return copy;
        }

        private AgentProviderSnapshot BuildFromCache(HaloSettings settings,
            DateTime nowUtc)
        {
            string nextRootId;
            AgentProviderSnapshot snapshot =
                DeepSeekHarnessSnapshotReducer.Build(lastRead, settings,
                    nowUtc, selectedRootId, out nextRootId);
            selectedRootId = nextRootId;
            return snapshot;
        }

        private static bool IsSameVerifiedProcess(int processId,
            string expectedExecutable, DateTime expectedStartUtc)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    DateTime startUtc = process.StartTime.ToUniversalTime();
                    string executable = Path.GetFullPath(
                        process.MainModule.FileName);
                    string expected = Path.GetFullPath(expectedExecutable);
                    return Math.Abs((startUtc - expectedStartUtc)
                            .TotalSeconds) <= 2 &&
                        String.Equals(executable, expected,
                            StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    "DeepSeekHarnessAgentProvider");
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window, out uint processId);
    }
}
