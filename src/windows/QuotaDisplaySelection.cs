using System;

namespace CodexHalo
{
    public enum QuotaDisplayKind
    {
        None,
        FiveHour,
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
                switch (Kind)
                {
                    case QuotaDisplayKind.FiveHour:
                        return "quota.5h";
                    case QuotaDisplayKind.CurrentAvailable:
                        return "quota.current_available";
                    default:
                        return "quota.weekly";
                }
            }
        }
    }

    public sealed class QuotaDisplaySet
    {
        public QuotaDisplaySelection FiveHour;
        public QuotaDisplaySelection LongTerm;
    }

    public static class QuotaDisplaySelector
    {
        public static QuotaDisplaySet Select(UsageMetrics metrics)
        {
            return new QuotaDisplaySet
            {
                FiveHour = SelectFiveHour(metrics),
                LongTerm = SelectLongTerm(metrics)
            };
        }

        private static QuotaDisplaySelection SelectFiveHour(UsageMetrics metrics)
        {
            if (metrics != null && metrics.HasFiveHour)
            {
                return new QuotaDisplaySelection
                {
                    Kind = QuotaDisplayKind.FiveHour,
                    UsedPercent = metrics.FiveHourUsedPercent,
                    ResetUtc = metrics.FiveHourResetUtc
                };
            }

            return Empty();
        }

        private static QuotaDisplaySelection SelectLongTerm(UsageMetrics metrics)
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

            return Empty();
        }

        private static QuotaDisplaySelection Empty()
        {
            return new QuotaDisplaySelection
            {
                Kind = QuotaDisplayKind.None,
                UsedPercent = 0,
                ResetUtc = DateTime.MinValue
            };
        }
    }
}
