using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        CancellationTokenSource cancellationTokenSource = serviceProvider.GetRequiredService<CancellationTokenSource>();
        CandlestickDownloaderOptions options = serviceProvider.GetRequiredService<IOptions<CandlestickDownloaderOptions>>().Value;
        Client client = serviceProvider.GetRequiredService<Client>();
        
        HttpResponseMessage data = await client.PriceHistoryAsync(
             options.Symbols,
             options.PeriodType,
             options.Period,
             options.FrequencyType,
             options.Frequency,
             options.StartDate,
             options.EndDate,
             options.NeedExtendedData,
             options.NeedPreviousClose,
             cancellationTokenSource.Token);

        data.EnsureSuccessStatusCode();
    }
}
