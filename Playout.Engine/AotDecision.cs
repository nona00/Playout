internal enum AotTransition
{
    Crossfade,
    HardStop,
    Wait
}

internal enum LateAotPolicy
{
    RequireDecision,
    Execute,
    Skip
}

internal enum AotAction
{
    NotDue,
    Start,
    Crossfade,
    StopAndStart,
    WaitForCurrent,
    LatePolicyRequired,
    Skip
}

internal sealed record AotDecision(
    DateTimeOffset ScheduledAtUtc,
    TimeSpan Remaining,
    TimeSpan LateBy,
    AotAction Action);

internal static class AotDecisionLogic
{
    public static AotDecision Evaluate(
        DateOnly playlistDate,
        TimeOnly aotTime,
        TimeZoneInfo timeZone,
        DateTimeOffset now,
        bool currentItemPlaying,
        AotTransition transition,
        bool waitingForCurrentToFinish,
        TimeSpan lateTolerance,
        LateAotPolicy latePolicy)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        if (!Enum.IsDefined(transition))
        {
            throw new ArgumentOutOfRangeException(nameof(transition));
        }

        if (!Enum.IsDefined(latePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(latePolicy));
        }

        if (lateTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lateTolerance));
        }

        if (waitingForCurrentToFinish && transition != AotTransition.Wait)
        {
            throw new ArgumentException(
                "Der Wartezustand ist nur für den AOT-Modus Warten zulässig.",
                nameof(waitingForCurrentToFinish));
        }

        DateTime localTime = DateTime.SpecifyKind(
            playlistDate.ToDateTime(aotTime), DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(localTime))
        {
            throw new ArgumentException(
                "Die AOT-Uhrzeit existiert in der angegebenen Zeitzone nicht.", nameof(aotTime));
        }

        if (timeZone.IsAmbiguousTime(localTime))
        {
            throw new ArgumentException(
                "Die AOT-Uhrzeit ist in der angegebenen Zeitzone nicht eindeutig.", nameof(aotTime));
        }

        DateTimeOffset scheduledAtUtc = new DateTimeOffset(
            localTime, timeZone.GetUtcOffset(localTime)).ToUniversalTime();
        DateTimeOffset nowUtc = now.ToUniversalTime();

        if (nowUtc < scheduledAtUtc)
        {
            return new AotDecision(
                scheduledAtUtc, scheduledAtUtc - nowUtc, TimeSpan.Zero, AotAction.NotDue);
        }

        TimeSpan lateBy = nowUtc - scheduledAtUtc;
        if (!waitingForCurrentToFinish && lateBy > lateTolerance)
        {
            if (latePolicy == LateAotPolicy.RequireDecision)
            {
                return new AotDecision(
                    scheduledAtUtc, TimeSpan.Zero, lateBy, AotAction.LatePolicyRequired);
            }

            if (latePolicy == LateAotPolicy.Skip)
            {
                return new AotDecision(scheduledAtUtc, TimeSpan.Zero, lateBy, AotAction.Skip);
            }
        }

        AotAction action = !currentItemPlaying
            ? AotAction.Start
            : transition switch
            {
                AotTransition.Crossfade => AotAction.Crossfade,
                AotTransition.HardStop => AotAction.StopAndStart,
                AotTransition.Wait => AotAction.WaitForCurrent,
                _ => throw new ArgumentOutOfRangeException(nameof(transition))
            };

        return new AotDecision(scheduledAtUtc, TimeSpan.Zero, lateBy, action);
    }
}
