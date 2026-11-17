// One conversion asked for from the browser: its folder (in/ for the uploads, out/ for the results) and the Filee
// job that converts it.

using Filee.Core.Conversion;

namespace Filee.Web.Jobs;

internal sealed class WebJob
{
    private int _cancelledBeforeStart;

    public WebJob(Guid id, string folder, ConversionJob conversion, IReadOnlyDictionary<string, string> uploadNames)
    {
        Id = id;
        Folder = folder;
        Conversion = conversion;
        UploadNames = uploadNames;
    }

    /// <summary>Random, so a job can't be guessed from another one's address.</summary>
    public Guid Id { get; }

    /// <summary>The job's own folder, &lt;jobs&gt;/&lt;id&gt;.</summary>
    public string Folder { get; }

    public string InputFolder => Path.Combine(Folder, "in");

    public string OutputFolder => Path.Combine(Folder, "out");

    public ConversionJob Conversion { get; }

    /// <summary>Saved upload path → the name the browser gave it.</summary>
    public IReadOnlyDictionary<string, string> UploadNames { get; }

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    /// <summary>When the job ended (converted, failed or cancelled); null while it waits or runs.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Waits for the slot, runs the conversion and ends when the job does.</summary>
    public Task Completion { get; set; } = Task.CompletedTask;

    /// <summary>Cancelled while it waited for a free slot: Filee's job never ran.</summary>
    public bool CancelledBeforeStart => Volatile.Read(ref _cancelledBeforeStart) == 1;

    public void MarkCancelledBeforeStart() => Interlocked.Exchange(ref _cancelledBeforeStart, 1);

    /// <summary>
    /// Whether <paramref name="path"/> is inside the job's out/ folder: only those are handed to the browser.
    /// </summary>
    public bool IsOwnOutput(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputFolder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>
    /// The results to download, in order: what each converted file became, only once it is done and only inside out/.
    /// </summary>
    public List<string> Results() =>
        Conversion.Files.Where(f => f.State == FileState.Done)
            .SelectMany(f => f.Outputs)
            .Where(p => IsOwnOutput(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
