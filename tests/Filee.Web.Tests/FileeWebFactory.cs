// Filee.Web in memory for the tests: its own data and jobs folders in %TEMP%, and the options a test asks for.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.Web.Tests;

internal sealed class FileeWebFactory : WebApplicationFactory<Program>
{
    /// <summary>A 1×1 PNG: enough for ImageMagick, which every build has.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public FileeWebFactory(Func<WebOptions, WebOptions>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "filee-web-tests", Guid.NewGuid().ToString("N"));
        var options = new WebOptions
        {
            DataDirectory = Path.Combine(Root, "data"),
            JobsDirectory = Path.Combine(Root, "jobs"),
            MaxUploadBytes = 10 * 1024 * 1024,
        };
        Options = configure?.Invoke(options) ?? options;
    }

    /// <summary>This test server's own folder (data and jobs), deleted with it.</summary>
    public string Root { get; }

    public WebOptions Options { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program sets its content root to the program's folder; the test output has the same wwwroot.
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseEnvironment("Production");
        builder.ConfigureTestServices(services => services.AddSingleton(Options));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A form with <paramref name="files"/> (name → bytes) and the target format.</summary>
    public static MultipartFormDataContent Form(string? to, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "files", name);
        }
        if (to is not null)
            form.Add(new StringContent(to), "to");
        return form;
    }

    /// <summary>Starts a job and waits until it ends; the job as GET /api/jobs/{id} gives it.</summary>
    public static async Task<JsonElement> ConvertAsync(HttpClient client, string to, params (string Name, byte[] Bytes)[] files)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var response = await client.PostAsync("/api/jobs", Form(to, files), cancellation);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var job = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
        return await WaitAsync(client, job.GetProperty("id").GetString()!);
    }

    public static async Task<JsonElement> WaitAsync(HttpClient client, string id)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", cancellation);
            if (job.GetProperty("finished").GetBoolean())
                return job;
            Assert.True(DateTime.UtcNow < deadline, $"Job {id} did not finish: {job}");
            await Task.Delay(100, cancellation);
        }
    }
}
