using Microsoft.Extensions.Logging;

using TraderAPI;

namespace CandlestickCollector;
public class Application
{
    private readonly ILogger<Application> _logger;
    private readonly Client _client;

    public Application(ILogger<Application> logger, Client client)
    {
        _logger = logger;
        _client = client;
        _logger.LogTrace("{Application} has been created...", nameof(Application));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Cancellation has been requested");
            return;
        }

        _logger.LogTrace("{Application} is running...", nameof(Application));
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

        HttpResponseMessage responseMessage = await _client.PriceHistoryAsync(
            "SPY", // symbol
            "day", // period type
            10, // period
            "minute", // frequency type
            1, // frequency
            DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeMilliseconds(), // start date
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), // end date
            false, // extended hours data
            false, // previous close data
            cancellationToken);
        Console.WriteLine(await responseMessage.Content.ReadAsStringAsync(cancellationToken));
        responseMessage.EnsureSuccessStatusCode();
    }
}
