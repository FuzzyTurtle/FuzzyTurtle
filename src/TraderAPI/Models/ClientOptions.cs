namespace TraderAPI.Models;

public sealed record class ClientOptions
{
    public required string AuthTokensFileLocation { get; init; }
    public required string DeveloperAppKey { get; init; }
    public required string DeveloperAppSecret { get; init; }
    public required string DeveloperAppCallbackUrl { get; init; }
    public required string TradingAccountUsername { get; init; }
    public required string TradingAccountPassword { get; init; }
}
