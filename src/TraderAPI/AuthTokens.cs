namespace TraderAPI;
public class AuthTokens
{
    public string RefreshToken { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Session { get; set; } = string.Empty;
    public DateTime Expiration { get; set; } = DateTime.MinValue;
    public string TokenType { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string IdToken { get; set; } = string.Empty;
}
