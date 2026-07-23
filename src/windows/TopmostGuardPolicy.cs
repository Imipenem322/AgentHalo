using System;

namespace CodexHalo
{
public static class TopmostGuardPolicy
    {
        public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

        public static bool IsDue(DateTime nowUtc, DateTime nextCheckUtc)
        {
            return nextCheckUtc == DateTime.MinValue || nowUtc >= nextCheckUtc;
        }

        public static DateTime NextCheckUtc(DateTime nowUtc)
        {
            return nowUtc.Add(CheckInterval);
        }

        public static bool ShouldRestore(
            bool alwaysOnTop,
            bool d3dFullScreenActive,
            bool nativeTopmost,
            bool foregroundChanged,
            bool force)
        {
            if (!alwaysOnTop || d3dFullScreenActive)
            {
                return false;
            }
            return force || foregroundChanged || !nativeTopmost;
        }
    }
}
