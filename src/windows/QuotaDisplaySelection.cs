using System;

namespace CodexHalo
{
    public enum QuotaDisplayKind
    {
        None,
        Weekly,
        CurrentAvailable
    }

    public sealed class QuotaDisplaySelection
    {
        public QuotaDisplayKind Kind;
        public double UsedPercent;
        public DateTime ResetUtc;

        public bool HasQuota
        {
            get { return Kind != QuotaDisplayKind.None; }
        }

        public string LabelKey
        {
            get
            {
                return Kind == QuotaDisplayKind.Weekly
                    ? "quota.weekly"
                    : "quota.current_available";
            }
        }
    }

    public static class QuotaDisplaySelector
    {
        public static QuotaDisplaySelection Select(UsageMetrics metrics)
        {
            if (metrics != null && metrics.HasWeekly)
            {
                return new QuotaDisplaySelection
                {
                    Kind = QuotaDisplayKind.Weekly,
                    UsedPercent = metrics.WeeklyUsedPercent,
                    ResetUtc = metrics.WeeklyResetUtc
                };
            }

            if (metrics != null && metrics.HasMonthly)
            {
                return new QuotaDisplaySelection
                {
                    Kind = QuotaDisplayKind.CurrentAvailable,
                    UsedPercent = metrics.MonthlyUsedPercent,
                    ResetUtc = metrics.MonthlyResetUtc
                };
            }

            return new QuotaDisplaySelection
            {
                Kind = QuotaDisplayKind.None,
                UsedPercent = 0,
                ResetUtc = DateTime.MinValue
            };
        }
    }
}
