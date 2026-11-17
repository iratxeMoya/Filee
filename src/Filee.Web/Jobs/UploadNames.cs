// Turns a name sent by a browser into a plain file name that is safe to create in a job's in/ folder.

using System.Text;

namespace Filee.Web.Jobs;

internal static class UploadNames
{
    private const int MaxLength = 120;
    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// The last part of <paramref name="name"/> (no folders), without control or reserved characters, at most 120
    /// characters with its extension kept; "file" when nothing is left.
    /// </summary>
    public static string Sanitize(string? name)
    {
        var text = (name ?? "").Replace('\\', '/');
        text = text[(text.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsControl(c) || Array.IndexOf(Forbidden, c) >= 0 ? '_' : c);
        var clean = builder.ToString().Trim().Trim('.').Trim();
        if (clean.Length == 0)
            return "file";
        if (clean.Length <= MaxLength)
            return clean;
        var extension = Path.GetExtension(clean);
        if (extension.Length > 20)
            extension = "";
        return clean[..(MaxLength - extension.Length)].TrimEnd() + extension;
    }

    /// <summary><paramref name="name"/>, or "name (2).ext", "name (3).ext", … when <paramref name="taken"/> has it.</summary>
    public static string Unique(string name, ISet<string> taken)
    {
        if (taken.Add(name))
            return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (taken.Add(candidate))
                return candidate;
        }
    }
}
