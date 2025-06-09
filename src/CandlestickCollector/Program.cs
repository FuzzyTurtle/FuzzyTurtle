using System.CommandLine;
using System.CommandLine.NamingConventionBinder;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TraderAPI;

namespace CandlestickCollector;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        RootCommand rootCommand = [
            new Option<string>("--configFile",()=> "config.json", "JSON configuration file")
        ];

        rootCommand.Description = "Candlestick Collector - Console application to download candlesticks.";
        rootCommand.Handler = CommandHandler.Create<string>(StartApplication);

        return await rootCommand.InvokeAsync(args);
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
                loggingBuilder.AddConfiguration(configuration.GetSection("Logging"));
                loggingBuilder.AddConsole();
            })
            .AddHttpClient()
            .AddSingleton((serviceProvider) => serviceProvider.GetRequiredService<IOptions<ClientOptions>>().Value)
            .AddSingleton<Client>()
            .AddSingleton<Application>()
            .AddSingleton<CancellationTokenSource>();

        services
            .AddOptions<ClientOptions>()
            .BindConfiguration(nameof(ClientOptions));

        ServiceProvider serviceProvider = services.BuildServiceProvider();

        Application application = serviceProvider.GetRequiredService<Application>();
        CancellationTokenSource cts = serviceProvider.GetRequiredService<CancellationTokenSource>();

        await application.RunAsync(cts.Token);
    }
}
