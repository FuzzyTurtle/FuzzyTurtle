namespace CandlestickDownloader;
internal class CandlestickHistory
{
    public required string Symbol { get; init; }
    public required bool Empty { get; init; }
    public required List<Candlestick> Candles { get; init; } = new List<Candlestick>();
}
