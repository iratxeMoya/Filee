using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Filee.Web.Tests;

/// <summary>The read-only endpoints and the page.</summary>
public sealed class ApiTests : IDisposable
{
    private readonly FileeWebFactory _factory = new();
    private readonly HttpClient _client;

    public ApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Health_reports_ok_and_the_limits()
    {
        var health = await _client.GetFromJsonAsync<JsonElement>("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal(_factory.Options.MaxFilesPerJob, health.GetProperty("maxFiles").GetInt32());
        Assert.Equal(10, health.GetProperty("maxUploadMb").GetInt64());
    }

    [Fact]
    public async Task Formats_list_known_formats_without_the_folder_pseudo_format()
    {
        var formats = await _client.GetFromJsonAsync<JsonElement[]>("/api/formats", TestContext.Current.CancellationToken);
        var ids = formats!.Select(f => f.GetProperty("id").GetString()).ToList();

        Assert.Contains("png", ids);
        Assert.Contains("pdf", ids);
        Assert.DoesNotContain("folder", ids);
    }

    [Fact]
    public async Task Targets_of_a_picture_include_other_pictures_and_pdf_but_not_itself()
    {
        var targets = await _client.GetFromJsonAsync<JsonElement>("/api/targets?ext=png", TestContext.Current.CancellationToken);
        var ids = targets.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();

        Assert.Contains("jpg", ids);
        Assert.Contains("webp", ids);
        Assert.Contains("pdf", ids);
        Assert.DoesNotContain("png", ids);
    }

    [Fact]
    public async Task Targets_by_extension_alias_find_the_format()
    {
        var targets = await _client.GetFromJsonAsync<JsonElement>("/api/targets?ext=JPEG", TestContext.Current.CancellationToken);

        var source = Assert.Single(targets.GetProperty("sources").EnumerateArray());
        Assert.Equal("jpg", source.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Targets_of_several_formats_are_those_every_one_converts_to()
    {
        // Which targets exist depends on the engines installed (with rhwp a CSV reaches pictures through PDF), so this
        // checks the rule itself: exactly the targets each format has on its own.
        var cancellation = TestContext.Current.CancellationToken;
        async Task<HashSet<string>> TargetsOf(string query) =>
            (await _client.GetFromJsonAsync<JsonElement>($"/api/targets?{query}", cancellation))
                .GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToHashSet();

        var png = await TargetsOf("ext=png");
        var csv = await TargetsOf("ext=csv");
        var both = await TargetsOf("ext=png&ext=csv");

        Assert.NotEmpty(both);
        Assert.Equal(png.Intersect(csv).Order(), both.Order());
    }

    [Fact]
    public async Task Targets_report_unknown_extensions()
    {
        var targets = await _client.GetFromJsonAsync<JsonElement>("/api/targets?ext=png&ext=notaformat", TestContext.Current.CancellationToken);

        Assert.Equal("notaformat", Assert.Single(targets.GetProperty("unknown").EnumerateArray()).GetString());
        Assert.NotEmpty(targets.GetProperty("targets").EnumerateArray());
    }

    [Fact]
    public async Task Targets_without_an_extension_are_refused()
    {
        using var response = await _client.GetAsync("/api/targets", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Engines_list_the_built_in_image_engine_as_available()
    {
        var engines = await _client.GetFromJsonAsync<JsonElement[]>("/api/engines", TestContext.Current.CancellationToken);

        var magick = Assert.Single(engines!, e => e.GetProperty("id").GetString() == "magick");
        Assert.True(magick.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task The_page_and_its_files_are_served()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var page = await _client.GetAsync("/", cancellation);
        var html = await page.Content.ReadAsStringAsync(cancellation);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("app.js", html);
        foreach (var file in new[] { "/app.js", "/app.css", "/filee.png" })
        {
            using var response = await _client.GetAsync(file, cancellation);
            Assert.True(response.IsSuccessStatusCode, file);
        }
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        using var response = await _client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
    }
}
