using System;

namespace Playout.Domain;

public enum AotBehavior
{
    Crossfade,
    HardStop,
    Wait
}

public enum AotAction
{
    NotDue,
    Start,
    CrossfadeAndStart,
    StopAndStart,
    WaitForCurrentTitle
}

public sealed record AotDecision(
    AotAction Action,
    TimeSpan Remaining);

public static class AotScheduler
{
    public static AotDecision Evaluate(
        DateTimeOffset scheduledTime,
        DateTimeOffset now,
        AotBehavior behavior,
        bool isTitlePlaying)
    {
        if (!Enum.IsDefined(behavior))
        {
            throw new ArgumentOutOfRangeException(nameof(behavior));
        }

        TimeSpan remaining = scheduledTime - now;

        if (remaining > TimeSpan.Zero)
        {
            return new AotDecision(AotAction.NotDue, remaining);
        }

        if (!isTitlePlaying)
        {
            return new AotDecision(AotAction.Start, remaining);
        }

        AotAction action = behavior switch
        {
            AotBehavior.Crossfade => AotAction.CrossfadeAndStart,
            AotBehavior.HardStop => AotAction.StopAndStart,
            AotBehavior.Wait => AotAction.WaitForCurrentTitle,
            _ => throw new ArgumentOutOfRangeException(nameof(behavior))
        };

        return new AotDecision(action, remaining);
    }
}