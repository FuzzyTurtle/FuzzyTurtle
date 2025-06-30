using System.CommandLine;
using System.Text.Json;
using System.Threading.RateLimiting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TraderAPI;
using TraderAPI.Models;

namespace CandlestickDownloader;

internal class Program
{
    private static ILogger s_logger = NullLogger<Program>.Instance;
    private static readonly RateLimiter RateLimiter = new TokenBucketRateLimiter(
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            QueueLimit = int.MaxValue,
            AutoReplenishment = true,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            ReplenishmentPeriod = TimeSpan.FromSeconds(0.5),
            TokensPerPeriod = 1
        });
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static async Task<int> Main(string[] args)
    {
        Option<string> configFileOption = new("--config-file")
        {
            Required = true,
            Description = "Path to the configuration file.",
            AllowMultipleArgumentsPerToken = false,
        };

        RootCommand rootCommand = new("Candlestick Downloader");
        rootCommand.Options.Add(configFileOption);
        rootCommand.SetAction(async (parseResult) =>
        {
            string configFile = parseResult.GetRequiredValue(configFileOption);
            await StartApplication(configFile);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task StartApplication(string configFile)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(configFile);

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(configFile, optional: false, reloadOnChange: false)
            .Build();

        IServiceCollection services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddOptions()
            .AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddConfiguration(configuration.GetSection("Logging"));
                loggingBuilder.AddConsole();
            })
            .AddHttpClient()
            .AddSingleton((serviceProvider) => serviceProvider.GetRequiredService<IOptions<ClientOptions>>().Value)
            .AddSingleton<Client>()
            .AddSingleton<CancellationTokenSource>();

        services
            .AddOptions<ClientOptions>()
            .BindConfiguration(nameof(ClientOptions));

        services
            .AddOptions<CandlestickDownloaderOptions>()
            .BindConfiguration(nameof(CandlestickDownloaderOptions));

        ServiceProvider serviceProvider = services.BuildServiceProvider();

        s_logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        CancellationTokenSource cancellationTokenSource = serviceProvider.GetRequiredService<CancellationTokenSource>();
        CandlestickDownloaderOptions options = serviceProvider.GetRequiredService<IOptions<CandlestickDownloaderOptions>>().Value;
        Client client = serviceProvider.GetRequiredService<Client>();

        s_logger.LogInformation("Starting Candlestick Downloader...");



        foreach (string symbol in options.Symbols)
        {
            int deltaDays = 0;

            while (true)
            {
                long startDate = GetNewYorkOpenUnixMillis(-1 * deltaDays);
                long endDate = startDate + 12 * 60 * 60 * 1000;
                deltaDays++;

                if (IsWeekend(startDate))
                {
                    s_logger.LogInformation("Skipping weekend date: {StartDate}", DateTimeOffset.FromUnixTimeMilliseconds(startDate));
                    continue;
                }

                RateLimitLease lease = await RateLimiter.AcquireAsync(1, cancellationTokenSource.Token);
                while (!lease.IsAcquired)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationTokenSource.Token);
                    lease?.Dispose();
                    lease = await RateLimiter.AcquireAsync(1, cancellationTokenSource.Token);
                }
                lease.Dispose();


                HttpResponseMessage data = await client.PriceHistoryAsync(
                        symbol,
                        "day",
                        1,
                        "minute",
                        1,
                        startDate,
                        endDate,
                        false,
                        false,
                        cancellationTokenSource.Token);

                if (data.IsSuccessStatusCode)
                {
                    s_logger.LogInformation("Successfully retrieved data for {Symbol} on {StartDate}", symbol, DateTimeOffset.FromUnixTimeMilliseconds(startDate));
                    string responseContent = await data.Content.ReadAsStringAsync(cancellationTokenSource.Token);

                    CandlestickHistory? history = JsonSerializer.Deserialize<CandlestickHistory>(responseContent, JsonSerializerOptions);
                    if (history is null || history.Empty)
                    {
                        s_logger.LogWarning("No data found for {Symbol} on {StartDate}", symbol, DateTimeOffset.FromUnixTimeMilliseconds(startDate));
                        break;
                    }

                    WriteData(responseContent, options.DataFolder, symbol, startDate);
                }
                else
                {
                    s_logger.LogError("Failed to retrieve data for {Symbol} on {StartDate}: {StatusCode} - {ReasonPhrase}", symbol, DateTimeOffset.FromUnixTimeMilliseconds(startDate), data.StatusCode, data.ReasonPhrase);
                }
            }
        }

        s_logger.LogInformation("Candlestick Downloader completed successfully.");
    }

    private static void WriteData(string responseContent, string dataFolder, string symbol, long startDate)
    {


        DateTime date = DateTimeOffset.FromUnixTimeMilliseconds(startDate).UtcDateTime;
        string year = date.Year.ToString("D4");
        string month = date.Month.ToString("D2");
        string day = date.Day.ToString("D2");
        string path = Path.Combine(dataFolder, symbol.ToUpperInvariant(), year, month);
        string fileName = $"{symbol}-{year}-{month}-{day}.json";
        string finalPath = Path.Combine(path, fileName);

        Directory.CreateDirectory(path);
        File.WriteAllText(finalPath, responseContent);

        s_logger.LogInformation("Response content saved to {FilePath}", finalPath);
    }

    private static long GetNewYorkOpenUnixMillis(int deltaDays)
    {
        TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTime nowEastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);
        DateTime deltaTime = nowEastern.Add(TimeSpan.FromDays(deltaDays));
        DateTime openEastern = new(deltaTime.Year, deltaTime.Month, deltaTime.Day, 6, 30, 0, DateTimeKind.Unspecified);
        DateTime openEasternUtc = TimeZoneInfo.ConvertTimeToUtc(openEastern, easternZone);
        long unixMillis = new DateTimeOffset(openEasternUtc).ToUnixTimeMilliseconds();
        return unixMillis;
    }

    private static bool IsWeekend(long unixMillis)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeMilliseconds(unixMillis).DateTime;
        TimeZoneInfo easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTime easternTime = TimeZoneInfo.ConvertTimeFromUtc(dateTime, easternZone);
        return easternTime.DayOfWeek == DayOfWeek.Saturday || easternTime.DayOfWeek == DayOfWeek.Sunday;
    }
}
