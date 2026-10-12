using System;
using System.IO;
using System.Threading;

namespace CodexHalo
{
internal static class AgentHaloPaths
    {
        private sealed class CodexPathSettings
        {
            public string DataRoot;
            public string[] AuthPaths;
        }

        private static readonly Lazy<CodexPathSettings> codexPathSettings =
            new Lazy<CodexPathSettings>(ResolveCodexPathSettings,
                LazyThreadSafetyMode.ExecutionAndPublication);

        public static string CodexDataRootDirectory()
        {
            return codexPathSettings.Value.DataRoot;
        }

        public static string[] CodexAuthPaths()
        {
            return (string[])codexPathSettings.Value.AuthPaths.Clone();
        }

        private static CodexPathSettings ResolveCodexPathSettings()
        {
            string configuredRoot = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!String.IsNullOrWhiteSpace(configuredRoot))
            {
                string root = Path.GetFullPath(configuredRoot.Trim());
                return new CodexPathSettings
                {
                    DataRoot = root,
                    AuthPaths = new[] { Path.Combine(root, "auth.json") }
                };
            }

            string userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            string defaultRoot = Path.GetFullPath(Path.Combine(userProfile,
                ".codex"));
            return new CodexPathSettings
            {
                DataRoot = defaultRoot,
                AuthPaths = new[]
                {
                    Path.Combine(userProfile, ".config", "codex", "auth.json"),
                    Path.Combine(defaultRoot, "auth.json")
                }
            };
        }

        public static string DeepSeekHarnessDesktopProfileDirectory()
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
                if (root == "~" || root.StartsWith("~/") ||
                    root.StartsWith("~\\"))
                {
                    root = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile),
                        root.Length == 1 ? String.Empty : root.Substring(2));
                }
            }
            return Path.GetFullPath(Path.Combine(root, "profiles",
                "desktop"));
        }

        public static bool IsUnderDirectory(string path, string directory)
        {
            if (String.IsNullOrWhiteSpace(path) ||
                String.IsNullOrWhiteSpace(directory))
            {
                return false;
            }
            string candidate = Path.GetFullPath(path);
            string parent = Path.GetFullPath(directory).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return candidate.StartsWith(parent,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
