using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Filee.Web.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.Web.Tests;

/// <summary>Uploads, conversions and downloads.</summary>
public sealed class JobTests : IDisposable
{
    private readonly FileeWebFactory _factory = new(o => o with { MaxFilesPerJob = 3 });
    private readonly HttpClient _client;

    public JobTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task A_png_converts_to_jpg_and_downloads()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var job = await FileeWebFactory.ConvertAsync(_client, "jpg", ("dot.png", FileeWebFactory.Png));

        Assert.Equal("completed", job.GetProperty("state").GetString());
        var file = Assert.Single(job.GetProperty("files").EnumerateArray());
        Assert.Equal("dot.png", file.GetProperty("name").GetString());
        Assert.Equal("done", file.GetProperty("state").GetString());
        var result = Assert.Single(file.GetProperty("results").EnumerateArray());
        Assert.Equal("dot.jpg", result.GetProperty("name").GetString());

        using var download = await _client.GetAsync($"/api/jobs/{job.GetProperty("id").GetString()}/results/0", cancellation);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/jpeg", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellation);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, bytes[..3]);
    }

    [Fact]
    public async Task Several_files_download_together_as_one_zip()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var job = await FileeWebFactory.ConvertAsync(_client, "webp", ("a.png", FileeWebFactory.Png), ("b.png", FileeWebFactory.Png));
        Assert.Equal(2, job.GetProperty("resultCount").GetInt32());

        using var response = await _client.GetAsync($"/api/jobs/{job.GetProperty("id").GetString()}/zip", cancellation);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var zip = new ZipArchive(stream);
        Assert.Equal(new[] { "a.webp", "b.webp" }, zip.Entries.Select(e => e.FullName).Order().ToArray());
    }

    [Fact]
    public async Task Files_with_the_same_name_are_both_kept()
    {
        var job = await FileeWebFactory.ConvertAsync(_client, "jpg", ("same.png", FileeWebFactory.Png), ("same.png", FileeWebFactory.Png));

        var names = job.GetProperty("files").EnumerateArray()
            .SelectMany(f => f.GetProperty("results").EnumerateArray())
            .Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Equal(2, names.Distinct().Count());
    }

    [Fact]
    public async Task An_upload_name_with_folders_stays_inside_the_job()
    {
        var job = await FileeWebFactory.ConvertAsync(_client, "jpg", ("../../../escape.png", FileeWebFactory.Png));

        var file = Assert.Single(job.GetProperty("files").EnumerateArray());
        Assert.Equal("escape.jpg", Assert.Single(file.GetProperty("results").EnumerateArray()).GetProperty("name").GetString());
        Assert.False(File.Exists(Path.Combine(_factory.Root, "escape.png")));
        var store = _factory.Services.GetRequiredService<JobStore>();
        var saved = store.Find(Guid.Parse(job.GetProperty("id").GetString()!))!;
        Assert.All(saved.UploadNames.Keys, path => Assert.StartsWith(saved.InputFolder, path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_does_not_convert_reports_why()
    {
        var job = await FileeWebFactory.ConvertAsync(_client, "jpg", ("broken.png", "not a picture"u8.ToArray()));

        var file = Assert.Single(job.GetProperty("files").EnumerateArray());
        Assert.Equal("failed", file.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(file.GetProperty("error").GetString()));
        Assert.Empty(file.GetProperty("results").EnumerateArray());
        Assert.DoesNotContain(_factory.Root, file.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(99)]
    public async Task Only_the_job_results_can_be_downloaded(int index)
    {
        var job = await FileeWebFactory.ConvertAsync(_client, "jpg", ("dot.png", FileeWebFactory.Png));

        using var response = await _client.GetAsync($"/api/jobs/{job.GetProperty("id").GetString()}/results/{index}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_job_is_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();

        foreach (var path in new[] { $"/api/jobs/{id}", $"/api/jobs/{id}/results/0", $"/api/jobs/{id}/zip" })
        {
            using var response = await _client.GetAsync(path, cancellation);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var cancel = await _client.PostAsync($"/api/jobs/{id}/cancel", null, cancellation);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        using var delete = await _client.DeleteAsync($"/api/jobs/{id}", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task A_removed_job_is_gone_with_its_files()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var job = await FileeWebFactory.ConvertAsync(_client, "jpg", ("dot.png", FileeWebFactory.Png));
        var id = job.GetProperty("id").GetString()!;
        var folder = Path.Combine(_factory.Options.JobsDirectory, Guid.Parse(id).ToString("N"));
        Assert.True(Directory.Exists(folder));

        using var delete = await _client.DeleteAsync($"/api/jobs/{id}", cancellation);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var after = await _client.GetAsync($"/api/jobs/{id}", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        for (var i = 0; i < 50 && Directory.Exists(folder); i++)
            await Task.Delay(100, cancellation);
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    public async Task An_unknown_target_is_refused(string to)
    {
        using var response = await _client.PostAsync("/api/jobs", FileeWebFactory.Form(to, ("dot.png", FileeWebFactory.Png)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_target_filee_only_reads_is_refused()
    {
        using var response = await _client.PostAsync("/api/jobs", FileeWebFactory.Form("cr2", ("dot.png", FileeWebFactory.Png)), TestContext.Current.CancellationToken);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("can't write", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_request_without_files_is_refused()
    {
        using var response = await _client.PostAsync("/api/jobs", FileeWebFactory.Form("jpg"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task More_files_than_allowed_are_refused()
    {
        var files = Enumerable.Range(1, 4).Select(i => ($"{i}.png", FileeWebFactory.Png)).ToArray();

        using var response = await _client.PostAsync("/api/jobs", FileeWebFactory.Form("jpg", files), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("high")]
    public async Task A_quality_out_of_range_is_refused(string quality)
    {
        var form = FileeWebFactory.Form("jpg", ("dot.png", FileeWebFactory.Png));
        form.Add(new StringContent(quality), "quality");

        using var response = await _client.PostAsync("/api/jobs", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_json_body_is_refused()
    {
        using var response = await _client.PostAsJsonAsync("/api/jobs", new { to = "jpg" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>Limits and expiry, each with its own server options.</summary>
public sealed class JobLimitTests
{
    [Fact]
    public async Task Uploads_over_the_limit_are_refused_with_413()
    {
        using var factory = new FileeWebFactory(o => o with { MaxUploadBytes = 1024 });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/jobs", FileeWebFactory.Form("jpg", ("big.png", new byte[8192])), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Ended_jobs_expire_with_their_files()
    {
        using var factory = new FileeWebFactory(o => o with { JobLifetime = TimeSpan.Zero });
        using var client = factory.CreateClient();
        var job = await FileeWebFactory.ConvertAsync(client, "jpg", ("dot.png", FileeWebFactory.Png));
        var id = Guid.Parse(job.GetProperty("id").GetString()!);
        var store = factory.Services.GetRequiredService<JobStore>();
        await store.Find(id)!.Completion;

        store.Sweep();

        Assert.Null(store.Find(id));
        Assert.False(Directory.Exists(Path.Combine(factory.Options.JobsDirectory, id.ToString("N"))));
        using var response = await client.GetAsync($"/api/jobs/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Leftover_job_folders_are_deleted_at_start()
    {
        using var factory = new FileeWebFactory();
        var leftover = Path.Combine(factory.Options.JobsDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leftover);
        var other = Path.Combine(factory.Options.JobsDirectory, "keep-me");
        Directory.CreateDirectory(other);

        using var client = factory.CreateClient();
        await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        factory.Services.GetRequiredService<JobStore>();

        Assert.False(Directory.Exists(leftover));
        Assert.True(Directory.Exists(other));
    }
}
