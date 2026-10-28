using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A Case Structure on an error cluster stores two selector ranges that both START at INT_MIN -
/// `No Error` is ..INT_MIN, `Error` the whole line - and LabVIEW wants them in that order. The
/// composer sorted ranges by their start alone, so the tie fell to the authored frame order: an
/// `Error` frame written first put the whole-line range first, and LabVIEW 2020 loaded the VI broken,
/// "Selector values are not unique / No case for some selector values", while the composer answered
/// ok (2026-10-28; the same two errors broke a createResources member on LabVIEW 2026, 2026-10-26,
/// most likely this: its AIXML was not kept).
/// The reader cannot show it - both orders export the same frames - so the stored ranges are read
/// off the heap. Measured after the fix on LabVIEW 2020: both orders, with and without a Default,
/// execState 1, and a run takes the Error frame for an error and the No Error frame for no error
/// and for a warning. SKIPS where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewErrorCaseOrderTests
{
    private const long IntMin = int.MinValue, IntMax = int.MaxValue;

    private static string Probe(string first, string second) => $$"""
        <VI _name="Error Case Probe.vi" description="An error-cluster case structure: which frame runs.">
          <Control _name="error in" conIdx="4" connection="recommended" type="cluster{bool.status,int32.code,string.source}" value="" outputs="value:4201.v" uid="4201" uid_parent="root"/>
          <Structure _name="Case Structure" selectin="4201.v" uid="4210" uid_parent="root">
            <Tunnel _id="Out1" outputs="value:4220.which" uid="4220" uid_parent="4210"/>
            <CaseFrame selector="{{first}}" selectout="" uid="4211" uid_parent="4210">
              <Constant _name="c1" outputs="value:4213.v" type="string" uid="4213" uid_parent="4211" value="first"/>
              <Tunnel _id="Out1" inputs="value:4213.v" uid="4214" uid_parent="4211"/>
            </CaseFrame>
            <CaseFrame selector="{{second}}" selectout="" uid="4212" uid_parent="4210">
              <Constant _name="c2" outputs="value:4215.v" type="string" uid="4215" uid_parent="4212" value="second"/>
              <Tunnel _id="Out1" inputs="value:4215.v" uid="4216" uid_parent="4212"/>
            </CaseFrame>
          </Structure>
          <Indicator _name="which" conIdx="11" connection="recommended" type="string" value="" inputs="value:4220.which" uid="4230" uid_parent="root"/>
        </VI>
        """;

    [Theory]
    [InlineData("No Error", "Error", 0, 1)]
    [InlineData("Error", "No Error", 1, 0)]
    [InlineData("Error", "No Error, Default", 1, 0)]
    public async Task An_error_case_keeps_the_No_Error_range_first_whatever_order_its_frames_are_written_in(
        string first, string second, int noErrorFrame, int errorFrame)
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "errcase-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "x"));
        try
        {
            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, "Error Case Probe.vi");
            await File.WriteAllTextAsync(xml, Probe(first, second));
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);
            Assert.True(File.Exists(vi), composed.Message);
            var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
                ["-x", "-i", vi, "-m", Path.Combine(dir, "x", "ErrorCaseProbe.xml")], 120, default);
            Assert.True(extract.ExitCode == 0, extract.StdErr);
            var heap = XElement.Load(Directory.GetFiles(Path.Combine(dir, "x"), "*_BDHb.xml").Single());

            var ranges = heap.Descendants("SelectRangeArray32").Single().Elements()
                .Select(r => (Start: (long)r.Element("start")!, End: (long)r.Element("end")!,
                              Frame: (int)r.Element("diagramIdx")!))
                .ToList();
            Assert.Equal([(IntMin, IntMin, noErrorFrame), (IntMin, IntMax, errorFrame)], ranges);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
