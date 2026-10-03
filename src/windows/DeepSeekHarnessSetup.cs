using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexHalo
{
internal sealed class DeepSeekHarnessSetup
    {
        internal const string PluginId = "agenthalo-deepseek-harness-observer";
        private readonly string profileDirectory;
        private readonly string observerPath;

        public bool Ready { get; private set; }
        public DateTime PreparedUtc { get; private set; }
        public string Error { get; private set; }

        public DeepSeekHarnessSetup()
            : this(DesktopProfileDirectory(), SettingsStorage.AppDirectory)
        {
        }

        internal DeepSeekHarnessSetup(string profile, string appDirectory)
        {
            profileDirectory = Path.GetFullPath(profile);
            observerPath = Path.Combine(Path.GetFullPath(appDirectory),
                "integrations", "deepseek-harness", "observer", "index.mjs");
        }

        private static string DesktopProfileDirectory()
        {
            string root = Environment.GetEnvironmentVariable("DSH_HOME");
            if (String.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".dsh");
            }
            else
            {
                root = root.Trim();
                if (root == "~" || root.StartsWith("~/") || root.StartsWith("~\\"))
                    root = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile),
                        root.Length == 1 ? String.Empty : root.Substring(2));
            }
            return Path.Combine(root, "profiles", "desktop");
        }

        public bool TryInstall()
        {
            if (Ready) return true;
            string previousError = Error;
            try
            {
                Error = null;
                // Desktop creates the profile under this lock before starting
                // its Host. Wait for that initialization instead of racing it.
                if (!ProfileReady()) return false;

                string patchPath = Path.Combine(profileDirectory,
                    "cordis.patch.yml");
                string original = File.ReadAllText(patchPath, Encoding.UTF8);
                string patched = RegisterObserver(original, observerPath);
                string observer;
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(
                        "CodexHalo.integrations.deepseek-harness.index.mjs"))
                {
                    if (stream == null)
                        throw new InvalidOperationException(
                            "The bundled DSH observer is missing.");
                    using (StreamReader reader = new StreamReader(stream,
                        Encoding.UTF8))
                        observer = reader.ReadToEnd();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(observerPath));
                WriteIfChanged(observerPath, observer);
                if (!ProfileReady()) return false;
                // Re-read after extraction so other profile changes survive.
                if (File.ReadAllText(patchPath, Encoding.UTF8) != original)
                    return false;
                WriteIfChanged(patchPath, patched);
                Ready = true;
                PreparedUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                if (Error != previousError)
                    SettingsStorage.Log("DeepSeek automatic setup failed: " + Error);
                return false;
            }
        }

        private bool ProfileReady()
        {
            string profileLock = Path.Combine(profileDirectory, "lock");
            return !File.Exists(profileLock) && !Directory.Exists(profileLock) &&
                File.Exists(Path.Combine(profileDirectory, "package.json")) &&
                File.Exists(Path.Combine(profileDirectory, "cordis.patch.yml")) &&
                File.Exists(Path.Combine(profileDirectory, "pnpm-workspace.yaml"));
        }

        internal static bool EnableMonitoring(HaloSettings settings)
        {
            if (settings.DeepSeekHarnessSetupComplete) return false;
            settings.NormalizeAgentSelection();
            if (!settings.IsAgentEnabled(AgentKind.DeepSeekHarness))
            {
                settings.EnabledAgents.Add("deepseek-harness");
                settings.FocusedAgent = "deepseek-harness";
            }
            settings.DeepSeekHarnessSetupComplete = true;
            return true;
        }

        internal static string RegisterObserver(string patch, string path)
        {
            string quotedPath = "'" + path.Replace('\\', '/').Replace("'", "''") + "'";
            string idPattern = @"(?m)^(?<indent>[ \t]*)(?<dash>-[ \t]+)?id:[ \t]*['""]?" +
                PluginId + @"['""]?[ \t]*(?:#[^\r\n]*)?\r?$";
            MatchCollection ids = Regex.Matches(patch, idPattern);
            if (ids.Count > 1)
                throw new InvalidDataException("Duplicate AgentHalo DSH entries in cordis.patch.yml.");
            if (ids.Count == 1)
            {
                Match id = ids[0];
                if (!id.Groups["dash"].Success)
                    throw new InvalidDataException("The AgentHalo DSH entry must be an insert item.");
                string indentation = id.Groups["indent"].Value + "  ";
                int end = patch.Length;
                foreach (Match line in Regex.Matches(patch.Substring(id.Index + id.Length),
                    @"(?m)^(?<indent>[ \t]*)(?<text>[^\s#][^\r\n]*)"))
                {
                    if (line.Groups["indent"].Length < indentation.Length)
                    {
                        end = id.Index + id.Length + line.Index;
                        break;
                    }
                }
                string entry = patch.Substring(id.Index, end - id.Index);
                Match name = Regex.Match(entry, @"(?m)^" + Regex.Escape(indentation) +
                    @"name:[ \t]*(?<value>'(?:[^']|'')*'|""(?:\\.|[^""\\])*""|[^#\r\n]*?)[ \t]*(?:#[^\r\n]*)?\r?$" );
                if (!name.Success)
                    throw new InvalidDataException("Cannot locate the AgentHalo DSH plugin path in cordis.patch.yml.");
                Group value = name.Groups["value"];
                int valueStart = id.Index + value.Index;
                return patch.Substring(0, valueStart) + quotedPath +
                    patch.Substring(valueStart + value.Length);
            }

            // DSH's empty profile template can contain comments followed by [].
            patch = Regex.Replace(patch, @"(?m)^[ \t]*\[\][ \t]*(?<comment>#[^\r\n]*)?\r?$",
                "${comment}");
            string newline = patch.Contains("\r\n") ? "\r\n" : "\n";
            if (patch.Length > 0 && !patch.EndsWith("\n")) patch += newline;
            return patch + "- insert:" + newline +
                "    - id: " + PluginId + newline +
                "      name: " + quotedPath + newline;
        }

        private static void WriteIfChanged(string path, string text)
        {
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text)
                return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
