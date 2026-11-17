// The conversion pipeline for the web server: the settings and presets in the data folder, every engine (bundled
// ones next to the executable, downloaded ones in <data>/engines, others on PATH) and one job queue shared by every
// request. Mirrors CliHost in Filee.Cli, without the Recycle Bin: the server never replaces an original.

using System.Collections.Concurrent;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Core.Web;
using Filee.Engines;
using Filee.Engines.Infrastructure;

namespace Filee.Web.Hosting;

/// <summary>What a source format converts to: now, and once optional engines are installed.</summary>
internal sealed record FormatTargets(IReadOnlyList<string> Now, IReadOnlyDictionary<string, IReadOnlyList<string>> WithEngines);

internal sealed class WebConversionHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FormatTargets> _targets = new();
    private readonly Lazy<OptionalEngineRoutes> _optional;

    private WebConversionHost(UserDataStore store, ConverterCatalog catalog, JobQueue queue)
    {
        Store = store;
        Catalog = catalog;
        Queue = queue;
        _optional = new Lazy<OptionalEngineRoutes>(() => new OptionalEngineRoutes(catalog));
    }

    public UserDataStore Store { get; }
    public ConverterCatalog Catalog { get; }
    public JobQueue Queue { get; }
    public ILocalizer Texts { get; } = new EnglishTexts();

    /// <summary>Tells which optional engine packages a failed route would need.</summary>
    public OptionalEngineRoutes Optional => _optional.Value;

    public static WebConversionHost Create(string dataDirectory, ILoggerFactory loggers)
    {
        var store = new UserDataStore(dataDirectory);
        store.Load();
        ProxySetting.Use(() => store.Settings.Proxy);
        EngineEnvironment.OwnCopies = new Dictionary<string, string>(store.Settings.EngineOwnCopies);
        EngineEnvironment.LibreOfficeForSlides = store.Settings.LibreOfficeForSlides;
        EngineEnvironment.LibreOfficeForHwp = store.Settings.LibreOfficeForHwp;
        EngineEnvironment.LibreOfficeForDocuments = store.Settings.LibreOfficeForDocuments;
        EngineEnvironment.LibreOfficeForSheets = store.Settings.LibreOfficeForSheets;
        var converters = EngineRegistry.CreateAll(new EngineEnvironment(store.Directory), loggers);
        var catalog = new ConverterCatalog(converters) { Priority = store.Settings.EnginePriority };
        var queue = new JobQueue(catalog, EngineRegistry.FindPdfMerger(converters), loggers.CreateLogger<JobQueue>(),
            combiner: EngineRegistry.FindFileCombiner(converters), tiffMerger: EngineRegistry.FindTiffMerger(converters),
            mediaJoiner: EngineRegistry.FindMediaJoiner(converters), moveToTrash: null);
        return new WebConversionHost(store, catalog, queue);
    }

    /// <summary>
    /// The formats <paramref name="source"/> converts to (never itself), checked once per format: engines don't change
    /// while the server runs.
    /// </summary>
    public FormatTargets TargetsOf(FileFormat source) => _targets.GetOrAdd(source.Id, _ =>
    {
        var planner = Catalog.CreatePlanner();
        var candidates = FormatRegistry.Known.Where(t => t.Writable && t.Id != source.Id && t.Id != FormatRegistry.Folder).ToList();
        var now = candidates.Where(t => planner.CanConvert(source.Id, t.Id)).Select(t => t.Id).ToList();
        var later = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var target in candidates.Where(t => !now.Contains(t.Id)))
            if (Optional.MissingPackages(new RouteRequest(source.Id, target.Id)) is { } packages)
                later[target.Id] = packages;
        return new FormatTargets(now, later);
    });

    /// <summary>A format by id ("jpg") or by extension ("jpeg", ".jpg").</summary>
    public static FileFormat? FindFormat(string text)
    {
        var key = text.Trim().TrimStart('.').ToLowerInvariant();
        return FormatRegistry.FindById(key) ?? FormatRegistry.FindByExtension(key);
    }

    public ValueTask DisposeAsync() => Queue.DisposeAsync();
}
