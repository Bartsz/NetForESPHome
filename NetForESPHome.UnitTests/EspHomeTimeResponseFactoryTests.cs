using NetForESPHome.Client;

namespace NetForESPHome.UnitTests;

/// <summary>Verifies ESPHome time responses generated from .NET time zone information.</summary>
public sealed class EspHomeTimeResponseFactoryTests
{
    /// <summary>Provides fixed UTC offsets covering common POSIX representations and edge cases.</summary>
    public static TheoryData<TimeSpan, string, int> FixedOffsetCases => new()
    {
        { TimeSpan.Zero, "STD+0", 0 },
        { TimeSpan.FromHours(1), "STD-1", -3_600 },
        { TimeSpan.FromHours(-5), "STD+5", 18_000 },
        { new TimeSpan(5, 30, 0), "STD-5:30", -19_800 },
        { new TimeSpan(5, 45, 0), "STD-5:45", -20_700 },
        { new TimeSpan(-3, -30, 0), "STD+3:30", 12_600 },
        { TimeSpan.FromHours(7), "STD-7", -25_200 },
        { TimeSpan.FromHours(14), "STD-14", -50_400 },
        { TimeSpan.FromHours(-14), "STD+14", 50_400 }
    };

    /// <summary>Converts fixed UTC offsets to matching POSIX and parsed ESPHome representations.</summary>
    [Theory]
    [MemberData(nameof(FixedOffsetCases))]
    public void Create_TimeZoneWithoutDst_ReturnsExpectedOffset(TimeSpan utcOffset, string expectedTimezone, int expectedStandardOffsetSeconds)
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test", utcOffset, "Test", "Test");
        var utcNow = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal((uint)utcNow.ToUnixTimeSeconds(), response.EpochSeconds);
        Assert.Equal(expectedTimezone, response.Timezone);
        Assert.NotNull(response.ParsedTimezone);
        Assert.Equal(expectedStandardOffsetSeconds, response.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(0, response.ParsedTimezone.DstOffsetSeconds);
    }

    /// <summary>Converts recurring DST transitions to the ESPHome month-week-day representation.</summary>
    [Fact]
    public void Create_TimeZoneWithDst_ReturnsExpectedRules()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var adjustmentRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(1), "Test", "STD", "DST", [adjustmentRule]);
        var utcNow = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal("STD-1DST-2,M3.5.0/2:00:00,M10.5.0/3:00:00", response.Timezone);
        Assert.Equal(-3_600, response.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(-7_200, response.ParsedTimezone.DstOffsetSeconds);

        Assert.Equal(DSTRuleType.MonthWeekDay, response.ParsedTimezone.DstStart.Type);
        Assert.Equal((uint)3, response.ParsedTimezone.DstStart.Month);
        Assert.Equal((uint)5, response.ParsedTimezone.DstStart.Week);
        Assert.Equal((uint)DayOfWeek.Sunday, response.ParsedTimezone.DstStart.DayOfWeek);
        Assert.Equal(7_200, response.ParsedTimezone.DstStart.TimeSeconds);

        Assert.Equal(DSTRuleType.MonthWeekDay, response.ParsedTimezone.DstEnd.Type);
        Assert.Equal((uint)10, response.ParsedTimezone.DstEnd.Month);
        Assert.Equal((uint)5, response.ParsedTimezone.DstEnd.Week);
        Assert.Equal((uint)DayOfWeek.Sunday, response.ParsedTimezone.DstEnd.DayOfWeek);
        Assert.Equal(10_800, response.ParsedTimezone.DstEnd.TimeSeconds);
    }

    /// <summary>Converts ordinary fixed-date DST transitions to Julian-no-leap rules.</summary>
    [Fact]
    public void Create_FixedDateDst_ReturnsJulianNoLeapRules()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 27);
        var adjustmentRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-5), "Test", "STD", "DST", [adjustmentRule]);
        var utcNow = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal("STD+5DST+4,J60/2:00:00,J300/2:00:00", response.Timezone);

        Assert.Equal(DSTRuleType.JulianNoLeap, response.ParsedTimezone.DstStart.Type);
        Assert.Equal((uint)60, response.ParsedTimezone.DstStart.Day);
        Assert.Equal(7_200, response.ParsedTimezone.DstStart.TimeSeconds);

        Assert.Equal(DSTRuleType.JulianNoLeap, response.ParsedTimezone.DstEnd.Type);
        Assert.Equal((uint)300, response.ParsedTimezone.DstEnd.Day);
        Assert.Equal(7_200, response.ParsedTimezone.DstEnd.TimeSeconds);
    }

    /// <summary>Preserves .NET's last-day-of-month behavior when a fixed transition day exceeds the month length.</summary>
    [Fact]
    public void Create_FixedDateBeyondMonthLength_UsesLastDayOfMonth()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 4, 31);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 31);
        var adjustmentRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(1), "Test", "STD", "DST", [adjustmentRule]);
        var utcNow = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal("STD-1DST-2,J120/2:00:00,J304/2:00:00", response.Timezone);
        Assert.Equal((uint)120, response.ParsedTimezone.DstStart.Day);
        Assert.Equal((uint)304, response.ParsedTimezone.DstEnd.Day);
    }

    /// <summary>Preserves .NET's last-day-of-February rule in both leap and non-leap years.</summary>
    [Fact]
    public void Create_LastDayOfFebruary_ReturnsEquivalentJulianRule()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 2, 29);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 27);
        var adjustmentRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(1), "Test", "STD", "DST", [adjustmentRule]);
        var utcNow = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal("STD-1DST-2,J60/-22:00:00,J300/2:00:00", response.Timezone);

        Assert.Equal(DSTRuleType.JulianNoLeap, response.ParsedTimezone.DstStart.Type);
        Assert.Equal((uint)60, response.ParsedTimezone.DstStart.Day);
        Assert.Equal(-79_200, response.ParsedTimezone.DstStart.TimeSeconds);
    }

    /// <summary>Includes BaseUtcOffsetDelta in both standard and daylight offsets.</summary>
    [Fact]
    public void Create_AdjustmentRuleWithBaseUtcOffsetDelta_ReturnsAdjustedOffsets()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var adjustmentRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd, TimeSpan.FromMinutes(30));
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(1), "Test", "STD", "DST", [adjustmentRule]);
        var utcNow = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal("STD-1:30DST-2:30,M3.5.0/2:00:00,M10.5.0/3:00:00", response.Timezone);
        Assert.Equal(-5_400, response.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(-9_000, response.ParsedTimezone.DstOffsetSeconds);
    }

    /// <summary>Uses the adjustment rule applicable to the time provided by TimeProvider for custom time zones.</summary>
    [Fact]
    public void Create_MultipleAdjustmentRules_UsesRuleForCurrentDate()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var oldRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2025, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        var currentRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(2), daylightStart, daylightEnd);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(1), "Test", "STD", "DST", [oldRule, currentRule]);
        var utcNow = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal("STD-1DST-3,M3.5.0/2:00:00,M10.5.0/3:00:00", response.Timezone);
        Assert.Equal(-3_600, response.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(-10_800, response.ParsedTimezone.DstOffsetSeconds);
    }

    /// <summary>Uses the recurring TZif rule for an IANA time zone instead of a single Unix transition interval.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Create_IanaTimeZone_ReturnsRecurringPosixRule()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var winterProvider = new TestTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), timeZone);
        var summerProvider = new TestTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), timeZone);

        var winterResponse = EspHomeTimeResponseFactory.Create(winterProvider);
        var summerResponse = EspHomeTimeResponseFactory.Create(summerProvider);

        const string expectedTimezone = "STD-1DST-2,M3.5.0/2:00:00,M10.5.0/3:00:00";

        Assert.Equal(expectedTimezone, winterResponse.Timezone);
        Assert.Equal(expectedTimezone, summerResponse.Timezone);

        Assert.Equal(-3_600, winterResponse.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(-7_200, winterResponse.ParsedTimezone.DstOffsetSeconds);
        Assert.Equal(-3_600, summerResponse.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(-7_200, summerResponse.ParsedTimezone.DstOffsetSeconds);
    }

    /// <summary>Provides deterministic time and time zone values for tests.</summary>
    internal sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        /// <summary>Initializes the provider with deterministic time and time zone values.</summary>
        public TestTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone)
        {
            _utcNow = utcNow;
            LocalTimeZone = localTimeZone;
        }

        /// <summary>Gets the time zone used by the deterministic provider.</summary>
        public override TimeZoneInfo LocalTimeZone { get; }

        /// <summary>Gets the deterministic UTC time.</summary>
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    /// <summary>Returns the same recurring time zone definition around the spring DST transition.</summary>
    [Theory]
    [InlineData("2026-03-29T00:59:59+00:00")]
    [InlineData("2026-03-29T01:00:00+00:00")]
    [InlineData("2026-03-29T01:00:01+00:00")]
    public void Create_AroundSpringDstTransition_ReturnsSameTimezoneDefinition(string utcTimestamp)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var utcNow = DateTimeOffset.Parse(utcTimestamp, CultureInfo.InvariantCulture);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal((uint)utcNow.ToUnixTimeSeconds(), response.EpochSeconds);
        Assert.Equal("STD-1DST-2,M3.5.0/2:00:00,M10.5.0/3:00:00", response.Timezone);
        Assert.Equal(-3_600, response.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(-7_200, response.ParsedTimezone.DstOffsetSeconds);
    }

    /// <summary>Returns the same recurring time zone definition around the autumn DST transition.</summary>
    [Theory]
    [InlineData("2026-10-25T00:59:59+00:00")]
    [InlineData("2026-10-25T01:00:00+00:00")]
    [InlineData("2026-10-25T01:00:01+00:00")]
    public void Create_AroundAutumnDstTransition_ReturnsSameTimezoneDefinition(string utcTimestamp)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var utcNow = DateTimeOffset.Parse(utcTimestamp, CultureInfo.InvariantCulture);
        var timeProvider = new TestTimeProvider(utcNow, timeZone);

        var response = EspHomeTimeResponseFactory.Create(timeProvider);

        Assert.Equal((uint)utcNow.ToUnixTimeSeconds(), response.EpochSeconds);
        Assert.Equal("STD-1DST-2,M3.5.0/2:00:00,M10.5.0/3:00:00", response.Timezone);
        Assert.Equal(-3_600, response.ParsedTimezone.StdOffsetSeconds);
        Assert.Equal(-7_200, response.ParsedTimezone.DstOffsetSeconds);
    }
}