using Axpo;
using PowerPositionAggregator;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo
    .Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Power Position Extractor starting up");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithThreadId()
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/power-position-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31,
            outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}"));

    builder.Services.Configure<AggregatorSettings>(
        builder.Configuration.GetSection("AggregatorSettings"));

    builder.Services.AddSingleton<PowerService>(_ => new PowerService());
    builder.Services.AddHostedService<PowerPositionWorker>();

    var host = builder.Build();
    await host.RunAsync();

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}