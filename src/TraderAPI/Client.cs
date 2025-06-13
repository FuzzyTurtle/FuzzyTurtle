using System.Net.Http.Headers;

using TraderAPI.Models;

namespace TraderAPI;

public class Client
{
    // link goes here...
    private const string TradingUrl = "https://api.schwabapi.com/trader/v1";

    // Market Data APIs
    // https://developer.schwab.com/products/trader-api--individual/details/specifications/Market%20Data%20Production
    private const string MarketDataUrl = "https://api.schwabapi.com/marketdata/v1";

    private readonly HttpClient _httpClient;
    private readonly ClientOptions _clientOptions;
    private readonly ClientAuth _clientAuth;

    public Client(HttpClient httpClient, ClientOptions options)
    {
        _httpClient = httpClient;
        _clientOptions = options;
        _clientAuth = new ClientAuth(_httpClient, _clientOptions);
    }

    public async Task<HttpResponseMessage> PriceHistoryAsync(string symbol, string periodType, int period, string frequencyType, int frequency, long startDate, long endDate, bool needExtendedHoursData, bool needPreviousClose, CancellationToken cancellationToken)
    {
        string path = "/pricehistory";

        // TODO: clean up - don't add items that are empty/missing
        Dictionary<string, string> query = new()
        {
            ["symbol"] = symbol,
            ["periodType"] = periodType,
            ["period"] = period.ToString(),
            ["frequencyType"] = frequencyType,
            ["frequency"] = frequency.ToString(),
            ["startDate"] = startDate.ToString(),
            ["endDate"] = endDate.ToString(),
            ["needExtendedHoursData"] = needExtendedHoursData.ToString(),
            ["needPreviousClose"] = needPreviousClose.ToString(),
        };

        string queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        string url = $"{MarketDataUrl}{path}?{queryString}";

        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(_clientAuth.AuthTokens.TokenType, _clientAuth.AuthTokens.AccessToken);

        HttpResponseMessage results = await _httpClient.SendAsync(request, cancellationToken);
        return results;
    }
}
