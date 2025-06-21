namespace TraderAPI.Models;

public sealed record class AuthTokens
{
    public required string Code { get; init; }
    public required string Session { get; init; }
    public required string TokenType { get; init; }
    public required string Scope { get; init; }
    public required DateTime Expiration { get; init; }
    public required string RefreshToken { get; init; }
    public required string AccessToken { get; init; }
    public required string IdToken { get; init; }
}
