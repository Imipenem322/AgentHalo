using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexHalo
{
internal sealed class AgentMonitorCoordinator : IDisposable
    {
        private readonly AgentProviderCatalog catalog;
        private readonly Dictionary<AgentKind, IAgentProvider> providers =
            new Dictionary<AgentKind, IAgentProvider>();
        private readonly object gate = new object();
        private readonly object operationGate = new object();
        private readonly Action<string> log;
        private AgentKind focusedKind;
        private IAgentProvider provider;
        private IAgentProvider subscribedProvider;
        private EventHandler subscribedHandler;
        private long generation;
        private bool switching;
        private bool started;
        private bool disposed;
        private string lastSwitchError = String.Empty;

        public AgentMonitorCoordinator()
            : this(new AgentProviderCatalog(), AgentKind.Codex,
                SettingsStorage.Log)
        {
        }

        internal AgentMonitorCoordinator(AgentProviderCatalog providerCatalog,
            AgentKind initialFocus)
            : this(providerCatalog, initialFocus, SettingsStorage.Log)
        {
        }

        internal AgentMonitorCoordinator(AgentProviderCatalog providerCatalog,
            AgentKind initialFocus, Action<string> diagnosticLog)
        {
            if (providerCatalog == null)
            {
                throw new ArgumentNullException("providerCatalog");
            }
            catalog = providerCatalog;
            log = diagnosticLog ?? delegate { };
            focusedKind = catalog.IsRegistered(initialFocus)
                ? initialFocus : catalog.DefaultKind;
        }

        public event EventHandler<AgentCoordinatorChangedEventArgs> Changed;

        public AgentKind FocusedKind
        {
            get
            {
                lock (gate)
                {
                    return focusedKind;
                }
            }
        }

        public bool IsStarted
        {
            get
            {
                lock (gate)
                {
                    return started && !disposed;
                }
            }
        }

        public string LastSwitchError
        {
            get
            {
                lock (gate)
                {
                    return lastSwitchError;
                }
            }
        }

        public bool IsCurrentGeneration(AgentKind kind,
            long candidateGeneration)
        {
            lock (gate)
            {
                return !disposed && focusedKind == kind &&
                    generation == candidateGeneration;
            }
        }

        internal bool IsCurrent(AgentCoordinatorChangedEventArgs args)
        {
            return args != null && IsCurrentGeneration(args.Kind,
                args.Generation);
        }

        public void Start()
        {
            lock (operationGate)
            {
                IAgentProvider current;
                lock (gate)
                {
                    ThrowIfDisposed();
                    if (started)
                    {
                        return;
                    }
                    current = EnsureProviderLocked(focusedKind);
                    provider = current;
                    generation = NextGeneration(generation);
                    switching = true;
                }
                try
                {
                    current.Start();
                    lock (gate)
                    {
                        started = true;
                        switching = false;
                        SubscribeProviderLocked(current, generation);
                    }
                }
                catch
                {
                    lock (gate)
                    {
                        started = false;
                        switching = false;
                        generation = NextGeneration(generation);
                        UnsubscribeProviderLocked();
                    }
                    try
                    {
                        current.Stop();
                    }
                    catch
                    {
                    }
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (operationGate)
            {
                IAgentProvider current;
                lock (gate)
                {
                    if (disposed || !started)
                    {
                        return;
                    }
                    started = false;
                    switching = false;
                    generation = NextGeneration(generation);
                    UnsubscribeProviderLocked();
                    current = provider;
                }
                if (current != null)
                {
                    current.Stop();
                }
            }
        }

        public bool Refresh()
        {
            lock (operationGate)
            {
                IAgentProvider current = GetProvider();
                return current.Refresh();
            }
        }

        public AgentProviderSnapshot Read(HaloSettings settings,
            DateTime nowUtc)
        {
            lock (operationGate)
            {
                IAgentProvider current = GetProvider();
                return current.Read(settings, nowUtc);
            }
        }

        public bool TrySwitch(AgentKind targetKind, HaloSettings settings,
            Func<bool> persistFocus, out AgentProviderSnapshot snapshot)
        {
            return TrySwitch(targetKind, settings, DateTime.UtcNow,
                persistFocus, out snapshot);
        }

        internal bool TrySwitch(AgentKind targetKind, HaloSettings settings,
            out AgentProviderSnapshot snapshot)
        {
            return TrySwitch(targetKind, settings, DateTime.UtcNow, null,
                out snapshot);
        }

        internal bool TrySwitch(AgentKind targetKind, HaloSettings settings,
            DateTime nowUtc, out AgentProviderSnapshot snapshot)
        {
            return TrySwitch(targetKind, settings, nowUtc, null,
                out snapshot);
        }

        internal bool TrySwitch(AgentKind targetKind, HaloSettings settings,
            DateTime nowUtc, Func<bool> persistFocus,
            out AgentProviderSnapshot snapshot)
        {
            EventHandler<AgentCoordinatorChangedEventArgs> changedHandler;
            AgentCoordinatorChangedEventArgs changedArgs;
            bool result;
            lock (operationGate)
            {
                result = TrySwitchCore(targetKind, settings, nowUtc,
                    persistFocus, out snapshot, out changedHandler,
                    out changedArgs);
            }
            if (changedHandler != null && changedArgs != null)
            {
                try
                {
                    changedHandler(this, changedArgs);
                }
                catch (Exception ex)
                {
                    log("Agent focus listener failed: " + ex.Message);
                }
            }
            return result;
        }

        private bool TrySwitchCore(AgentKind targetKind, HaloSettings settings,
            DateTime nowUtc, Func<bool> persistFocus,
            out AgentProviderSnapshot snapshot,
            out EventHandler<AgentCoordinatorChangedEventArgs> changedHandler,
            out AgentCoordinatorChangedEventArgs changedArgs)
        {
            snapshot = null;
            changedHandler = null;
            changedArgs = null;
            if (settings == null)
            {
                SetSwitchError("Agent settings are required.");
                return false;
            }

            IAgentProvider previous = null;
            IAgentProvider candidate = null;
            AgentKind previousKind = focusedKind;
            string previousFocusKey = settings.FocusedAgent;
            string targetFocusKey = null;
            bool sameTarget = false;
            lock (gate)
            {
                ThrowIfDisposed();
                if (!started || switching)
                {
                    lastSwitchError = !started
                        ? "Agent switching is unavailable while monitoring is stopped."
                        : "Another agent switch is already in progress.";
                    return false;
                }
                if (!catalog.IsRegistered(targetKind) ||
                    !settings.IsAgentEnabled(targetKind, catalog))
                {
                    lastSwitchError =
                        "The requested agent provider is not enabled.";
                    return false;
                }
                if (targetKind == focusedKind)
                {
                    previous = provider ?? EnsureProviderLocked(focusedKind);
                    sameTarget = true;
                }
                else
                {
                    previous = provider ?? EnsureProviderLocked(focusedKind);
                    previousKind = focusedKind;
                    previousFocusKey = settings.FocusedAgent;
                    targetFocusKey = catalog.KeyFor(targetKind);
                    try
                    {
                        candidate = EnsureProviderLocked(targetKind);
                    }
                    catch (Exception ex)
                    {
                        lastSwitchError = SafeError(ex);
                        return false;
                    }
                    switching = true;
                    generation = NextGeneration(generation);
                    UnsubscribeProviderLocked();
                }
            }

            if (sameTarget)
            {
                try
                {
                    snapshot = previous.Read(settings, nowUtc);
                    SetSwitchError(String.Empty);
                    return snapshot != null;
                }
                catch (Exception ex)
                {
                    SetSwitchError(SafeError(ex));
                    return false;
                }
            }

            bool candidateStartAttempted = false;
            bool persisted = false;
            Exception failure = null;
            try
            {
                previous.Stop();

                candidateStartAttempted = true;
                candidate.Start();
                if (!candidate.Refresh())
                {
                    throw new InvalidOperationException(
                        "The target agent provider refresh failed.");
                }
                snapshot = candidate.Read(settings, nowUtc);
                ValidateCandidateSnapshot(targetKind, snapshot);

                settings.FocusedAgent = targetFocusKey;
                if (persistFocus != null && !persistFocus())
                {
                    throw new InvalidOperationException(
                        "The focused agent setting could not be persisted.");
                }
                persisted = true;

                lock (gate)
                {
                    provider = candidate;
                    focusedKind = targetKind;
                    switching = false;
                    lastSwitchError = String.Empty;
                    SubscribeProviderLocked(candidate, generation);
                    changedHandler = Changed;
                    changedArgs = new AgentCoordinatorChangedEventArgs(
                        targetKind, generation, true);
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            settings.FocusedAgent = previousFocusKey;
            Exception rollbackFailure = null;
            if (candidateStartAttempted)
            {
                try
                {
                    candidate.Stop();
                    candidateStartAttempted = false;
                }
                catch (Exception ex)
                {
                    rollbackFailure = ex;
                }
            }

            bool previousRestored = false;
            if (!candidateStartAttempted)
            {
                try
                {
                    previous.Start();
                    previousRestored = true;
                }
                catch (Exception ex)
                {
                    if (rollbackFailure == null)
                    {
                        rollbackFailure = ex;
                    }
                }
            }

            if (persisted && persistFocus != null)
            {
                try
                {
                    if (!persistFocus() && rollbackFailure == null)
                    {
                        rollbackFailure = new InvalidOperationException(
                            "The previous focused agent setting could not be restored.");
                    }
                }
                catch (Exception ex)
                {
                    if (rollbackFailure == null)
                    {
                        rollbackFailure = ex;
                    }
                }
            }

            lock (gate)
            {
                provider = previous;
                focusedKind = previousKind;
                switching = false;
                generation = NextGeneration(generation);
                started = previousRestored && !candidateStartAttempted;
                lastSwitchError = SafeError(failure);
                if (rollbackFailure != null)
                {
                    lastSwitchError += " Rollback: " +
                        SafeError(rollbackFailure);
                }
                if (started)
                {
                    SubscribeProviderLocked(previous, generation);
                }
            }
            snapshot = null;
            return false;
        }

        public bool IsForeground(IntPtr foregroundWindow)
        {
            lock (operationGate)
            {
                IAgentProvider current;
                lock (gate)
                {
                    if (disposed)
                    {
                        return false;
                    }
                    current = provider ?? EnsureProviderLocked(focusedKind);
                }
                return current.IsForeground(foregroundWindow);
            }
        }

        public bool TryActivateWindow()
        {
            lock (operationGate)
            {
                IAgentProvider current;
                lock (gate)
                {
                    if (disposed)
                    {
                        return false;
                    }
                    current = provider ?? EnsureProviderLocked(focusedKind);
                }
                return current.TryActivateWindow();
            }
        }

        private IAgentProvider GetProvider()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                provider = provider ?? EnsureProviderLocked(focusedKind);
                return provider;
            }
        }

        private IAgentProvider EnsureProviderLocked(AgentKind kind)
        {
            IAgentProvider existing;
            if (providers.TryGetValue(kind, out existing))
            {
                return existing;
            }
            IAgentProvider created = catalog.Create(kind);
            providers.Add(kind, created);
            return created;
        }

        private void SubscribeProviderLocked(IAgentProvider current,
            long slotGeneration)
        {
            UnsubscribeProviderLocked();
            if (current == null || !started)
            {
                return;
            }
            EventHandler handler = delegate(object sender, EventArgs e)
            {
                OnProviderChanged(current, slotGeneration, sender);
            };
            subscribedProvider = current;
            subscribedHandler = handler;
            current.Changed += handler;
        }

        private void UnsubscribeProviderLocked()
        {
            if (subscribedProvider != null && subscribedHandler != null)
            {
                subscribedProvider.Changed -= subscribedHandler;
            }
            subscribedProvider = null;
            subscribedHandler = null;
        }

        private void OnProviderChanged(IAgentProvider slotProvider,
            long slotGeneration, object sender)
        {
            EventHandler<AgentCoordinatorChangedEventArgs> handler;
            AgentKind kind;
            lock (gate)
            {
                if (disposed || !started || switching ||
                    sender != slotProvider || provider != slotProvider ||
                    subscribedProvider != slotProvider ||
                    generation != slotGeneration)
                {
                    return;
                }
                handler = Changed;
                kind = focusedKind;
            }
            if (handler != null)
            {
                try
                {
                    handler(this, new AgentCoordinatorChangedEventArgs(kind,
                        slotGeneration, false));
                }
                catch (Exception ex)
                {
                    log("Agent provider listener failed: " + ex.Message);
                }
            }
        }

        private static void ValidateCandidateSnapshot(AgentKind targetKind,
            AgentProviderSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Aggregate == null ||
                snapshot.Details == null)
            {
                throw new InvalidOperationException(
                    "The target agent provider returned an incomplete snapshot.");
            }
            if (snapshot.Aggregate.FocusedAgent != targetKind ||
                snapshot.Details.Agent != targetKind)
            {
                throw new InvalidOperationException(
                    "The target agent provider returned data for another agent.");
            }
        }

        private void SetSwitchError(string message)
        {
            lock (gate)
            {
                lastSwitchError = message ?? String.Empty;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("AgentMonitorCoordinator");
            }
        }

        private static long NextGeneration(long value)
        {
            return value == Int64.MaxValue ? 1 : value + 1;
        }

        private static string SafeError(Exception ex)
        {
            return ex == null ? "Unknown agent switch failure." :
                ex.GetType().Name + ": " + ex.Message;
        }

        public void Dispose()
        {
            List<IAgentProvider> allProviders;
            IAgentProvider activeProvider;
            bool stopActive;
            lock (operationGate)
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }
                    disposed = true;
                    stopActive = started;
                    started = false;
                    switching = false;
                    generation = NextGeneration(generation);
                    UnsubscribeProviderLocked();
                    activeProvider = provider;
                    provider = null;
                    allProviders = providers.Values.Distinct().ToList();
                    providers.Clear();
                    Changed = null;
                }
                if (stopActive && activeProvider != null)
                {
                    try
                    {
                        activeProvider.Stop();
                    }
                    catch (Exception ex)
                    {
                        log("Agent provider stop failed: " + ex.Message);
                    }
                }
                foreach (IAgentProvider item in allProviders)
                {
                    try
                    {
                        item.Dispose();
                    }
                    catch (Exception ex)
                    {
                        log("Agent provider dispose failed: " + ex.Message);
                    }
                }
            }
        }
    }
}
