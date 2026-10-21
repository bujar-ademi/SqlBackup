namespace SqlBackup.Core;

public static class ScheduleCalculator
{
    public static void Validate(BackupSchedule schedule)
    {
        if (schedule.StartLocal.Kind != DateTimeKind.Unspecified || schedule.StartLocal.Year is < 2000 or > 2100)
        {
            throw new ArgumentException("Use a local start date between 2000 and 2100 with an explicit schedule time zone.");
        }

        if (schedule.FullIntervalMinutes is < 1 or > 525600
            || schedule.DifferentialIntervalMinutes is < 0 or > 525600
            || schedule.LogIntervalMinutes is < 0 or > 525600)
        {
            throw new ArgumentException("Full needs a positive interval; enabled intervals must be between 1 minute and 365 days.");
        }

        if (schedule.Weekdays is null || schedule.Weekdays.Length == 0 || schedule.Weekdays.Any(day => !Enum.IsDefined(day))
            || schedule.MonthDays is null || schedule.MonthDays.Any(day => day is < 1 or > 31))
        {
            throw new ArgumentException("Select weekdays and valid optional days of the month (1–31).");
        }

        if (schedule.WindowStart.HasValue != schedule.WindowEnd.HasValue
            || (schedule.WindowStart.HasValue && schedule.WindowStart == schedule.WindowEnd))
        {
            throw new ArgumentException("Specify both ends of a non-empty time window, or leave the window disabled.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new ArgumentException("Select a valid Windows time zone.");
        }
    }

    public static ScheduledOccurrence? Next(BackupSchedule schedule, DateTimeOffset afterUtc)
    {
        Validate(schedule);
        return Enum.GetValues<BackupType>()
            .Select(type => Find(schedule, type, afterUtc, forward: true))
            .Where(item => item is not null)
            .OrderBy(item => item!.DueUtc).ThenBy(item => item!.Type).FirstOrDefault();
    }

    public static ScheduledOccurrence? Due(BackupSchedule schedule, DateTimeOffset afterUtc, DateTimeOffset nowUtc)
    {
        Validate(schedule);
        // Coalesce downtime into one run, preferring the recovery base over later dependent work.
        foreach (var type in Enum.GetValues<BackupType>())
        {
            var candidate = Find(schedule, type, nowUtc, forward: false);
            if (candidate is not null && candidate.DueUtc > afterUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    public static ScheduledOccurrence[] Preview(BackupSchedule schedule, DateTimeOffset afterUtc, int count = 12)
    {
        if (count is < 1 or > 100)
        {
            throw new ArgumentException("Preview count must be between 1 and 100.");
        }

        var result = new List<ScheduledOccurrence>();
        for (int index = 0; index < count; index++)
        {
            var next = Next(schedule, afterUtc);
            if (next is null)
            {
                break;
            }

            result.Add(next);
            afterUtc = next.DueUtc;
        }

        return result.ToArray();
    }

    private static ScheduledOccurrence? Find(BackupSchedule schedule, BackupType type, DateTimeOffset boundUtc, bool forward)
    {
        int interval = type switch
        {
            BackupType.Full => schedule.FullIntervalMinutes,
            BackupType.Differential => schedule.DifferentialIntervalMinutes,
            _ => schedule.LogIntervalMinutes
        };
        if (interval == 0)
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        DateTime localBound = TimeZoneInfo.ConvertTime(boundUtc, zone).DateTime;
        if (!forward)
        {
            while (zone.IsAmbiguousTime(localBound))
            {
                localBound = localBound.AddMinutes(1);
            }
        }

        long stepTicks = TimeSpan.FromMinutes(interval).Ticks;
        long index = DivideFloor(localBound.Ticks - schedule.StartLocal.Ticks, stepTicks);
        if (forward)
        {
            index = Math.Max(0, index);
        }

        for (int attempts = 0; attempts < 5000 && index >= 0; attempts++)
        {
            long ticks = schedule.StartLocal.Ticks + index * stepTicks;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                return null;
            }

            var local = new DateTime(ticks, DateTimeKind.Unspecified);
            if (Math.Abs((local - localBound).TotalDays) > 366 * 5)
            {
                return null;
            }

            bool calendarAllowed = schedule.Weekdays.Contains(local.DayOfWeek)
                && (schedule.MonthDays.Length == 0 || schedule.MonthDays.Contains(local.Day));
            if (!calendarAllowed)
            {
                var jump = forward ? local.Date.AddDays(1) : local.Date.AddTicks(-1);
                index = JumpIndex(schedule, jump, stepTicks, forward);
                continue;
            }

            if (!InWindow(schedule, TimeOnly.FromDateTime(local)))
            {
                var start = schedule.WindowStart!.Value;
                var end = schedule.WindowEnd!.Value;
                var time = TimeOnly.FromDateTime(local);
                DateTime jump;
                if (forward)
                {
                    jump = local.Date.Add(start.ToTimeSpan());
                    if (start < end && time >= end)
                    {
                        jump = jump.AddDays(1);
                    }
                }
                else
                {
                    jump = local.Date.Add(end.ToTimeSpan()).AddTicks(-1);
                    if (start < end && time < start)
                    {
                        jump = jump.AddDays(-1);
                    }
                }

                index = JumpIndex(schedule, jump, stepTicks, forward);
                continue;
            }

            if (!zone.IsInvalidTime(local))
            {
                TimeSpan offset = zone.IsAmbiguousTime(local)
                    ? zone.GetAmbiguousTimeOffsets(local).Max()
                    : zone.GetUtcOffset(local);
                var utc = new DateTimeOffset(local, offset).ToUniversalTime();
                if ((forward && utc > boundUtc) || (!forward && utc <= boundUtc))
                {
                    return new ScheduledOccurrence(type, utc);
                }
            }

            index += forward ? 1 : -1;
        }

        return null;
    }

    private static long JumpIndex(BackupSchedule schedule, DateTime local, long stepTicks, bool forward)
    {
        long delta = local.Ticks - schedule.StartLocal.Ticks;
        long result = DivideFloor(delta, stepTicks);
        return forward && delta % stepTicks != 0 ? result + 1 : result;
    }

    private static long DivideFloor(long dividend, long divisor)
    {
        long quotient = dividend / divisor;
        return dividend % divisor < 0 ? quotient - 1 : quotient;
    }

    private static bool InWindow(BackupSchedule schedule, TimeOnly time)
    {
        if (!schedule.WindowStart.HasValue)
        {
            return true;
        }

        var start = schedule.WindowStart.Value;
        var end = schedule.WindowEnd!.Value;
        return start < end ? time >= start && time < end : time >= start || time < end;
    }
}
