namespace TraderAPI;
public class ClientOptions
{
    // Used to store the tokens from the OAuth flow so that we don't have to do the full flow each time
    public required string AuthTokensFileLocation { get; init; } = string.Empty;

    // From Schwab Developer Dashboard: https://developer.schwab.com/dashboard/apps
    public required string DeveloperAppKey { get; init; } = string.Empty;
    public required string DeveloperAppSecret { get; init; } = string.Empty;
    public required string DeveloperAppCallbackUrl { get; init; } = string.Empty;

    // Credentials for the account used to execute trades
    public required string TradingAccountUsername { get; init; } = string.Empty;
    public required string TradingAccountPassword { get; init; }=string.Empty;
}
