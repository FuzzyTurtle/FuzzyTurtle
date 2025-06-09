using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

using Microsoft.Extensions.Logging;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

using SeleniumExtras.WaitHelpers;

namespace TraderAPI;

public class Client
{
    private readonly HttpClient _httpClient;
    private readonly ClientOptions _options;
    private readonly ILogger<Client> _logger;

    private AuthTokens _authTokens;
    private string OAuthUrl => $"https://api.schwabapi.com/v1/oauth/authorize?client_id={_options.DeveloperAppKey}&redirect_uri={_options.DeveloperAppCallbackUrl}";
    private string TokenUrl => "https://api.schwabapi.com/v1/oauth/token";
    private string TradingUrl => "https://api.schwabapi.com/trader/v1";
    private string MarketDataUrl => "https://api.schwabapi.com/marketdata/v1";

    public Client(HttpClient httpClient, ClientOptions options, ILogger<Client> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        try
        {
            string json = File.ReadAllText(_options.AuthTokensFileLocation);
            _authTokens = JsonSerializer.Deserialize<AuthTokens>(json) ?? new();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error loading auth tokens file");
        }
        finally
        {
            _authTokens ??= new();
        }

        _logger.LogTrace("Client created");
    }

    // Market Data APIs
    // https://developer.schwab.com/products/trader-api--individual/details/specifications/Market%20Data%20Production

    public async Task<HttpResponseMessage> PriceHistoryAsync(
        string symbol,
        string periodType,
        int period,
        string frequencyType,
        int frequency,
        long startDate,
        long endDate,
        bool needExtendedHoursData,
        bool needPreviousClose,
        CancellationToken cancellationToken)
    {
        string path = "/pricehistory";
        var query = new Dictionary<string, string>
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
        request.Headers.Authorization = new AuthenticationHeaderValue(_authTokens.TokenType, _authTokens.AccessToken);

        HttpResponseMessage results = await _httpClient.SendAsync(request, cancellationToken);

        if (results.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            if (await RefreshTokensAsync(cancellationToken, forceFullFlow: true))
            {
                request.Headers.Clear();
                request.Headers.Authorization = new AuthenticationHeaderValue(_authTokens.TokenType, _authTokens.AccessToken);
                results = await _httpClient.GetAsync(url, cancellationToken);
            }
        }

        return results;
    }

    private async Task<bool> RefreshTokensAsync(CancellationToken cancellationToken, bool forceFullFlow = false)
    {
        if (forceFullFlow ||
            string.IsNullOrWhiteSpace(_authTokens.RefreshToken) ||
            string.IsNullOrWhiteSpace(_authTokens.AccessToken) ||
            string.IsNullOrWhiteSpace(_authTokens.Code) ||
            string.IsNullOrWhiteSpace(_authTokens.Session))
        {
            AutomateOAuthFlow();
            await CreateAccessToken(cancellationToken);
        }
        else
        {
            // refresh tokens
        }

        return true;
    }

    private async Task CreateAccessToken(CancellationToken cancellationToken)
    {
        string input = $"{_options.DeveloperAppKey}:{_options.DeveloperAppSecret}";
        string base64Input = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

        HttpRequestMessage request = new(HttpMethod.Post, TokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Input);

        Dictionary<string, string> formData = new()
        {
            { "grant_type", "authorization_code" },
            { "code", _authTokens.Code },
            { "redirect_uri", _options.DeveloperAppCallbackUrl }
        };

        FormUrlEncodedContent content = new(formData);

        request.Content = content;

        HttpResponseMessage message = await _httpClient.SendAsync(request, cancellationToken);

        message.EnsureSuccessStatusCode();

        string contents = await message.Content.ReadAsStringAsync(cancellationToken);

        using JsonDocument doc = JsonDocument.Parse(contents);

        _authTokens.Expiration = DateTime.UtcNow.AddSeconds(0.9 * doc.RootElement.GetProperty("expires_in").GetInt64());
        _authTokens.TokenType = doc.RootElement.GetProperty("token_type").GetString() ?? string.Empty;
        _authTokens.Scope = doc.RootElement.GetProperty("scope").GetString() ?? string.Empty;
        _authTokens.RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? string.Empty;
        _authTokens.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        _authTokens.IdToken = doc.RootElement.GetProperty("id_token").GetString() ?? string.Empty;

        string json = JsonSerializer.Serialize(_authTokens);
        File.WriteAllText(_options.AuthTokensFileLocation, json);
    }

    private void AutomateOAuthFlow()
    {
        _logger.LogTrace("Beginning automation of OAuth flow...");

        ChromeOptions options = new();
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);
        options.AddArgument("--disable-blink-features=AutomationControlled");

        using ChromeDriver driver = new(options);
        ((IJavaScriptExecutor)driver).ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

        ProcessSchwabMicroLoginSite(driver);

        driver.Quit();
    }

    private void ProcessSchwabMicroLoginSite(ChromeDriver driver)
    {
        InitialLoginPage(driver);
        AcceptTermsPage(driver);
        SelectAccountPage(driver);
        ConfirmPage(driver);
        CallbackPage(driver);
    }

    private void CallbackPage(ChromeDriver driver)
    {
        WaitForUrl(driver, _options.DeveloperAppCallbackUrl, TimeSpan.FromSeconds(30));

        string url = driver.Url;
        Uri uri = new Uri(url);
        NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);

        _authTokens.Code = query["code"] ?? string.Empty;
        _authTokens.Session = query["session"] ?? string.Empty;
    }

    private void ConfirmPage(ChromeDriver driver)
    {
        WaitForUrl(driver, "https://sws-gateway.schwab.com/ui/host/#/third-party-auth/confirmation", TimeSpan.FromSeconds(30));

        IWebElement submit = driver.FindElement(By.CssSelector("#cancel-btn"));
        submit.Click();
    }

    private void SelectAccountPage(ChromeDriver driver)
    {
        WaitForUrl(driver, "https://sws-gateway.schwab.com/ui/host/#/third-party-auth/account", TimeSpan.FromSeconds(30));

        IWebElement checkbox = driver.FindElement(By.CssSelector("#form-container > div.form-group > label > input"));
        if (!checkbox.Selected)
        {
            checkbox.Click();
        }

        IWebElement submit = driver.FindElement(By.CssSelector("#submit-btn"));
        submit.Click();
    }

    private void AcceptTermsPage(ChromeDriver driver)
    {
        WaitForUrl(driver, "https://sws-gateway.schwab.com/ui/host/#/third-party-auth/cag", TimeSpan.FromSeconds(30));

        IWebElement accept = driver.FindElement(By.CssSelector("#acceptTerms"));
        accept.Click();

        IWebElement submit = driver.FindElement(By.CssSelector("#submit-btn"));
        submit.Click();

        WaitForElementVisible(driver, By.CssSelector("#agree-modal-btn-"), TimeSpan.FromSeconds(30));
        IWebElement agreemodal = driver.FindElement(By.CssSelector("#agree-modal-btn-"));
        agreemodal.Click();
    }

    private void InitialLoginPage(ChromeDriver driver)
    {
        driver.Navigate().GoToUrl(OAuthUrl);

        IWebElement name = driver.FindElement(By.CssSelector("#loginIdInput"));
        name.Click();
        name.Clear();
        name.SendKeys(_options.TradingAccountUsername);

        IWebElement pass = driver.FindElement(By.CssSelector("#passwordInput"));
        pass.Click();
        pass.Clear();
        pass.SendKeys(_options.TradingAccountPassword);

        IWebElement login = driver.FindElement(By.CssSelector("#btnLogin"));
        login.Click();
    }

    private static void WaitForUrl(IWebDriver driver, string expectedUrl, TimeSpan timeout)
    {
        WebDriverWait wait = new(driver, timeout);
        wait.Until(drv => drv.Url.StartsWith(expectedUrl, StringComparison.OrdinalIgnoreCase));
    }

    private static void WaitForElementVisible(IWebDriver driver, By by, TimeSpan timeout)
    {
        var wait = new WebDriverWait(driver, timeout);
        wait.Until(ExpectedConditions.ElementIsVisible(by));
    }
}
