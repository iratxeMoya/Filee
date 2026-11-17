// The HTTP API under /api: formats and what they convert to, engines, and jobs (upload, progress, results).

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Web.Hosting;
using Filee.Web.Jobs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

namespace Filee.Web.Api;

internal static class FileeApi
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapFileeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", version = typeof(FileeApi).Assembly.GetName().Version?.ToString(3) }));

        api.MapGet("/formats", () => Results.Ok(FormatRegistry.Known
            .Where(f => f.Id != FormatRegistry.Folder)
            .Select(f => new FormatDto(f.Id, f.DisplayName, f.Category.ToString(), f.Extensions, f.Writable))));

        api.MapGet("/targets", Targets);

        api.MapGet("/engines", (WebConversionHost host) => Results.Ok(host.Catalog.Ordered.Select(c =>
        {
            var status = host.Catalog.StatusOf(c);
            return new EngineDto(c.Id, c.DisplayName, status.IsAvailable, status.Version,
                status.ReasonKey is null ? null : host.Texts[status.ReasonKey]);
        })));

        api.MapPost("/jobs", CreateJob);
        api.MapGet("/jobs/{id:guid}", (Guid id, JobStore store, WebConversionHost host) =>
            store.Find(id) is { } job ? Results.Ok(Describe(job, host)) : Results.NotFound());
        api.MapPost("/jobs/{id:guid}/cancel", (Guid id, JobStore store) =>
            store.Cancel(id) ? Results.NoContent() : Results.NotFound());
        api.MapDelete("/jobs/{id:guid}", (Guid id, JobStore store) =>
            store.Remove(id) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/jobs/{id:guid}/results/{index:int}", DownloadResult);
        api.MapGet("/jobs/{id:guid}/zip", DownloadAll);
    }

    /// <summary>
    /// GET /api/targets?ext=heic&amp;ext=png: what every one of these converts to now, and what with optional engines.
    /// </summary>
    private static IResult Targets(HttpRequest request, WebConversionHost host)
    {
        var extensions = request.Query["ext"].Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (extensions.Count == 0)
            return Results.BadRequest(new ErrorDto("Give at least one ext."));

        var sources = new List<SourceDto>();
        var unknown = new List<string>();
        foreach (var extension in extensions)
        {
            if (WebConversionHost.FindFormat(extension) is { } format)
                sources.Add(new SourceDto(extension, format.Id, format.DisplayName));
            else
                unknown.Add(extension);
        }

        var formats = sources.Select(s => s.Format).Distinct().Select(FormatRegistry.Get).ToList();
        if (formats.Count == 0)
            return Results.Ok(new TargetsDto(sources, unknown, [], []));

        var all = formats.Select(host.TargetsOf).ToList();
        var now = all.Select(t => t.Now).Aggregate((a, b) => a.Intersect(b).ToList());
        // Possible once engines are installed: every source converts to it, now or with an engine, and one needs one.
        var later = FormatRegistry.Known
            .Where(t => !now.Contains(t.Id) && all.All(s => s.Now.Contains(t.Id) || s.WithEngines.ContainsKey(t.Id)))
            .Select(t => new TargetDto(t.Id, t.DisplayName, t.Category.ToString(),
                all.SelectMany(s => s.WithEngines.GetValueOrDefault(t.Id) ?? []).Distinct().ToList()))
            .ToList();
        var ready = now.Select(FormatRegistry.Get).Select(t => new TargetDto(t.Id, t.DisplayName, t.Category.ToString(), [])).ToList();
        return Results.Ok(new TargetsDto(sources, unknown, ready, later));
    }

    /// <summary>POST /api/jobs (multipart): "files" (one or more), "to" (a format id or extension), optional "quality".</summary>
    private static async Task<IResult> CreateJob(HttpRequest request, JobStore store, WebConversionHost host, WebOptions options,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new ErrorDto("Send the files as multipart/form-data."));
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(options);
        }
        catch (InvalidDataException)
        {
            return TooLarge(options);
        }

        var files = form.Files.GetFiles("files").Where(f => f.Length > 0).ToList();
        if (files.Count == 0)
            return Results.BadRequest(new ErrorDto("No files were sent."));
        if (files.Count > options.MaxFilesPerJob)
            return Results.BadRequest(new ErrorDto($"At most {options.MaxFilesPerJob} files at once."));

        var to = form["to"].ToString();
        if (string.IsNullOrWhiteSpace(to) || WebConversionHost.FindFormat(to) is not { } target)
            return Results.BadRequest(new ErrorDto($"Unknown format '{to}'."));
        if (!target.Writable)
            return Results.BadRequest(new ErrorDto($"Filee reads {target.DisplayName} but can't write it."));

        var preset = new Preset { TargetFormat = target.Id };
        if (form["quality"].ToString() is { Length: > 0 } qualityText)
        {
            if (!int.TryParse(qualityText, out var quality) || quality is < 1 or > 100)
                return Results.BadRequest(new ErrorDto("Quality is a number from 1 to 100."));
            preset.Image.Quality = quality;
        }

        var job = await store.CreateAsync(files, preset, cancellationToken);
        return Results.Accepted($"/api/jobs/{job.Id}", Describe(job, host));
    }

    /// <summary>GET /api/jobs/{id}/results/{index}: one result as a download (a folder as a ZIP of it).</summary>
    private static async Task<IResult> DownloadResult(Guid id, int index, JobStore store, HttpContext context, CancellationToken cancellationToken)
    {
        if (store.Find(id) is not { } job)
            return Results.NotFound();
        var results = job.Results();
        if (index < 0 || index >= results.Count)
            return Results.NotFound();
        var path = results[index];
        if (File.Exists(path))
        {
            var name = Path.GetFileName(path);
            var type = ContentTypes.TryGetContentType(name, out var known) ? known : "application/octet-stream";
            return Results.File(path, type, name, enableRangeProcessing: true);
        }
        await WriteZipAsync(context, Path.GetFileName(path) + ".zip", [path], job, cancellationToken);
        return Results.Empty;
    }

    /// <summary>GET /api/jobs/{id}/zip: every result in one ZIP.</summary>
    private static async Task<IResult> DownloadAll(Guid id, JobStore store, HttpContext context, CancellationToken cancellationToken)
    {
        if (store.Find(id) is not { } job)
            return Results.NotFound();
        var results = job.Results();
        if (results.Count == 0)
            return Results.NotFound();
        await WriteZipAsync(context, $"filee-{job.Conversion.Preset.TargetFormat}.zip", results, job, cancellationToken);
        return Results.Empty;
    }

    /// <summary>Streams <paramref name="paths"/> (files, and folders with what they hold) as a ZIP.</summary>
    private static async Task WriteZipAsync(HttpContext context, string name, List<string> paths, WebJob job, CancellationToken cancellationToken)
    {
        if (context.Features.Get<IHttpBodyControlFeature>() is { } body)
            body.AllowSynchronousIO = true; // ZipArchive writes its central directory synchronously
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = name,
        }.ToString();

        await using var archive = await ZipArchive.CreateAsync(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8, cancellationToken);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            List<(string Entry, string File)> files = File.Exists(path)
                ? [(Path.GetFileName(path), path)]
                : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(job.IsOwnOutput)
                    .Select(f => (Path.Combine(Path.GetFileName(path), Path.GetRelativePath(path, f)).Replace('\\', '/'), f))
                    .ToList();
            foreach (var (entryName, file) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(UploadNames.Unique(entryName, taken), CompressionLevel.Fastest);
                await using var entryStream = await entry.OpenAsync(cancellationToken);
                await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await source.CopyToAsync(entryStream, cancellationToken);
            }
        }
    }

    private static IResult TooLarge(WebOptions options) =>
        Results.Json(new ErrorDto($"The files are larger than {options.MaxUploadBytes / (1024 * 1024)} MB together."),
            statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>The job as the page shows it.</summary>
    internal static JobDto Describe(WebJob job, WebConversionHost host)
    {
        var conversion = job.Conversion;
        var results = job.Results();
        var files = conversion.Files.Select(file =>
        {
            List<ResultDto> outputs = file.State == FileState.Done
                ? file.Outputs.Select(o => results.IndexOf(o)).Where(i => i >= 0).Select(i => Result(results, i)).ToList()
                : [];
            string? error = null;
            IReadOnlyList<string>? needs = null;
            if (file.State is FileState.Failed or FileState.Cancelled)
            {
                error = file.ErrorKey is null ? "Conversion failed." : host.Texts[file.ErrorKey];
                if (!string.IsNullOrWhiteSpace(file.ErrorDetail))
                    error += $" ({file.ErrorDetail.Replace(job.Folder, "", StringComparison.Ordinal)})";
                if (file.ErrorKey == "error.no_route" && file.FailedRoute is { } route)
                    needs = host.Optional.MissingPackages(route);
            }
            return new FileDto(
                job.UploadNames.GetValueOrDefault(file.SourcePath) ?? Path.GetFileName(file.SourcePath),
                job.CancelledBeforeStart ? "cancelled" : Camel(file.State.ToString()),
                Math.Round(file.Progress, 3),
                outputs,
                error,
                needs,
                file.HasNotice ? host.Texts.Notice(file) : null,
                file.MissingFonts);
        }).ToList();

        var target = FormatRegistry.FindById(conversion.Preset.TargetFormat);
        var state = job.CancelledBeforeStart ? "cancelled" : Camel(conversion.State.ToString());
        return new JobDto(job.Id, state, job.FinishedUtc is not null, Math.Round(conversion.Progress, 3),
            conversion.Preset.TargetFormat, target?.DisplayName ?? conversion.Preset.TargetFormat, files, results.Count);
    }

    private static ResultDto Result(List<string> results, int index)
    {
        var path = results[index];
        var folder = Directory.Exists(path);
        return new ResultDto(index, Path.GetFileName(path) + (folder ? ".zip" : ""), folder ? null : new FileInfo(path).Length, folder);
    }

    private static string Camel(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}

internal sealed record ErrorDto(string Error);
internal sealed record FormatDto(string Id, string Name, string Category, IReadOnlyList<string> Extensions, bool Writable);
internal sealed record EngineDto(string Id, string Name, bool Available, string? Version, string? Reason);
internal sealed record SourceDto(string Extension, string Format, string Name);
internal sealed record TargetDto(string Id, string Name, string Category, IReadOnlyList<string> NeedsEngines);
internal sealed record TargetsDto(IReadOnlyList<SourceDto> Sources, IReadOnlyList<string> Unknown, IReadOnlyList<TargetDto> Targets, IReadOnlyList<TargetDto> WithEngines);
internal sealed record ResultDto(int Index, string Name, long? Size, bool Folder);
internal sealed record FileDto(string Name, string State, double Progress, IReadOnlyList<ResultDto> Results, string? Error,
    IReadOnlyList<string>? NeedsEngines, string? Notice, IReadOnlyList<string> MissingFonts);
internal sealed record JobDto(Guid Id, string State, bool Finished, double Progress, string Target, string TargetName,
    IReadOnlyList<FileDto> Files, int ResultCount);
