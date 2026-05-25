namespace PowerPositionAggregator;

public class AggregatorSettings
{
    public int IntervalMinutes { get; set; } = 5;

    public string ExportFolderPath { get; set; } = "./";
}