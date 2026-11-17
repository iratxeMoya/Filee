// Which optional engine downloads a conversion would need, when the engines installed now can't do it. The same rule
// as the command line's (Cli.OptionalEngineRoutes in src/Filee.Cli/Cli.cs).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Web.Hosting;

internal sealed class OptionalEngineRoutes
{
    private readonly ConverterCatalog _catalog;
    private readonly RoutePlanner _installed;
    private readonly RoutePlanner _optional;
    private readonly Dictionary<string, IReadOnlyList<string>> _packages;

    public OptionalEngineRoutes(ConverterCatalog catalog)
    {
        _catalog = catalog;
        _packages = EngineDownloads.Packages.SelectMany(p => p.ConverterIds.Select(id => (Converter: id, Package: p.Id)))
            .GroupBy(p => p.Converter).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Package).ToList());
        _installed = catalog.CreatePlanner();
        _optional = catalog.CreatePlanner(catalog.All
            .Where(c => _packages.ContainsKey(c.Id) && catalog.StatusOf(c).ReasonKey != "engine.reason.unsupported_platform")
            .Select(c => c.Id).ToList());
    }

    /// <summary>The engine packages <paramref name="request"/> needs, or null when it works now or never will.</summary>
    public IReadOnlyList<string>? MissingPackages(RouteRequest request)
    {
        if (Plan(_installed) is not null || Plan(_optional) is not { } route)
            return null;
        var packages = route.Steps.Where(s => !_catalog.StatusOf(s.Converter).IsAvailable)
            .SelectMany(s => _packages[s.Converter.Id]).Distinct().ToList();
        return packages.Count > 0 ? packages : null;

        Filee.Core.Conversion.Route? Plan(RoutePlanner planner) => planner.Plan(request.Source, request.Target, frames: request.Frames,
            shaping: request.Shaping, motionPhoto: request.MotionPhoto, livePhoto: request.LivePhoto, ocr: request.Ocr, metadata: request.Metadata);
    }
}
