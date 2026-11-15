using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// vi2aixml.py names two kinds of call target as NI's export does (2026-10-30, measured over 2011
/// cached NI exports - 459 call targets differed, and these two causes were all of them):
///
/// - A CHANNEL wire's endpoint. LabVIEW clones one library per channel type into
///   `&lt;extravilib&gt;\ChannelInstances` (`Tag-bool.lvlib`), the link record names that clone, and NI's
///   export names the template it came from (`Tag.lvlib:Write.vi`) - 452 of the 459.
/// - A library NESTED in the caller's own, saved relative to it (LinkSaveFlag 2) with a name of two
///   parts: DQMH's `Clone Registration.lvlib:Init.vi`, which NI exports with the module's library in
///   front - the other 7. Flag 2 used to be applied to one-part names only.
///
/// The fixtures are NI's own shipped examples, so each test SKIPS where the example is not installed,
/// and where the bundled interpreter is missing, as the other reader tests do.
/// </summary>
public sealed class ReaderChannelAndNestedLibraryCallTests
{
    private static string? Example(params string[] parts) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine([d, "examples", .. parts]))
                .FirstOrDefault(File.Exists)
            : null;

    private static async Task<XElement?> ReadAsync(string? vi)
    {
        if (vi is null || AixmlComposer.Locate(out _) is not { } setup) return null;
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    private static List<string> Targets(XElement vi) =>
        vi.Descendants("Call").Select(c => AixmlUnescape((string?)c.Attribute("target") ?? "")).ToList();

    // the reader writes a qualified name's colon raw today, NI writes it \3A; compare unescaped
    private static string AixmlUnescape(string s) => s.Replace(@"\3A", ":").Replace(@"\5C", @"\");

    [Fact]
    public async Task A_channel_endpoint_is_named_after_its_template_not_its_typed_clone()
    {
        if (await ReadAsync(Example("Channels", "Accumulator Tag", "Random Series.vi")) is not { } vi) return;

        var targets = Targets(vi);
        Assert.Contains("Tag.lvlib:Write.vi", targets);
        Assert.DoesNotContain(targets, t => t.StartsWith("Tag-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_library_nested_in_the_callers_own_carries_the_outer_library()
    {
        if (await ReadAsync(Example("DQMH Consortium", "DQMH Fundamentals - Thermal Chamber", "Libraries",
                "Device Under Test_Cloneable_DQMH", "Start Module.vi")) is not { } vi) return;

        var targets = Targets(vi);
        Assert.Contains("Device Under Test_Cloneable_DQMH.lvlib:Clone Registration.lvlib:Init.vi", targets);
        Assert.DoesNotContain("Clone Registration.lvlib:Init.vi", targets);
    }
}
