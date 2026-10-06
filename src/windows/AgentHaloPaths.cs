using System;
using System.IO;

namespace CodexHalo
{
internal static class AgentHaloPaths
    {
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
