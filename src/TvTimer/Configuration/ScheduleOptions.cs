namespace TvTimer.Configuration;

public class ScheduleOptions
{
    /// <summary>Standard 5-field cron expression (minute hour day-of-month month day-of-week).</summary>
    public string CronExpression { get; set; } = "30 22 * * 0-4";

    /// <summary>IANA time zone id the cron expression is evaluated against.</summary>
    public string TimeZoneId { get; set; } = "Etc/UTC";
}
