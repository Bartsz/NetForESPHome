using NetForESPHome.DomainModel.Native;

#pragma warning disable CS0612 // Type or member is obsolete

namespace NetForESPHome.Client;

/// <summary>Creates ESPHome time responses from the system time provider.</summary>
public static class EspHomeTimeResponseFactory
{
    /// <summary>Creates the current time response with matching POSIX and parsed time zone representations.</summary>
    public static GetTimeResponse Create(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var utcNow = timeProvider.GetUtcNow();
        var timeZone = timeProvider.LocalTimeZone;
        var localDate = TimeZoneInfo.ConvertTime(utcNow, timeZone).Date;
        var adjustmentRule = GetPosixAdjustmentRule(timeZone, localDate);

        var standardUtcOffset = adjustmentRule is null
            ? timeZone.GetUtcOffset(utcNow)
            : timeZone.BaseUtcOffset + adjustmentRule.BaseUtcOffsetDelta;

        var parsedTimezone = new ParsedTimezone
        {
            StdOffsetSeconds = checked(-(int)standardUtcOffset.TotalSeconds)
        };

        var response = new GetTimeResponse
        {
            EpochSeconds = checked((uint)utcNow.ToUnixTimeSeconds()),
            Timezone = $"STD{FormatPosixOffset(standardUtcOffset)}",
            ParsedTimezone = parsedTimezone
        };

        if (adjustmentRule is null || adjustmentRule.DaylightDelta == TimeSpan.Zero) return response;

        var daylightUtcOffset = standardUtcOffset + adjustmentRule.DaylightDelta;
        var daylightStart = CreateDstRule(adjustmentRule.DaylightTransitionStart);
        var daylightEnd = CreateDstRule(adjustmentRule.DaylightTransitionEnd);

        response.Timezone = string.Create(
            CultureInfo.InvariantCulture,
            $"STD{FormatPosixOffset(standardUtcOffset)}DST{FormatPosixOffset(daylightUtcOffset)},{FormatTransition(daylightStart)},{FormatTransition(daylightEnd)}");

        parsedTimezone.DstOffsetSeconds = checked(-(int)daylightUtcOffset.TotalSeconds);
        parsedTimezone.DstStart = daylightStart;
        parsedTimezone.DstEnd = daylightEnd;

        return response;
    }

    /// <summary>Selects the recurring rule suitable for describing future POSIX transitions.</summary>
    private static TimeZoneInfo.AdjustmentRule? GetPosixAdjustmentRule(TimeZoneInfo timeZone, DateTime localDate)
    {
        var adjustmentRules = timeZone.GetAdjustmentRules();
        if (adjustmentRules.Length == 0) return null;

        if (timeZone.HasIanaId)
        {
            var futureRule = adjustmentRules[^1];
            if (futureRule.DateEnd == DateTime.MaxValue.Date) return futureRule;
        }

        foreach (var adjustmentRule in adjustmentRules)
        {
            if (localDate < adjustmentRule.DateStart || localDate > adjustmentRule.DateEnd) continue;

            return adjustmentRule;
        }

        return null;
    }

    /// <summary>Converts a .NET DST transition into the representation expected by ESPHome.</summary>
    private static DSTRule CreateDstRule(TimeZoneInfo.TransitionTime transition)
    {
        var transitionTimeSeconds = checked((int)transition.TimeOfDay.TimeOfDay.TotalSeconds);

        if (!transition.IsFixedDateRule)
        {
            var recurringRule = new DSTRule
            {
                TimeSeconds = transitionTimeSeconds,
                Type = DSTRuleType.MonthWeekDay,
                Month = checked((uint)transition.Month),
                Week = checked((uint)transition.Week),
                DayOfWeek = checked((uint)transition.DayOfWeek)
            };

            return recurringRule;
        }

        var daysInMonth = DateTime.DaysInMonth(2001, transition.Month);

        // .NET treats February 29-31 as the last day of February.
        // J60 is always March 1, so subtracting one day preserves that behavior in leap and non-leap years.
        if (transition.Month == 2 && transition.Day > daysInMonth)
        {
            var lastDayOfFebruaryRule = new DSTRule
            {
                TimeSeconds = checked(transitionTimeSeconds - 24 * 60 * 60),
                Type = DSTRuleType.JulianNoLeap,
                Day = 60
            };

            return lastDayOfFebruaryRule;
        }

        // POSIX Jn ignores February 29, so a non-leap year maps fixed month/day values directly to Jn.
        var transitionDay = Math.Min(transition.Day, daysInMonth);
        var julianDay = new DateTime(2001, transition.Month, transitionDay).DayOfYear;

        var fixedRule = new DSTRule
        {
            TimeSeconds = transitionTimeSeconds,
            Type = DSTRuleType.JulianNoLeap,
            Day = checked((uint)julianDay)
        };

        return fixedRule;
    }

    /// <summary>Formats a UTC offset using the reversed sign required by POSIX time zones.</summary>
    private static string FormatPosixOffset(TimeSpan utcOffset)
    {
        var posixOffset = new DateTimeOffset(2000, 1, 1, 0, 0, 0, -utcOffset);
        var hours = posixOffset.ToString("%z", CultureInfo.InvariantCulture);
        var minutes = Math.Abs(posixOffset.Offset.Minutes);

        return minutes == 0 ? hours : $"{hours}:{minutes:00}";
    }

    /// <summary>Formats an ESPHome DST rule using POSIX transition syntax.</summary>
    private static string FormatTransition(DSTRule rule)
    {
        var transitionTime = FormatTransitionTime(rule.TimeSeconds);

        return rule.Type switch
        {
            DSTRuleType.MonthWeekDay => $"M{rule.Month}.{rule.Week}.{rule.DayOfWeek}/{transitionTime}",
            DSTRuleType.JulianNoLeap => $"J{rule.Day}/{transitionTime}",
            DSTRuleType.DayOfYear => $"{rule.Day}/{transitionTime}",
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Type, "Unsupported DST rule type.")
        };
    }

    /// <summary>Formats transition seconds using the signed POSIX transition-time syntax supported by ESPHome.</summary>
    private static string FormatTransitionTime(int timeSeconds)
    {
        var absoluteSeconds = Math.Abs((long)timeSeconds);
        var hours = absoluteSeconds / 3600;
        var remainingTime = TimeSpan.FromSeconds(absoluteSeconds % 3600);
        var sign = timeSeconds < 0 ? "-" : string.Empty;

        return string.Create(CultureInfo.InvariantCulture, $"{sign}{hours}:{remainingTime:mm\\:ss}");
    }
}