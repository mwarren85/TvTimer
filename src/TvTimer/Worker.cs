using Cronos;
using Microsoft.Extensions.Options;
using TvTimer.Configuration;

namespace TvTimer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly SonyBraviaClient _tvClient;
    private readonly ScheduleOptions _schedule;
    private readonly CronExpression _cron;
    private readonly TimeZoneInfo _timeZone;

    public Worker(ILogger<Worker> logger, SonyBraviaClient tvClient, IOptions<ScheduleOptions> scheduleOptions)
    {
        _logger = logger;
        _tvClient = tvClient;
        _schedule = scheduleOptions.Value;
        _cron = CronExpression.Parse(_schedule.CronExpression);
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_schedule.TimeZoneId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _tvClient.TryLoadAuthCookie();

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var nextUtc = _cron.GetNextOccurrence(nowUtc, _timeZone);

            if (nextUtc is null)
            {
                _logger.LogWarning(
                    "Cron expression '{Cron}' has no future occurrences. Worker is stopping.",
                    _schedule.CronExpression);
                return;
            }

            var delay = nextUtc.Value - nowUtc;
            _logger.LogInformation(
                "Next TV power-off scheduled for {NextLocal} ({TimeZone}), in {Delay}.",
                TimeZoneInfo.ConvertTimeFromUtc(nextUtc.Value, _timeZone),
                _schedule.TimeZoneId,
                delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _tvClient.PowerOffAsync(stoppingToken);
                _logger.LogInformation("TV power-off command sent successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send TV power-off command.");
            }

            // Sleep past the fire minute so we don't recompute and trigger again immediately.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(61), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
