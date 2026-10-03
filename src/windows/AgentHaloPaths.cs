using System;
using System.IO;

namespace CodexHalo
{
internal static class AgentHaloPaths
    {
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
