namespace CandlestickDownloader;
public sealed record class CandlestickDownloaderOptions
{
    public required List<string> Symbols { get; init; }
    public required string DataFolder { get; init; }
}