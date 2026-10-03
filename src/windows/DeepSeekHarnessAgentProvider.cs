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
        internal const AgentCapability SupportedCapabilities =
            AgentCapability.Lifecycle | AgentCapability.ToolName |
            AgentCapability.Attention;

        private readonly object pollGate = new object();
        private readonly object notificationGate = new object();
        private readonly DeepSeekHarnessSnapshotReader snapshotReader;
        private Timer timer;
        private bool active;
        private bool disposed;
        private string selectedRootId = String.Empty;
        private HaloSettings lastSettings = new HaloSettings();
        private AgentProviderSnapshot lastSnapshot;
        private DeepSeekHarnessReadResult lastRead;

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
            bool startedNow = false;
            lock (pollGate)
            {
                ThrowIfDisposed();
                if (active)
                {
                    return;
                }
                active = true;
                timer = new Timer(OnTimer, null, 0, 1000);
                startedNow = true;
            }
            if (startedNow)
            {
                Poll(false);
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
                    if (timer != null)
                    {
                        timer.Dispose();
                        timer = null;
                    }
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
                if (settings != null)
                {
                    lastSettings = settings;
                }
                if (lastSettings.Paused)
                {
                    lastSnapshot = DeepSeekHarnessSnapshotReducer.BuildPaused(
                        lastSnapshot, lastRead);
                }
                else
                {
                    if (active)
                    {
                        lastRead = snapshotReader.Read(nowUtc);
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
                    Changed = null;
                    lastRead = null;
                    lastSnapshot = null;
                }
            }
        }

        private void OnTimer(object state)
        {
            Poll(true);
        }

        private void Poll(bool notify)
        {
            bool changed;
            lock (pollGate)
            {
                if (disposed || !active)
                {
                    return;
                }
                if (lastSettings.Paused)
                {
                    return;
                }
                AgentProviderSnapshot previous = lastSnapshot;
                lastRead = snapshotReader.Read(DateTime.UtcNow);
                lastSnapshot = BuildFromCache(lastSettings, DateTime.UtcNow);
                changed = DeepSeekHarnessSnapshotReducer.SnapshotChanged(
                    previous, lastSnapshot);
            }

            if (!notify || !changed)
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
