using System.Net;
using System.Net.Http.Headers;

namespace Filee.Web.Tests;

/// <summary>FILEE_WEB_TOKEN: every /api request needs it, except the health check.</summary>
public sealed class TokenTests : IDisposable
{
    private const string Token = "s3cret-token";
    private readonly FileeWebFactory _factory = new(o => o with { Token = Token });
    private readonly HttpClient _client;

    public TokenTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer s3cret-toke")]
    [InlineData("Basic s3cret-token")]
    public async Task Api_without_the_right_token_is_refused(string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/formats");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_is_accepted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/formats");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_cookie_is_accepted_for_download_links()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/formats");
        request.Headers.Add("Cookie", $"filee_web_token={Token}");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/")]
    [InlineData("/app.js")]
    public async Task The_health_check_and_the_page_stay_open(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
