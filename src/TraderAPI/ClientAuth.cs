using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

using SeleniumExtras.WaitHelpers;

using TraderAPI.Models;

namespace TraderAPI;
internal class ClientAuth
{
    private readonly HttpClient _httpClient;
    private readonly ClientOptions _clientOptions;

    public AuthTokens AuthTokens { get; private set; }

    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(AuthTokens.Code) &&
        !string.IsNullOrWhiteSpace(AuthTokens.Session) &&
        !string.IsNullOrWhiteSpace(AuthTokens.RefreshToken) &&
        !string.IsNullOrWhiteSpace(AuthTokens.AccessToken) &&
        !string.IsNullOrWhiteSpace(AuthTokens.IdToken) &&
        DateTime.Compare(DateTime.UtcNow, AuthTokens.Expiration) < 0;

    private static void WaitForUrl(IWebDriver driver, string expectedUrl, TimeSpan timeout)
    {
        WebDriverWait wait = new(driver, timeout);
        wait.Until(drv => drv.Url.StartsWith(expectedUrl, StringComparison.OrdinalIgnoreCase));
    }
    private static void WaitForEndOfUrl(IWebDriver driver, string endOfUrl, TimeSpan timeout)
    {
        WebDriverWait wait = new(driver, timeout);
        wait.Until(drv => drv.Url.EndsWith(endOfUrl, StringComparison.OrdinalIgnoreCase));
    }

    private static void WaitForElementVisible(IWebDriver driver, By by, TimeSpan timeout)
    {
        WebDriverWait wait = new WebDriverWait(driver, timeout);
        wait.Until(ExpectedConditions.ElementIsVisible(by));
    }

    public ClientAuth(HttpClient httpClient, ClientOptions clientOptions)
    {
        _httpClient = httpClient;
        _clientOptions = clientOptions;
        try
        {
            if (!string.IsNullOrWhiteSpace(_clientOptions.AuthTokensFileLocation) && File.Exists(_clientOptions.AuthTokensFileLocation))
            {
                AuthTokens = JsonSerializer.Deserialize<AuthTokens>(File.ReadAllText(_clientOptions.AuthTokensFileLocation))!;
            }
        }
        catch { }
        finally
        {
            AuthTokens ??= new AuthTokens()
            {
                Code = string.Empty,
                Session = string.Empty,
                TokenType = string.Empty,
                Scope = string.Empty,
                Expiration = DateTime.MinValue,
                RefreshToken = string.Empty,
                AccessToken = string.Empty,
                IdToken = string.Empty,
            };
        }
    }

    public async Task<bool> RefreshAuthAsync(CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested &&
            (IsAuthenticated ||
            await RefreshAccessTokenAsync(cancellationToken) ||
            AutomateOAuthFlow() && await CreateAccessTokenAsync(cancellationToken));
    }

    private bool AutomateOAuthFlow()
    {
        try
        {
            ChromeOptions options = new();
            options.AddExcludedArgument("enable-automation");
            options.AddAdditionalOption("useAutomationExtension", false);
            options.AddArgument("--disable-blink-features=AutomationControlled");

            using ChromeDriver driver = new(options);
            ((IJavaScriptExecutor)driver).ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            bool status = ProcessSchwabMicroLoginSite(driver);

            driver.Quit();

            return status;
        }
        catch
        {
            return false;
        }
    }

    private bool ProcessSchwabMicroLoginSite(ChromeDriver driver)
    {
        try
        {
            // Initial Login Page
            driver.Navigate().GoToUrl($"https://api.schwabapi.com/v1/oauth/authorize?client_id={_clientOptions.DeveloperAppKey}&redirect_uri={_clientOptions.DeveloperAppCallbackUrl}");

            IWebElement name = driver.FindElement(By.CssSelector("#loginIdInput"));
            name.Click();
            name.Clear();
            name.SendKeys(_clientOptions.TradingAccountUsername);

            IWebElement pass = driver.FindElement(By.CssSelector("#passwordInput"));
            pass.Click();
            pass.Clear();
            pass.SendKeys(_clientOptions.TradingAccountPassword);

            IWebElement login = driver.FindElement(By.CssSelector("#btnLogin"));
            login.Click();


            // Accept Terms Page
            //WaitForUrl(driver, "https://sws-gateway.schwab.com/ui/host/#/third-party-auth/cag", TimeSpan.FromMinutes(2));
            WaitForEndOfUrl(driver, "/#/third-party-auth/cag", TimeSpan.FromMinutes(2));

            IWebElement accept = driver.FindElement(By.CssSelector("#acceptTerms"));
            accept.Click();

            IWebElement acceptSubmit = driver.FindElement(By.CssSelector("#submit-btn"));
            acceptSubmit.Click();

            WaitForElementVisible(driver, By.CssSelector("#agree-modal-btn-"), TimeSpan.FromMinutes(2));
            IWebElement agreemodal = driver.FindElement(By.CssSelector("#agree-modal-btn-"));
            agreemodal.Click();


            // Select Account Page
            //WaitForUrl(driver, "https://sws-gateway.schwab.com/ui/host/#/third-party-auth/account", TimeSpan.FromMinutes(2));
            WaitForEndOfUrl(driver, "/#/third-party-auth/account", TimeSpan.FromMinutes(2));

            IWebElement checkbox = driver.FindElement(By.CssSelector("#form-container > div.form-group > label > input"));
            if (!checkbox.Selected)
            {
                checkbox.Click();
            }

            IWebElement accountSubmit = driver.FindElement(By.CssSelector("#submit-btn"));
            accountSubmit.Click();


            // Confirm Page
            //WaitForUrl(driver, "https://sws-gateway.schwab.com/ui/host/#/third-party-auth/confirmation", TimeSpan.FromMinutes(2));
            WaitForEndOfUrl(driver, "/#/third-party-auth/confirmation", TimeSpan.FromMinutes(2));

            IWebElement confirmSubmit = driver.FindElement(By.CssSelector("#cancel-btn"));
            confirmSubmit.Click();


            // Callback Page
            WaitForUrl(driver, _clientOptions.DeveloperAppCallbackUrl, TimeSpan.FromMinutes(2));

            string url = driver.Url;
            Uri uri = new(url);
            NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);

            AuthTokens = AuthTokens with
            {
                Code = query["code"] ?? AuthTokens.Code,
                Session = query["session"] ?? AuthTokens.Session,
            };

            return SaveAuthTokensFile();
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CreateAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        string input = $"{_clientOptions.DeveloperAppKey}:{_clientOptions.DeveloperAppSecret}";
        string base64Input = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

        HttpRequestMessage request = new(HttpMethod.Post, "https://api.schwabapi.com/v1/oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Input);

        Dictionary<string, string> formData = new()
        {
            { "grant_type", "authorization_code" },
            { "code", AuthTokens.Code },
            { "redirect_uri", _clientOptions.DeveloperAppCallbackUrl }
        };

        FormUrlEncodedContent content = new(formData);
        request.Content = content;
        HttpResponseMessage message = await _httpClient.SendAsync(request, cancellationToken);

        if (!message.IsSuccessStatusCode)
        {
            return false;
        }

        try
        {
            string contents = await message.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(contents);

            AuthTokens = AuthTokens with
            {
                Expiration = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt64()),
                TokenType = doc.RootElement.GetProperty("token_type").GetString() ?? AuthTokens.TokenType,
                Scope = doc.RootElement.GetProperty("scope").GetString() ?? AuthTokens.Scope,
                RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? AuthTokens.RefreshToken,
                AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? AuthTokens.AccessToken,
                IdToken = doc.RootElement.GetProperty("id_token").GetString() ?? AuthTokens.IdToken
            };

            return SaveAuthTokensFile();
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        string input = $"{_clientOptions.DeveloperAppKey}:{_clientOptions.DeveloperAppSecret}";
        string base64Input = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

        HttpRequestMessage request = new(HttpMethod.Post, "https://api.schwabapi.com/v1/oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Input);

        Dictionary<string, string> formData = new()
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", AuthTokens.RefreshToken }
        };

        FormUrlEncodedContent content = new(formData);
        request.Content = content;
        HttpResponseMessage message = await _httpClient.SendAsync(request, cancellationToken);

        if (!message.IsSuccessStatusCode)
        {
            return false;
        }

        try
        {
            string contents = await message.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(contents);

            AuthTokens = AuthTokens with
            {
                Expiration = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt64()),
                TokenType = doc.RootElement.GetProperty("token_type").GetString() ?? AuthTokens.TokenType,
                Scope = doc.RootElement.GetProperty("scope").GetString() ?? AuthTokens.Scope,
                RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? AuthTokens.RefreshToken,
                AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? AuthTokens.AccessToken,
                IdToken = doc.RootElement.GetProperty("id_token").GetString() ?? AuthTokens.IdToken
            };

            return SaveAuthTokensFile();
        }
        catch
        {
            return false;
        }
    }

    private bool SaveAuthTokensFile()
    {
        try
        {
            File.WriteAllText(_clientOptions.AuthTokensFileLocation, JsonSerializer.Serialize(AuthTokens));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
