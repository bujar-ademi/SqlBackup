namespace SqlBackup.Core;

public sealed record BackupSchedule(
    bool Enabled,
    string TimeZoneId,
    DateTime StartLocal,
    int FullIntervalMinutes,
    int DifferentialIntervalMinutes,
    int LogIntervalMinutes,
    DayOfWeek[] Weekdays,
    int[] MonthDays,
    TimeOnly? WindowStart = null,
    TimeOnly? WindowEnd = null,
    bool RunMissed = true);
