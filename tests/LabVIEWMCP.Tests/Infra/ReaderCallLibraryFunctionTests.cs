using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// vi2aixml.py reads a CALL LIBRARY FUNCTION NODE as NI's export writes it (2026-10-30): a Node
/// listing every terminal that is neither hidden nor Void, wired or not, in terminal order - `path
/// in`/`path out` only where the path is set on the diagram, the return value on the output side
/// only. It was the largest unread node (85 in 80 VIs) and its 109 lost wires the largest edge gap;
/// all 67 such nodes in the cached NI exports now match by name, order and wiring.
///
/// The fixtures are NI's own shipped VIs, so each test SKIPS where the VI is not installed, and
/// where the bundled interpreter is missing, as the other reader tests do.
/// </summary>
public sealed class ReaderCallLibraryFunctionTests
{
    private static string? Installed(params string[] parts) =>
        Directory.Exists(@"C:\Program Files (x86)\National Instruments")
            ? Directory.EnumerateDirectories(@"C:\Program Files (x86)\National Instruments", "LabVIEW 20*")
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine([d, .. parts]))
                .FirstOrDefault(File.Exists)
            : null;

    private static async Task<XElement?> ClfAsync(string? vi, string? parameter = null)
    {
        if (vi is null || AixmlComposer.Locate(out _) is not { } setup) return null;
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output).Descendants("Node")
                .First(n => (string?)n.Attribute("_name") == "Call Library Function Node" &&
                            (parameter is null || ((string?)n.Attribute("inputs") ?? "").Contains(parameter + ":")));
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    // "name:net,name:net" -> (name, wired?)
    private static List<(string Name, bool Wired)> Ports(XElement node, string attr) =>
        ((string?)node.Attribute(attr) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (p[..p.LastIndexOf(':')], p[(p.LastIndexOf(':') + 1)..].Length > 0)).ToList();

    [Fact]
    public async Task A_void_function_lists_its_parameters_on_both_sides_and_no_return()
    {
        if (await ClfAsync(Installed("examples", "Connectivity", "Libraries and Executables", "DLL Calling VIs", "ANSIint.vi"))
            is not { } node) return;

        Assert.Equal([("error in (no error)", false), ("input", true), ("output", false)], Ports(node, "inputs"));
        Assert.Equal([("error out", true), ("input", false), ("output", true)], Ports(node, "outputs"));
    }

    [Fact]
    public async Task A_return_value_is_an_output_only_and_a_hidden_path_is_not_listed()
    {
        if (await ClfAsync(Installed("vi.lib", "Utility", "High Resolution Polling Wait.vi"), "lpPerformanceCount") is not { } node) return;

        Assert.Equal(["error in (no error)", "lpPerformanceCount"], Ports(node, "inputs").Select(p => p.Name));
        Assert.Equal(["error out", "return value", "lpPerformanceCount"], Ports(node, "outputs").Select(p => p.Name));
    }

    [Fact]
    public async Task A_path_set_on_the_diagram_is_listed_first()
    {
        if (await ClfAsync(Installed("examples", "File IO", "TDMS", "Check TDMS File for Fragmentation.vi")) is not { } node) return;

        var ins = Ports(node, "inputs");
        Assert.Equal(("path in", true), ins[0]);
        Assert.Equal("error in (no error)", ins[1].Name);
        Assert.Equal(["path out", "error out", "return type"], Ports(node, "outputs").Take(3).Select(p => p.Name));
    }
}
