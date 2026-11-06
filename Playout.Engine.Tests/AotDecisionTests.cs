using Xunit;

public sealed class AotDecisionTests
{
    private static readonly DateOnly PlaylistDate = new(2025, 5, 10);
    private static readonly TimeOnly AotTime = new(12, 0);
    private static readonly DateTimeOffset Due = new(2025, 5, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BeforeAotReturnsRemainingTime()
    {
        AotDecision result = Evaluate(Due.AddMinutes(-5), AotTransition.HardStop, true);

        Assert.Equal(AotAction.NotDue, result.Action);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Remaining);
        Assert.Equal(TimeSpan.Zero, result.LateBy);
        Assert.Equal(Due, result.ScheduledAtUtc);
    }

    [Theory]
    [InlineData((int)AotTransition.Crossfade, (int)AotAction.Crossfade)]
    [InlineData((int)AotTransition.HardStop, (int)AotAction.StopAndStart)]
    [InlineData((int)AotTransition.Wait, (int)AotAction.WaitForCurrent)]
    public void DueWithPlayingItemUsesTransition(int transitionValue, int expectedValue)
    {
        AotTransition transition = (AotTransition)transitionValue;
        AotAction expected = (AotAction)expectedValue;
        AotDecision result = Evaluate(Due, transition, true);

        Assert.Equal(expected, result.Action);
        Assert.Equal(TimeSpan.Zero, result.Remaining);
    }

    [Theory]
    [InlineData((int)AotTransition.Crossfade)]
    [InlineData((int)AotTransition.HardStop)]
    [InlineData((int)AotTransition.Wait)]
    public void DueWithoutPlayingItemStarts(int transitionValue)
    {
        AotTransition transition = (AotTransition)transitionValue;
        Assert.Equal(AotAction.Start, Evaluate(Due, transition, false).Action);
    }

    [Theory]
    [InlineData((int)LateAotPolicy.RequireDecision, (int)AotAction.LatePolicyRequired)]
    [InlineData((int)LateAotPolicy.Skip, (int)AotAction.Skip)]
    [InlineData((int)LateAotPolicy.Execute, (int)AotAction.StopAndStart)]
    public void LateAotUsesExplicitPolicy(int policyValue, int expectedValue)
    {
        LateAotPolicy policy = (LateAotPolicy)policyValue;
        AotAction expected = (AotAction)expectedValue;
        AotDecision result = Evaluate(Due.AddSeconds(6), AotTransition.HardStop, true, policy);

        Assert.Equal(expected, result.Action);
        Assert.Equal(TimeSpan.FromSeconds(6), result.LateBy);
    }

    [Fact]
    public void AtToleranceBoundaryAotStillExecutes()
    {
        Assert.Equal(AotAction.StopAndStart,
            Evaluate(Due.AddSeconds(5), AotTransition.HardStop, true).Action);
    }

    [Fact]
    public void WaitingForCurrentIgnoresLaterLatenessAndStartsWhenCurrentEnds()
    {
        AotDecision waiting = Evaluate(Due.AddMinutes(2), AotTransition.Wait, true,
            LateAotPolicy.RequireDecision, waitingForCurrentToFinish: true);
        AotDecision ended = Evaluate(Due.AddMinutes(3), AotTransition.Wait, false,
            LateAotPolicy.RequireDecision, waitingForCurrentToFinish: true);

        Assert.Equal(AotAction.WaitForCurrent, waiting.Action);
        Assert.Equal(AotAction.Start, ended.Action);
    }

    [Fact]
    public void RejectsWaitingStateForAnotherTransition()
    {
        Assert.Throws<ArgumentException>(() => Evaluate(Due, AotTransition.HardStop, true,
            waitingForCurrentToFinish: true));
    }

    [Fact]
    public void UsesPlaylistDateAndConfiguredTimeZone()
    {
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+02", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
        AotDecision result = AotDecisionLogic.Evaluate(
            PlaylistDate, AotTime, zone, Due.AddHours(-3), false,
            AotTransition.Wait, false, TimeSpan.Zero, LateAotPolicy.RequireDecision);

        Assert.Equal(Due.AddHours(-2), result.ScheduledAtUtc);
        Assert.Equal(TimeSpan.FromHours(1), result.Remaining);
    }

    [Theory]
    [InlineData(3, 31, 2, 30)]
    [InlineData(10, 27, 2, 30)]
    public void RejectsInvalidAndAmbiguousDaylightSavingTimes(int month, int day, int hour, int minute)
    {
        TimeZoneInfo zone = CreateDaylightSavingTimeZone();

        Assert.Throws<ArgumentException>(() => AotDecisionLogic.Evaluate(
            new DateOnly(2024, month, day), new TimeOnly(hour, minute), zone, Due, false,
            AotTransition.Wait, false, TimeSpan.Zero, LateAotPolicy.RequireDecision));
    }

    [Fact]
    public void RejectsNegativeLateTolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AotDecisionLogic.Evaluate(
            PlaylistDate, AotTime, TimeZoneInfo.Utc, Due, false, AotTransition.Wait, false,
            TimeSpan.FromTicks(-1), LateAotPolicy.RequireDecision));
    }

    private static AotDecision Evaluate(
        DateTimeOffset now,
        AotTransition transition,
        bool currentItemPlaying,
        LateAotPolicy policy = LateAotPolicy.RequireDecision,
        bool waitingForCurrentToFinish = false)
    {
        return AotDecisionLogic.Evaluate(
            PlaylistDate, AotTime, TimeZoneInfo.Utc, now, currentItemPlaying,
            transition, waitingForCurrentToFinish, TimeSpan.FromSeconds(5), policy);
    }

    private static TimeZoneInfo CreateDaylightSavingTimeZone()
    {
        TimeZoneInfo.TransitionTime start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 31);
        TimeZoneInfo.TransitionTime end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 3, 0, 0), 10, 27);
        TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2024, 1, 1), new DateTime(2024, 12, 31), TimeSpan.FromHours(1), start, end);

        return TimeZoneInfo.CreateCustomTimeZone(
            "Test DST", TimeSpan.FromHours(1), "Test DST", "Test standard", "Test daylight", [rule]);
    }
}
