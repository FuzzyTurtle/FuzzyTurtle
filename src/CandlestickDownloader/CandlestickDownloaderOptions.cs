namespace CandlestickDownloader;
public sealed record class CandlestickDownloaderOptions
{
    public required string Symbols { get; init; }
    public required string PeriodType { get; init; }
    public required int Period { get; init; }
    public required string FrequencyType { get; init; }
    public required int Frequency { get; init; }
    public required long StartDate { get; init; }
    public required long EndDate { get; init; }
    public required bool NeedExtendedData { get; init; }
    public required bool NeedPreviousClose { get; init; }
}