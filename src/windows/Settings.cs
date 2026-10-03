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
public sealed class HaloSettings
    {
        public bool HasPosition { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool Paused { get; set; }
        public string InstalledAt { get; set; }
        public Dictionary<string, string> Acknowledged { get; set; }
        public string AcknowledgedErrorAt { get; set; }
        public Dictionary<string, string> AcknowledgedErrors { get; set; }
        public int HaloScalePercent { get; set; }
        public string FocusedAgent { get; set; }
        public List<string> EnabledAgents { get; set; }
        public bool DeepSeekHarnessSetupComplete { get; set; }
        public string Language { get; set; }  // null = follow system

        public HaloSettings()
        {
            AlwaysOnTop = true;
            HaloScalePercent = 100;
            FocusedAgent = "codex";
            EnabledAgents = new List<string>
            {
                "codex"
            };
            InstalledAt = DateTime.UtcNow.ToString("o");
            Acknowledged = new Dictionary<string, string>();
            AcknowledgedErrors = new Dictionary<string, string>();
            Language = null;  // follow system by default
        }

        public DateTime GetInstalledUtc()
        {
            DateTime parsed;
            if (DateTime.TryParse(InstalledAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToUniversalTime();
            }
            return DateTime.UtcNow;
        }

        public DateTime GetAcknowledgedUtc(string threadId)
        {
            return GetAcknowledgedUtc(AgentKind.Codex, threadId);
        }

        public DateTime GetAcknowledgedUtc(AgentKind agent, string threadId)
        {
            if (String.IsNullOrWhiteSpace(threadId))
            {
                return DateTime.MinValue;
            }
            string value;
            DateTime parsed;
            string scopedKey = ScopedAcknowledgementKey(agent, threadId);
            if (Acknowledged != null &&
                Acknowledged.TryGetValue(scopedKey, out value) &&
                DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToUniversalTime();
            }
            if (agent == AgentKind.Codex && Acknowledged != null &&
                Acknowledged.TryGetValue(threadId, out value) &&
                DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToUniversalTime();
            }
            return DateTime.MinValue;
        }

        public void Acknowledge(string threadId, DateTime completedUtc)
        {
            Acknowledge(AgentKind.Codex, threadId, completedUtc);
        }

        public void Acknowledge(AgentKind agent, string threadId,
            DateTime completedUtc)
        {
            if (String.IsNullOrWhiteSpace(threadId))
            {
                return;
            }
            if (Acknowledged == null)
            {
                Acknowledged = new Dictionary<string, string>();
            }
            Acknowledged[ScopedAcknowledgementKey(agent, threadId)] =
                completedUtc.ToUniversalTime().ToString("o");
        }

        public DateTime GetAcknowledgedErrorUtc()
        {
            return GetAcknowledgedErrorUtc(AgentKind.Codex);
        }

        public DateTime GetAcknowledgedErrorUtc(AgentKind agent)
        {
            string value;
            DateTime parsed;
            if (AcknowledgedErrors != null &&
                AcknowledgedErrors.TryGetValue(AgentKey(agent), out value) &&
                DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToUniversalTime();
            }
            if (agent != AgentKind.Codex)
            {
                return DateTime.MinValue;
            }
            return DateTime.TryParse(AcknowledgedErrorAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime() : DateTime.MinValue;
        }

        public void AcknowledgeError(AgentKind agent, DateTime errorUtc)
        {
            if (AcknowledgedErrors == null)
            {
                AcknowledgedErrors = new Dictionary<string, string>();
            }
            string value = errorUtc.ToUniversalTime().ToString("o");
            AcknowledgedErrors[AgentKey(agent)] = value;
            if (agent == AgentKind.Codex)
            {
                AcknowledgedErrorAt = value;
            }
        }

        public bool NormalizeAgentSelection()
        {
            return NormalizeAgentSelection(new AgentProviderCatalog());
        }

        internal bool NormalizeAgentSelection(AgentProviderCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            List<string> normalized = new List<string>();
            if (EnabledAgents == null)
            {
                normalized.AddRange(catalog.DefaultEnabledKeys);
            }
            else
            {
                HashSet<AgentKind> selected = new HashSet<AgentKind>();
                foreach (string key in EnabledAgents)
                {
                    AgentProviderDescriptor descriptor = catalog.Find(key);
                    if (descriptor != null)
                    {
                        selected.Add(descriptor.Kind);
                    }
                }
                foreach (AgentProviderDescriptor descriptor in
                    catalog.Descriptors)
                {
                    if (selected.Contains(descriptor.Kind))
                    {
                        normalized.Add(descriptor.Key);
                    }
                }
            }
            if (normalized.Count == 0)
            {
                string fallbackKey = catalog.KeyFor(catalog.DefaultKind);
                if (!String.IsNullOrWhiteSpace(fallbackKey))
                {
                    normalized.Add(fallbackKey);
                }
            }

            AgentProviderDescriptor focused = catalog.Find(FocusedAgent);
            // A retired selection should keep an already-enabled provider
            // instead of enabling an extra provider during migration.
            if (focused == null && String.Equals(
                    (FocusedAgent ?? String.Empty).Trim(), "antigravity",
                    StringComparison.OrdinalIgnoreCase))
            {
                focused = catalog.Find(normalized[0]);
            }
            if (focused == null)
            {
                AgentProviderDescriptor preferred = catalog.Find(
                    catalog.DefaultKind);
                if (preferred != null)
                {
                    focused = preferred;
                    if (!ContainsKey(normalized, preferred.Key))
                    {
                        normalized.Add(preferred.Key);
                        normalized = catalog.Descriptors.Where(
                            delegate(AgentProviderDescriptor descriptor)
                            {
                                return ContainsKey(normalized,
                                    descriptor.Key);
                            }).Select(
                            delegate(AgentProviderDescriptor descriptor)
                            {
                                return descriptor.Key;
                            }).ToList();
                    }
                }
            }
            else if (!ContainsKey(normalized, focused.Key))
            {
                focused = catalog.Find(normalized[0]);
            }

            bool repaired = !SameCanonicalList(EnabledAgents, normalized);
            if (repaired)
            {
                EnabledAgents = normalized;
            }
            string normalizedFocus = focused == null
                ? catalog.KeyFor(catalog.DefaultKind) : focused.Key;
            if (!String.Equals(FocusedAgent, normalizedFocus,
                    StringComparison.Ordinal))
            {
                FocusedAgent = normalizedFocus;
                repaired = true;
            }
            return repaired;
        }

        public bool TryGetFocusedAgent(out AgentKind kind)
        {
            AgentProviderCatalog catalog = new AgentProviderCatalog();
            AgentProviderDescriptor descriptor = catalog.Find(FocusedAgent);
            if (descriptor != null && IsAgentEnabled(descriptor.Kind,
                    catalog))
            {
                kind = descriptor.Kind;
                return true;
            }
            kind = catalog.DefaultKind;
            return false;
        }

        public bool IsAgentEnabled(AgentKind kind)
        {
            return IsAgentEnabled(kind, new AgentProviderCatalog());
        }

        internal bool IsAgentEnabled(AgentKind kind,
            AgentProviderCatalog catalog)
        {
            if (catalog == null || EnabledAgents == null)
            {
                return false;
            }
            string key = catalog.KeyFor(kind);
            return !String.IsNullOrWhiteSpace(key) &&
                ContainsKey(EnabledAgents, key);
        }

        private static bool SameCanonicalList(IList<string> current,
            IList<string> normalized)
        {
            if (current == null || current.Count != normalized.Count)
            {
                return false;
            }
            for (int i = 0; i < current.Count; i++)
            {
                if (!String.Equals(current[i], normalized[i],
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ContainsKey(IEnumerable<string> keys, string key)
        {
            return keys != null && keys.Any(delegate(string candidate)
            {
                return String.Equals(candidate, key,
                    StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string ScopedAcknowledgementKey(AgentKind agent,
            string id)
        {
            return AgentKey(agent) + ":" + id;
        }

        private static string AgentKey(AgentKind agent)
        {
            if (agent == AgentKind.DeepSeekHarness)
            {
                return "deepseek-harness";
            }
            return "codex";
        }
    }

public static class SettingsStorage
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

    public static string AppDirectory
        {
            get
            {
                string diagnostic = Environment.GetEnvironmentVariable(
                    "AGENTHALO_DIAGNOSTIC_APP_DIRECTORY");
                bool diagnosticMode = String.Equals(
                    Environment.GetEnvironmentVariable(
                        "AGENTHALO_TEST_MODE"), "1",
                    StringComparison.Ordinal);
                if (diagnosticMode && !String.IsNullOrWhiteSpace(diagnostic))
                {
                    try
                    {
                        string full = Path.GetFullPath(diagnostic);
                        string temp = Path.GetFullPath(Path.GetTempPath())
                            .TrimEnd(Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar);
                        if (AgentHaloPaths.IsUnderDirectory(full, temp))
                        {
                            return full;
                        }
                    }
                    catch
                    {
                    }
                }
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(root, "CodexHalo");
            }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(AppDirectory, "settings.json"); }
        }

        public static HaloSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    HaloSettings result = Serializer.Deserialize<HaloSettings>(
                        File.ReadAllText(SettingsPath, Encoding.UTF8));
                    if (result != null)
                    {
                        bool repaired = false;
                        if (result.Acknowledged == null)
                        {
                            result.Acknowledged = new Dictionary<string, string>();
                            repaired = true;
                        }
                        if (result.AcknowledgedErrors == null)
                        {
                            result.AcknowledgedErrors =
                                new Dictionary<string, string>();
                            repaired = true;
                        }
                        if (String.IsNullOrEmpty(result.InstalledAt))
                        {
                            result.InstalledAt = DateTime.UtcNow.ToString("o");
                            repaired = true;
                        }
                        if (result.Paused)
                        {
                            // Pause is a temporary runtime control. Persisting it
                            // makes the next launch look like a broken monitor.
                            result.Paused = false;
                            repaired = true;
                        }
                        if (!HaloWindow.IsValidScalePercent(result.HaloScalePercent))
                        {
                            result.HaloScalePercent = 100;
                            repaired = true;
                        }
                        if (result.NormalizeAgentSelection())
                        {
                            repaired = true;
                        }
                        if (repaired)
                        {
                            Save(result);
                        }
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Settings load failed: " + ex.Message);
            }
            return new HaloSettings();
        }

        public static void Save(HaloSettings settings)
        {
            try
            {
                SaveAtomicOrThrow(settings);
            }
            catch (Exception ex)
            {
                Log("Settings save failed: " + ex.Message);
            }
        }

        internal static void SaveAtomicOrThrow(HaloSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            Directory.CreateDirectory(AppDirectory);
            string destination = SettingsPath;
            string temp = destination + "." +
                Guid.NewGuid().ToString("N") + ".tmp";
            bool runtimePaused = settings.Paused;
            settings.Paused = false;
            string json;
            try
            {
                json = Serializer.Serialize(settings);
            }
            finally
            {
                settings.Paused = runtimePaused;
            }

            try
            {
                File.WriteAllText(temp, json, Encoding.UTF8);
                if (File.Exists(destination))
                {
                    try
                    {
                        File.Replace(temp, destination, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceOnSameVolume(temp, destination);
                    }
                    catch (IOException)
                    {
                        if (File.Exists(destination))
                        {
                            ReplaceOnSameVolume(temp, destination);
                        }
                        else
                        {
                            File.Move(temp, destination);
                        }
                    }
                }
                else
                {
                    try
                    {
                        File.Move(temp, destination);
                    }
                    catch (IOException)
                    {
                        if (!File.Exists(destination))
                        {
                            throw;
                        }
                        ReplaceOnSameVolume(temp, destination);
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                }
            }
        }

        private static void ReplaceOnSameVolume(string source,
            string destination)
        {
            const int replaceExisting = 0x1;
            const int writeThrough = 0x8;
            if (!MoveFileEx(source, destination,
                    replaceExisting | writeThrough))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Atomic settings replacement failed.");
            }
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(AppDirectory);
                File.AppendAllText(Path.Combine(AppDirectory, "halo.log"),
                    DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
            }
            catch
            {
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName,
            string newFileName, int flags);
    }
}
