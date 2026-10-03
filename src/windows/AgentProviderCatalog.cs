using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexHalo
{
internal sealed class AgentProviderDescriptor
    {
        public AgentKind Kind;
        public string Key;
        public string DisplayName;
        public string DisplayNameKey;
        public int Order;
        public bool EnabledByDefault;
        public AgentCapability Capabilities;
        public Func<IAgentProvider> CreateProvider;
    }

internal sealed class AgentProviderCatalog
    {
        private readonly List<AgentProviderDescriptor> descriptors;

        public AgentProviderCatalog()
            : this(new[]
            {
                new AgentProviderDescriptor
                {
                    Kind = AgentKind.Codex,
                    Key = "codex",
                    DisplayName = "Codex",
                    DisplayNameKey = "agent.codex",
                    Order = 0,
                    EnabledByDefault = true,
                    Capabilities = CodexAgentProvider.SupportedCapabilities,
                    CreateProvider = delegate { return new CodexAgentProvider(); }
                },
                new AgentProviderDescriptor
                {
                    Kind = AgentKind.DeepSeekHarness,
                    Key = "deepseek-harness",
                    DisplayName = "DeepSeek Harness",
                    DisplayNameKey = "agent.deepseek-harness",
                    Order = 1,
                    EnabledByDefault = false,
                    Capabilities = DeepSeekHarnessAgentProvider.SupportedCapabilities,
                    CreateProvider = delegate
                    {
                        return new DeepSeekHarnessAgentProvider();
                    }
                }
            })
        {
        }

        internal AgentProviderCatalog(
            IEnumerable<AgentProviderDescriptor> providerDescriptors)
        {
            descriptors = (providerDescriptors ??
                Enumerable.Empty<AgentProviderDescriptor>())
                .Where(delegate(AgentProviderDescriptor descriptor)
                {
                    return descriptor != null &&
                        !String.IsNullOrWhiteSpace(descriptor.Key) &&
                        descriptor.CreateProvider != null;
                })
                .OrderBy(delegate(AgentProviderDescriptor descriptor)
                {
                    return descriptor.Order;
                })
                .ToList();
            if (descriptors.Count == 0)
            {
                throw new InvalidOperationException(
                    "At least one agent provider must be registered.");
            }
            foreach (AgentProviderDescriptor descriptor in descriptors)
            {
                descriptor.Key = descriptor.Key.Trim();
            }
            if (descriptors.GroupBy(
                    delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor.Kind;
                    }).Any(delegate(IGrouping<AgentKind, AgentProviderDescriptor> group)
                    {
                        return group.Count() > 1;
                    }) ||
                descriptors.GroupBy(
                    delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor.Key;
                    }, StringComparer.OrdinalIgnoreCase)
                    .Any(delegate(IGrouping<string, AgentProviderDescriptor> group)
                    {
                        return group.Count() > 1;
                    }))
            {
                throw new InvalidOperationException(
                    "Agent provider kinds and keys must be unique.");
            }
        }

        public IList<AgentProviderDescriptor> Descriptors
        {
            get { return descriptors.AsReadOnly(); }
        }

        public AgentKind DefaultKind
        {
            get
            {
                AgentProviderDescriptor codex = Find(AgentKind.Codex);
                if (codex != null)
                {
                    return codex.Kind;
                }
                AgentProviderDescriptor enabled = descriptors.FirstOrDefault(
                    delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor.EnabledByDefault;
                    });
                return (enabled ?? descriptors[0]).Kind;
            }
        }

        public IList<string> DefaultEnabledKeys
        {
            get
            {
                List<string> keys = descriptors.Where(
                    delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor.EnabledByDefault;
                    }).Select(delegate(AgentProviderDescriptor descriptor)
                    {
                        return descriptor.Key;
                    }).ToList();
                if (keys.Count == 0)
                {
                    keys.Add(KeyFor(DefaultKind));
                }
                return keys.AsReadOnly();
            }
        }

        public AgentProviderDescriptor Find(AgentKind kind)
        {
            return descriptors.FirstOrDefault(
                delegate(AgentProviderDescriptor descriptor)
                {
                    return descriptor.Kind == kind;
                });
        }

        public AgentProviderDescriptor Find(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                return null;
            }
            string candidate = key.Trim();
            return descriptors.FirstOrDefault(
                delegate(AgentProviderDescriptor descriptor)
                {
                    return String.Equals(descriptor.Key, candidate,
                        StringComparison.OrdinalIgnoreCase);
                });
        }

        public bool TryResolve(string key,
            out AgentProviderDescriptor descriptor)
        {
            descriptor = Find(key);
            return descriptor != null;
        }

        public bool TryParse(string key, out AgentKind kind)
        {
            AgentProviderDescriptor descriptor = Find(key);
            if (descriptor == null)
            {
                kind = DefaultKind;
                return false;
            }
            kind = descriptor.Kind;
            return true;
        }

        public AgentKind ParseOrDefault(string key)
        {
            AgentProviderDescriptor descriptor = Find(key);
            return descriptor == null ? DefaultKind : descriptor.Kind;
        }

        public bool IsRegistered(AgentKind kind)
        {
            return Find(kind) != null;
        }

        public string KeyFor(AgentKind kind)
        {
            AgentProviderDescriptor descriptor = Find(kind);
            return descriptor == null ? null : descriptor.Key;
        }

        public IAgentProvider Create(AgentKind kind)
        {
            AgentProviderDescriptor descriptor = Find(kind);
            if (descriptor == null)
            {
                throw new InvalidOperationException(
                    "The requested agent provider is not registered.");
            }
            IAgentProvider provider = descriptor.CreateProvider();
            if (provider == null)
            {
                throw new InvalidOperationException(
                    "The agent provider factory returned no provider.");
            }
            if (provider.Kind != kind)
            {
                try
                {
                    provider.Dispose();
                }
                catch
                {
                }
                throw new InvalidOperationException(
                    "The agent provider factory returned the wrong provider kind.");
            }
            return provider;
        }
    }
}
