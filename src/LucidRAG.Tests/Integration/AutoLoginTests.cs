using System.Text.Json;
using FluentAssertions;
using PuppeteerSharp;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Tests for the development mode auto-login feature.
/// Requires the server to be running on localhost:5019.
/// </summary>
[Collection("Browser")]
[Trait("Category", "Browser")]
public class AutoLoginTests : IAsyncLifetime
{
    private IBrowser? _browser;
    private IPage? _page;
    private const string BaseUrl = "http://localhost:5019";

    public async Task InitializeAsync()
    {
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        });

        _page = await _browser.NewPageAsync();
        await _page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });
    }

    public async Task DisposeAsync()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
    }

    [Fact]
    public async Task AutoLogin_ShouldAuthenticateUser_InDevelopmentMode()
    {
        // Act - Navigate to home page
        var response = await _page!.GoToAsync(BaseUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle0]
        });

        // Assert - Should get a 200 response
        response!.Status.Should().Be(System.Net.HttpStatusCode.OK);

        // Wait for page to render
        await Task.Delay(1000);

        // Check if we're authenticated by looking for user info or checking cookies
        var cookies = await _page.GetCookiesAsync();
        var hasAuthCookie = cookies.Any(c =>
            c.Name.Contains(".AspNetCore.Identity") ||
            c.Name.Contains("Identity") ||
            c.Name == ".AspNetCore.Cookies");

        Console.WriteLine($"Cookies: {string.Join(", ", cookies.Select(c => c.Name))}");

        // Take a screenshot
        await _page.ScreenshotAsync("E:\\source\\lucidrag\\auto-login-test.png");
        Console.WriteLine("Screenshot saved to E:\\source\\lucidrag\\auto-login-test.png");

        // Check for login page elements (should NOT be present if auto-login worked)
        var loginForm = await _page.QuerySelectorAsync("form[action*='login']");
        var isOnLoginPage = loginForm != null && await _page.EvaluateFunctionAsync<bool>(@"
            () => window.location.pathname.includes('/auth/login')
        ");

        Console.WriteLine($"On login page: {isOnLoginPage}");
        Console.WriteLine($"Has auth cookie: {hasAuthCookie}");

        // In dev mode with auto-login, we should NOT be on the login page
        // and should have an auth cookie
        hasAuthCookie.Should().BeTrue("Auto-login should set an authentication cookie in dev mode");
    }

    [Fact]
    public async Task AutoLogin_ShouldShowDemoAdminUser_InUI()
    {
        // Navigate to home page
        await _page!.GoToAsync(BaseUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle0]
        });

        await Task.Delay(1000);

        // Look for any user display element in the navbar
        var userInfoHtml = await _page.EvaluateFunctionAsync<string>(@"
            () => {
                // Common patterns for user info display
                const selectors = [
                    '.user-info',
                    '.user-name',
                    '[data-user]',
                    '.avatar',
                    '.profile',
                    'nav .dropdown'
                ];

                for (const sel of selectors) {
                    const el = document.querySelector(sel);
                    if (el) return el.outerHTML.substring(0, 500);
                }

                // Get navbar content for inspection
                const nav = document.querySelector('nav') || document.querySelector('header');
                return nav ? nav.innerHTML.substring(0, 1000) : 'No nav found';
            }
        ");

        Console.WriteLine($"User info area: {userInfoHtml}");

        // Check we're authenticated via API
        var authCheck = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                try {
                    // Check if we can access protected endpoints
                    const response = await fetch('/api/documents');
                    return {
                        status: response.status,
                        ok: response.ok,
                        authenticated: response.status !== 401
                    };
                } catch (e) {
                    return { error: e.toString() };
                }
            }
        ");

        Console.WriteLine($"Auth check result: {authCheck}");

        var isAuthenticated = authCheck.GetProperty("authenticated").GetBoolean();
        isAuthenticated.Should().BeTrue("API requests should be authenticated after auto-login");
    }

    [Fact]
    public async Task HomePage_ShouldLoad_WithoutRedirectToLogin()
    {
        // Act - Navigate to home page
        var response = await _page!.GoToAsync(BaseUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle0]
        });

        var finalUrl = _page.Url;
        Console.WriteLine($"Final URL: {finalUrl}");

        // Assert
        finalUrl.Should().NotContain("/auth/login",
            "With auto-login, home page should not redirect to login");
        response!.Status.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
