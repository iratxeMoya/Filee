// The jobs of the web server: saves the uploads of each one in its own folder, runs them on the shared queue (a few at
// a time) and forgets them, with their files, some time after they end.

using System.Collections.Concurrent;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Web.Hosting;

namespace Filee.Web.Jobs;

internal sealed class JobStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, WebJob> _jobs = new();
    private readonly WebConversionHost _host;
    private readonly WebOptions _options;
    private readonly ILogger<JobStore> _log;
    private readonly SemaphoreSlim _slots;

    public JobStore(WebConversionHost host, WebOptions options, ILogger<JobStore> log)
    {
        _host = host;
        _options = options;
        _log = log;
        _slots = new SemaphoreSlim(options.MaxConcurrentJobs, options.MaxConcurrentJobs);
        ClearLeftovers();
    }

    public WebJob? Find(Guid id) => _jobs.GetValueOrDefault(id);

    /// <summary>
    /// Saves <paramref name="uploads"/> into a new job folder and starts converting them with <paramref name="preset"/>
    /// (a copy whose results go to the job's out/ folder).
    /// </summary>
    public async Task<WebJob> CreateAsync(IReadOnlyList<IFormFile> uploads, Preset preset, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(_options.JobsDirectory, id.ToString("N"));
        var input = Path.Combine(folder, "in");
        var output = Path.Combine(folder, "out");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        try
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = new List<string>(uploads.Count);
            foreach (var upload in uploads)
            {
                var path = Path.Combine(input, UploadNames.Unique(UploadNames.Sanitize(upload.FileName), taken));
                await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await upload.CopyToAsync(target, cancellationToken);
                names[path] = upload.FileName;
                paths.Add(path);
            }

            var own = _host.Store.Settings.WithDefaultOutput(preset).Clone();
            own.Output.Location = OutputLocation.CustomFolder;
            own.Output.CustomFolder = output;
            own.Output.ReplaceOriginal = false;
            own.Output.TrashOriginalAfterConvert = false;
            var conversion = new ConversionJob(paths, own, _host.Texts.DisplayName(own));
            var job = new WebJob(id, folder, conversion, names);
            _jobs[id] = job;
            job.Completion = RunAsync(job);
            return job;
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    /// <summary>Cancels a job that waits or runs; its files are kept until it is removed or expires.</summary>
    public bool Cancel(Guid id)
    {
        if (Find(id) is not { } job)
            return false;
        job.Conversion.Cancel();
        return true;
    }

    /// <summary>Cancels a job and deletes its files once it has stopped.</summary>
    public bool Remove(Guid id)
    {
        if (!_jobs.TryRemove(id, out var job))
            return false;
        job.Conversion.Cancel();
        _ = job.Completion.ContinueWith(_ => TryDelete(job.Folder), TaskScheduler.Default);
        return true;
    }

    /// <summary>Forgets the jobs that ended longer ago than the lifetime and deletes their folders.</summary>
    public void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var job in _jobs.Values)
        {
            if (job.FinishedUtc is { } finished && now - finished > _options.JobLifetime && _jobs.TryRemove(job.Id, out _))
            {
                TryDelete(job.Folder);
                _log.LogInformation("Job {Id} expired and was deleted", job.Id);
            }
        }
    }

    private async Task RunAsync(WebJob job)
    {
        try
        {
            await _slots.WaitAsync(job.Conversion.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelledBeforeStart();
            job.FinishedUtc = DateTime.UtcNow;
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(_options.JobTimeout);
            using var _ = timeout.Token.Register(job.Conversion.Cancel);
            _log.LogInformation("Job {Id}: {Count} file(s) to {Target}", job.Id, job.Conversion.Files.Count, job.Conversion.Preset.TargetFormat);
            await _host.Queue.RunAsync(job.Conversion);
            _log.LogInformation("Job {Id} ended: {State}", job.Id, job.Conversion.State);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {Id} failed", job.Id);
        }
        finally
        {
            job.FinishedUtc = DateTime.UtcNow;
            _slots.Release();
        }
    }

    /// <summary>Job folders from an earlier run: nothing points at them any more.</summary>
    private void ClearLeftovers()
    {
        Directory.CreateDirectory(_options.JobsDirectory);
        foreach (var folder in Directory.EnumerateDirectories(_options.JobsDirectory))
            if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _))
                TryDelete(folder);
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not delete {Folder}", folder);
        }
    }

    public void Dispose()
    {
        foreach (var job in _jobs.Values)
            job.Conversion.Cancel();
        _slots.Dispose();
    }
}

/// <summary>Sweeps expired jobs every minute.</summary>
internal sealed class JobCleanupService(JobStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            store.Sweep();
    }
}
