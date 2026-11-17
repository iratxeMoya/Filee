using Filee.Web.Jobs;

namespace Filee.Web.Tests;

public sealed class UploadNamesTests
{
    [Theory]
    [InlineData("photo.png", "photo.png")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData("C:\\Users\\me\\report.docx", "report.docx")]
    [InlineData("a:b*c?d\"e<f>g|h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("tab\there.png", "tab_here.png")]
    [InlineData("  .hidden.  ", "hidden")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("café ñ 写真.jpg", "café ñ 写真.jpg")]
    public void Sanitize_keeps_a_plain_file_name(string? name, string expected) =>
        Assert.Equal(expected, UploadNames.Sanitize(name));

    [Fact]
    public void Sanitize_cuts_long_names_but_keeps_the_extension()
    {
        var name = UploadNames.Sanitize(new string('x', 300) + ".tar.gz");

        Assert.Equal(120, name.Length);
        Assert.EndsWith("x.gz", name);
    }

    [Fact]
    public void Unique_numbers_repeated_names_ignoring_case()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("a.png", UploadNames.Unique("a.png", taken));
        Assert.Equal("A (2).png", UploadNames.Unique("A.png", taken));
        Assert.Equal("a (3).png", UploadNames.Unique("a.png", taken));
        Assert.Equal("b.png", UploadNames.Unique("b.png", taken));
    }
}
