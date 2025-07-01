using System.CommandLine;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

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
            .AddSingleton<JsonSerializerOptions>((_) =>
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true,
                })
            .AddSingleton<RateLimiter>((_) =>
                new TokenBucketRateLimiter(
                    new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 120,
                        QueueLimit = int.MaxValue,
                        AutoReplenishment = true,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(0.5),
                        TokensPerPeriod = 1
                    }))
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

        ILogger<Program> logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        CancellationTokenSource cancellationTokenSource = serviceProvider.GetRequiredService<CancellationTokenSource>();
        CandlestickDownloaderOptions options = serviceProvider.GetRequiredService<IOptions<CandlestickDownloaderOptions>>().Value;
        Client client = serviceProvider.GetRequiredService<Client>();
        RateLimiter rateLimiter = serviceProvider.GetRequiredService<RateLimiter>();
        JsonSerializerOptions jsonOptions = serviceProvider.GetRequiredService<JsonSerializerOptions>();

        logger.LogInformation("Starting Candlestick Downloader...");

        await DownloadAndSaveCandlesticks(
            client,
            options,
            logger,
            rateLimiter,
            jsonOptions,
            cancellationTokenSource.Token);

        logger.LogInformation("Candlestick Downloader completed.");
    }

    private static async Task DownloadAndSaveCandlesticks(
        Client client,
        CandlestickDownloaderOptions options,
        ILogger logger,
        RateLimiter rateLimiter,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataFolder);

        foreach (string symbol in options.Symbols)
        {
            logger.LogInformation("Downloading data for symbol: {Symbol}", symbol);

            long startDate = 0;
            long endDate = 0;
            Dictionary<long, Candlestick> allData = new();

            while (true)
            {
                await EnsureTokenLease(rateLimiter, cancellationToken);

                HttpResponseMessage data = await client.PriceHistoryAsync(symbol, "day", 10, "minute", 1, startDate, endDate, false, false, cancellationToken);

                if (!data.IsSuccessStatusCode)
                {
                    break;
                }

                string responseContent = await data.Content.ReadAsStringAsync(cancellationToken);
                CandlestickHistory? history = JsonSerializer.Deserialize<CandlestickHistory>(responseContent, jsonOptions);
                if (history is null || history.Empty)
                {
                    break;
                }

                bool newData = false;

                foreach (Candlestick candle in history.Candles)
                {
                    if (!allData.ContainsKey(candle.DateTime))
                    {
                        allData[candle.DateTime] = candle;
                        newData = true;
                    }
                }

                if (!newData)
                {
                    break;
                }

                endDate = history.Candles.Min(c => c.DateTime);
                endDate = DateTimeOffset.FromUnixTimeMilliseconds(endDate).AddMinutes(-1).ToUnixTimeMilliseconds();
            }

            FileInfo fileInfo = new(Path.Combine(options.DataFolder, $"{symbol.ToUpperInvariant()}.json"));

            FileStream fileStream;

            if (!fileInfo.Exists)
            {
                fileStream = fileInfo.Create();
            }
            else
            {
                fileStream = File.OpenRead(fileInfo.FullName);
            }


            CandlestickHistory existingHistory;
            try
            {
                existingHistory = await JsonSerializer.DeserializeAsync<CandlestickHistory>(fileStream, jsonOptions, cancellationToken) ??
                new CandlestickHistory()
                {
                    Candles = new List<Candlestick>(),
                    Empty = true,
                    Symbol = symbol,
                };
            }
            catch
            {
                existingHistory = new CandlestickHistory()
                {
                    Candles = new List<Candlestick>(),
                    Empty = true,
                    Symbol = symbol,
                };
            }

            fileStream.Dispose();


            Dictionary<long, Candlestick> candlesToSave = new();

            foreach (Candlestick candle in existingHistory.Candles)
            {
                if (!candlesToSave.ContainsKey(candle.DateTime))
                {
                    candlesToSave[candle.DateTime] = candle;
                }
            }

            foreach (Candlestick candle in allData.Values)
            {
                if (!candlesToSave.ContainsKey(candle.DateTime))
                {
                    candlesToSave[candle.DateTime] = candle;
                }
            }

            CandlestickHistory saveHistory = new()
            {
                Candles = [.. candlesToSave.Values.OrderBy(c => c.DateTime)],
                Empty = candlesToSave.Count == 0,
                Symbol = symbol
            };

            await WriteData(saveHistory, fileInfo, jsonOptions);

        }
    }

    private static async Task EnsureTokenLease(RateLimiter rateLimiter, CancellationToken cancellationToken)
    {
        RateLimitLease lease = await rateLimiter.AcquireAsync(1, cancellationToken);
        while (!lease.IsAcquired)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            lease?.Dispose();
            lease = await rateLimiter.AcquireAsync(1, cancellationToken);
        }
        lease.Dispose();
    }

    private static async Task WriteData(CandlestickHistory candlestickHistory, FileInfo fileInfo, JsonSerializerOptions jsonOptions)
    {
        using FileStream stream = fileInfo.Open(FileMode.Truncate);
        await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(candlestickHistory, jsonOptions));
        await stream.FlushAsync();
    }
}
