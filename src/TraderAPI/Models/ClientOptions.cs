namespace TraderAPI.Models;

public sealed record ClientOptions(
    string AuthTokensFileLocation,
    string DeveloperAppKey,
    string DeveloperAppSecret,
    string DeveloperAppCallbackUrl,
    string TradingAccountUsername,
    string TradingAccountPassword);
