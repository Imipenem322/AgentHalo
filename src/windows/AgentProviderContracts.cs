using System;
using System.Collections.Generic;

namespace CodexHalo
{
[Flags]
internal enum AgentCapability
    {
        None = 0,
        Lifecycle = 1,
        ToolName = 2,
        Attention = 4,
        Usage = 8,
        ContextWindow = 16,
        WindowActivation = 32
    }

internal enum AgentDetailsMode
    {
        Quota = 0,
        Information = 1,
        DeepSeekTask = 3
    }

internal enum AgentIntegrationState
    {
        NotRequired = 0,
        NotConfigured = 1,
        Healthy = 3,
        Stale = 4,
        Broken = 5
    }

internal sealed class AgentIntegrationHealth
    {
        public AgentIntegrationState State;
        public string DetailKey;
        public DateTime LastEventUtc;

        public AgentIntegrationHealth()
        {
            State = AgentIntegrationState.NotRequired;
            DetailKey = String.Empty;
            LastEventUtc = DateTime.MinValue;
        }
    }

internal sealed class AgentDetailsSnapshot
    {
        public AgentKind Agent;
        public AgentDetailsMode Mode;
        public UsageMetrics Usage;
        public string ProjectName;
        public string TaskTitle;
        public string TaskTitleKey;
        public string ModelNameKey;
        public string ModelSourceKey;
        public string StatusDetailKey;
        public string ModelName;
        public string ProviderName;
        public long InputTokens;
        public long OutputTokens;
        public long ContextInputTokens;
        public long ContextWindowTokens;

        public bool HasProject
        {
            get { return !String.IsNullOrWhiteSpace(ProjectName); }
        }

        public bool HasModel
        {
            get { return !String.IsNullOrWhiteSpace(ModelName); }
        }

        public bool HasTokenUsage
        {
            get { return InputTokens > 0 || OutputTokens > 0; }
        }

        public bool HasContext
        {
            get { return ContextInputTokens >= 0 && ContextWindowTokens > 0; }
        }

        public double ContextUsedPercent
        {
            get
            {
                if (!HasContext)
                {
                    return 0;
                }
                return Math.Max(0, Math.Min(100,
                    ContextInputTokens * 100.0 / ContextWindowTokens));
            }
        }
    }

internal sealed class AgentProviderSnapshot
    {
        public AggregateSnapshot Aggregate;
        public List<SessionSnapshot> Sessions;
        public AgentDetailsSnapshot Details;
        public AgentCapability Capabilities;
        public AgentIntegrationHealth Integration;

        public AgentProviderSnapshot()
        {
            Integration = new AgentIntegrationHealth();
        }
    }

internal sealed class AgentCoordinatorChangedEventArgs : EventArgs
    {
        public AgentCoordinatorChangedEventArgs(AgentKind kind,
            long generation, bool focusChanged)
        {
            Kind = kind;
            Generation = generation;
            FocusChanged = focusChanged;
        }

        public AgentKind Kind { get; private set; }
        public long Generation { get; private set; }
        public bool FocusChanged { get; private set; }
    }

internal interface IAgentProvider : IDisposable
    {
        AgentKind Kind { get; }
        AgentCapability Capabilities { get; }
        event EventHandler Changed;

        void Start();
        void Stop();
        bool Refresh();
        AgentProviderSnapshot Read(HaloSettings settings, DateTime nowUtc);
        bool IsForeground(IntPtr foregroundWindow);
        bool TryActivateWindow();
    }
}
