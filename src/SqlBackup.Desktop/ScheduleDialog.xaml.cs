using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class ScheduleDialog : Window
{
    public BackupSchedule? SelectedSchedule { get; private set; }

    public ScheduleDialog(BackupSchedule? schedule)
    {
        InitializeComponent();
        TimeZone.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            WeekdayPanel.Children.Add(new CheckBox { Content = day.ToString()[..3], Tag = day, Margin = new Thickness(0, 0, 10, 8) });
        }

        Populate(schedule ?? DefaultSchedule());
        RefreshPreview();
    }

    private static BackupSchedule DefaultSchedule()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        DateTime tomorrow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date.AddDays(1).AddHours(2);
        return new BackupSchedule(false, zone.Id, tomorrow, 1440, 0, 0,
            Enum.GetValues<DayOfWeek>(), [], RunMissed: true);
    }

    private void Populate(BackupSchedule schedule)
    {
        ScheduleEnabled.IsChecked = schedule.Enabled;
        StartDate.SelectedDate = schedule.StartLocal.Date;
        StartTime.Text = schedule.StartLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        TimeZone.SelectedValue = schedule.TimeZoneId;
        FullHours.Text = (schedule.FullIntervalMinutes / 60).ToString(CultureInfo.InvariantCulture);
        FullMinutes.Text = (schedule.FullIntervalMinutes % 60).ToString(CultureInfo.InvariantCulture);
        DifferentialEnabled.IsChecked = schedule.DifferentialIntervalMinutes > 0;
        DifferentialHours.Text = (schedule.DifferentialIntervalMinutes / 60).ToString(CultureInfo.InvariantCulture);
        DifferentialMinutes.Text = (schedule.DifferentialIntervalMinutes % 60).ToString(CultureInfo.InvariantCulture);
        LogEnabled.IsChecked = schedule.LogIntervalMinutes > 0;
        LogHours.Text = (schedule.LogIntervalMinutes / 60).ToString(CultureInfo.InvariantCulture);
        LogMinutes.Text = (schedule.LogIntervalMinutes % 60).ToString(CultureInfo.InvariantCulture);
        foreach (CheckBox checkbox in WeekdayPanel.Children)
        {
            checkbox.IsChecked = schedule.Weekdays.Contains((DayOfWeek)checkbox.Tag);
        }

        MonthDaysEnabled.IsChecked = schedule.MonthDays.Length > 0;
        MonthDays.Text = string.Join(",", schedule.MonthDays);
        WindowEnabled.IsChecked = schedule.WindowStart.HasValue;
        WindowStart.Text = schedule.WindowStart?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "00:00";
        WindowEnd.Text = schedule.WindowEnd?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "23:59";
        RunMissed.IsChecked = schedule.RunMissed;
    }

    public BackupSchedule ReadSchedule()
    {
        if (StartDate.SelectedDate is not { } date || TimeZone.SelectedValue is not string zone)
        {
            throw new ArgumentException("Choose a start date and time zone.");
        }

        int[] monthDays = [];
        if (MonthDaysEnabled.IsChecked == true)
        {
            var values = new List<int>();
            foreach (string part in MonthDays.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, out int value))
                {
                    throw new ArgumentException("Use comma-separated month days such as 1,15,28.");
                }

                values.Add(value);
            }

            if (values.Count == 0)
            {
                throw new ArgumentException("Enter at least one month day or disable the filter.");
            }

            monthDays = values.Distinct().ToArray();
        }

        var schedule = new BackupSchedule(ScheduleEnabled.IsChecked == true, zone,
            DateTime.SpecifyKind(date.Date.Add(ParseTime(StartTime.Text).ToTimeSpan()), DateTimeKind.Unspecified),
            Interval(FullHours.Text, FullMinutes.Text),
            DifferentialEnabled.IsChecked == true ? Interval(DifferentialHours.Text, DifferentialMinutes.Text) : 0,
            LogEnabled.IsChecked == true ? Interval(LogHours.Text, LogMinutes.Text) : 0,
            WeekdayPanel.Children.Cast<CheckBox>().Where(item => item.IsChecked == true).Select(item => (DayOfWeek)item.Tag).ToArray(),
            monthDays,
            WindowEnabled.IsChecked == true ? ParseTime(WindowStart.Text) : null,
            WindowEnabled.IsChecked == true ? ParseTime(WindowEnd.Text) : null,
            RunMissed.IsChecked == true);
        ScheduleCalculator.Validate(schedule);
        return schedule;
    }

    private static int Interval(string hours, string minutes)
    {
        if (!int.TryParse(hours, out int hour) || !int.TryParse(minutes, out int minute)
            || hour is < 0 or > 8760 || minute is < 0 or > 59 || hour + minute == 0)
        {
            throw new ArgumentException("Use nonnegative hours and minutes (0–59) with a positive total interval.");
        }

        return checked(hour * 60 + minute);
    }

    private static TimeOnly ParseTime(string value)
    {
        if (!TimeOnly.TryParseExact(value, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new ArgumentException("Use 24-hour times in HH:mm format.");
        }

        return time;
    }

    public bool RefreshPreview()
    {
        try
        {
            var schedule = ReadSchedule();
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            PreviewList.ItemsSource = ScheduleCalculator.Preview(schedule, DateTimeOffset.UtcNow)
                .Select(item => $"{TimeZoneInfo.ConvertTime(item.DueUtc, zone):yyyy-MM-dd HH:mm zzz}   {item.Type}").ToArray();
            Status.Text = "Preview uses the same calculation as the service. Save Job to persist this schedule.";
            return true;
        }
        catch (ArgumentException exception)
        {
            Status.Text = exception.Message;
            PreviewList.ItemsSource = null;
            return false;
        }
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        RefreshPreview();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        Populate(DefaultSchedule() with
        {
            Enabled = true,
            FullIntervalMinutes = 10080,
            DifferentialIntervalMinutes = 1440,
            LogIntervalMinutes = 15
        });
        RefreshPreview();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (RefreshPreview())
        {
            SelectedSchedule = ReadSchedule();
            DialogResult = true;
        }
    }
}
