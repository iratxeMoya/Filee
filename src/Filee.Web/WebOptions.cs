// Settings of the web server, read once at start from environment variables (FILEE_WEB_*), so a container is
// configured without files.

using Filee.Core.Settings;

namespace Filee.Web;

/// <summary>Limits and folders of the web server.</summary>
internal sealed record WebOptions
{
    /// <summary>Filee's data folder: settings, presets and downloaded engines (FILEE_DATA_DIR, as the app and CLI).</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Where each job keeps its uploads and results (FILEE_WEB_JOBS_DIR, default &lt;data&gt;/jobs).</summary>
    public required string JobsDirectory { get; init; }

    /// <summary>Largest request body, all files of one job together (FILEE_WEB_MAX_UPLOAD_MB, default 500).</summary>
    public long MaxUploadBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>Most files in one job (FILEE_WEB_MAX_FILES, default 50).</summary>
    public int MaxFilesPerJob { get; init; } = 50;

    /// <summary>Jobs converting at the same time; the others wait (FILEE_WEB_MAX_CONCURRENT_JOBS, default 2).</summary>
    public int MaxConcurrentJobs { get; init; } = 2;

    /// <summary>How long a job and its files are kept after it ends (FILEE_WEB_JOB_TTL_MINUTES, default 60).</summary>
    public TimeSpan JobLifetime { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>A job running longer than this is cancelled (FILEE_WEB_JOB_TIMEOUT_MINUTES, default 30).</summary>
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>When set, every /api request must carry it (FILEE_WEB_TOKEN); empty, the server is open.</summary>
    public string? Token { get; init; }

    public static WebOptions FromEnvironment()
    {
        var data = UserDataStore.DefaultDirectory;
        return new WebOptions
        {
            DataDirectory = data,
            JobsDirectory = Path.GetFullPath(Text("FILEE_WEB_JOBS_DIR") ?? Path.Combine(data, "jobs")),
            MaxUploadBytes = Number("FILEE_WEB_MAX_UPLOAD_MB", 500, 1, 100_000) * 1024L * 1024,
            MaxFilesPerJob = Number("FILEE_WEB_MAX_FILES", 50, 1, 10_000),
            MaxConcurrentJobs = Number("FILEE_WEB_MAX_CONCURRENT_JOBS", 2, 1, 64),
            JobLifetime = TimeSpan.FromMinutes(Number("FILEE_WEB_JOB_TTL_MINUTES", 60, 1, 7 * 24 * 60)),
            JobTimeout = TimeSpan.FromMinutes(Number("FILEE_WEB_JOB_TIMEOUT_MINUTES", 30, 1, 24 * 60)),
            Token = Text("FILEE_WEB_TOKEN"),
        };
    }

    private static string? Text(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int Number(string name, int fallback, int min, int max) =>
        int.TryParse(Text(name), out var value) ? Math.Clamp(value, min, max) : fallback;
}
