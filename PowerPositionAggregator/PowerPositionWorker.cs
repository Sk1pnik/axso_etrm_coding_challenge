using System.Globalization;
using System.Text;
using Axpo;
using Microsoft.Extensions.Options;

namespace PowerPositionAggregator;

public sealed class PowerPositionWorker : BackgroundService
{
    private const int TotalPeriods = 24;
    private const int TradingDayStartHour = 23;
    private const int MaxAttempts = 3;
    private const int RetryDelaySeconds = 5;

    private readonly ILogger<PowerPositionWorker> _logger;
    private readonly AggregatorSettings _settings;
    private readonly PowerService _powerService;

    private static readonly TimeZoneInfo LondonTz = ResolveLondonTimeZone();

    public PowerPositionWorker(
        ILogger<PowerPositionWorker> logger,
        IOptions<AggregatorSettings> settings,
        PowerService powerService)
    {
        _logger = logger;
        _settings = settings.Value;
        _powerService = powerService;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Power Position Extractor started. " +
            "Interval={IntervalMinutes} min | OutputFolder={ExportFolderPath} | ",
            _settings.IntervalMinutes,
            _settings.ExportFolderPath);

        await RunExtractWithRetryAsync(cancellationToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_settings.IntervalMinutes));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunExtractWithRetryAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Power Position Extractor received stop signal – shutting down.");
        }
    }

    private async Task RunExtractWithRetryAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await RunExtractAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Extract attempt {Attempt}/{MaxAttempts} failed with {ExceptionType}: {Message}",
                    attempt, MaxAttempts, ex.GetType().Name, ex.Message);

                if (attempt < MaxAttempts)
                {
                    var delaySeconds = RetryDelaySeconds * (int)Math.Pow(2, attempt - 1);

                    _logger.LogInformation(
                        "Waiting {Delay}s before retry attempt {Next}/{MaxAttempts}...",
                        delaySeconds, attempt + 1, MaxAttempts);

                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                }
            }
        }

        _logger.LogCritical(
            "All {MaxAttempts} extract attempt(s) failed. " +
            "This interval's extract has been skipped. ",
            MaxAttempts);
    }

    private async Task RunExtractAsync(CancellationToken ct)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(utcNow, LondonTz);

        var tradingDate = DetermineTradingDate(localNow);

        _logger.LogInformation(
            "Extract starting | LocalTime={LocalTime} UTC={UtcTime} TradingDate={TradingDate}",
            localNow.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            utcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            tradingDate.ToString("yyyy-MM-dd"));

        var trades = await _powerService.GetTradesAsync(tradingDate);

        if (trades is null)
            throw new InvalidOperationException(
                $"PowerService.GetTradesAsync returned null for date {tradingDate:yyyy-MM-dd}.");

        var tradeList = trades.ToList();
        _logger.LogDebug("Retrieved {TradeCount} trade(s) from PowerService.", tradeList.Count);

        var aggregated = AggregateTrades(tradeList);

        var csvContent = BuildCsv(aggregated);

        var fileName = BuildFileName(localNow);
        var outputDir = Path.GetFullPath(_settings.ExportFolderPath);

        Directory.CreateDirectory(outputDir);

        var filePath = Path.Combine(outputDir, fileName);

        var tempPath = filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, csvContent, Encoding.UTF8, ct);
        File.Move(tempPath, filePath, overwrite: true);

        _logger.LogInformation(
            "Extract complete | File={FilePath} | Periods={PeriodCount} | TotalVolume={TotalVolume:F4}",
            filePath,
            aggregated.Count,
            aggregated.Values.Sum());
    }

    private static DateTime DetermineTradingDate(DateTimeOffset localTime)
    {
        return localTime.Hour >= TradingDayStartHour
            ? localTime.Date.AddDays(1)
            : localTime.Date;
    }

    private static Dictionary<int, double> AggregateTrades(
        IEnumerable<PowerTrade> trades)
    {
        var totals = new Dictionary<int, double>(TotalPeriods);

        foreach (var trade in trades)
        {
            foreach (var period in trade.Periods)
            {
                totals[period.Period] = totals.TryGetValue(period.Period, out var existing)
                    ? existing + period.Volume
                    : period.Volume;
            }
        }

        return totals;
    }

    private static string BuildCsv(Dictionary<int, double> aggregatedByPeriod)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Local Time,Volume");

        for (int period = 1; period <= TotalPeriods; period++)
        {
            var hour = (22 + period) % 24;
            var vol = aggregatedByPeriod.GetValueOrDefault(period, 0.0);

            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:D2}:00,{1:G}",
                hour, vol));
        }

        return sb.ToString();
    }

    private static string BuildFileName(DateTimeOffset localTime)
        => $"PowerPosition_{localTime:yyyyMMdd_HHmm}.csv";

    private static TimeZoneInfo ResolveLondonTimeZone()
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
                return tz;
        }

        throw new TimeZoneNotFoundException(
            "Could not resolve the Europe/London timezone. ");
    }
}