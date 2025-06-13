namespace TraderAPI.Models;

public sealed record AuthTokens(
    string Code,
    string Session,
    string TokenType,
    string Scope,
    DateTime Expiration,
    string RefreshToken,
    string AccessToken,
    string IdToken);

