// =================================================================================================
//  ComposerDefaultPaneTests - a composed NEW VI gets the connector pane LabVIEW would give it: the
//  DefaultConPane of the LabVIEW in use, else 4815, not the composer skeleton's 4833.
// =================================================================================================
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Found on LabVIEW 2020, 2026-11-03: none of the station's LabVIEW.ini files carries
/// DefaultConPane, so `lvai_connector_pane` answered 4815 - and the composer wrote every VI on its
/// skeleton's 4833, the setting of the station its parts were cut on. A pane authored by the tool's
/// numbers came back with three violations. The server now passes the pattern
/// (<see cref="StationPaneDefault.ForNewVi"/>, `aixml2vi.py --default-pane`). The composer tests
/// run the bundled interpreter and SKIP without it.
/// </summary>
public sealed class ComposerDefaultPaneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ComposerDefaultPane", Path.GetRandomFileName());

    public ComposerDefaultPaneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private LabViewContext ContextWithIni(string? iniText)
    {
        var root = Path.Combine(_dir, "LabVIEW 20xx");
        Directory.CreateDirectory(root);
        var ini = Path.Combine(root, "LabVIEW.ini");
        if (iniText is not null) File.WriteAllText(ini, iniText);
        return new LabViewContext(Path.Combine(root, "LabVIEW.exe"), 20, 0, 32, null, ini, null, "test",
                                  true, [], [], []);
    }

    [Fact]
    public void A_new_VI_gets_the_INI_s_DefaultConPane() =>
        Assert.Equal(4833, StationPaneDefault.ForNewVi(ContextWithIni("[LabVIEW]\nDefaultConPane=\"4833\"\n")));

    [Fact]
    public void Without_the_key_a_new_VI_gets_LabVIEW_s_own_4815() =>
        Assert.Equal(4815, StationPaneDefault.ForNewVi(ContextWithIni("[LabVIEW]\nFancyFPTerms=\"False\"\n")));

    [Fact]
    public void With_no_INI_at_all_the_composer_keeps_its_own() =>
        Assert.Null(StationPaneDefault.ForNewVi(ContextWithIni(null)));

    [Fact]
    public void A_pref_file_is_the_INI_LabVIEW_reads()
    {
        var pref = Path.Combine(_dir, "project.ini");
        File.WriteAllText(pref, "[LabVIEW]\nDefaultConPane=4834\n");
        var context = ContextWithIni("[LabVIEW]\nDefaultConPane=4833\n") with { PrefFile = pref, IniFile = pref };
        Assert.Equal(4834, StationPaneDefault.ForNewVi(context));
    }

    private const string Err = "cluster{bool.status,int32.code,string.source}";

    private static string Doc(int errorOutConIdx, string tail = "") => $$"""
        <VI _name="Pane Probe.vi" description="Passes the error through.">
          <Control _name="error in" conIdx="8" connection="recommended" outputs="value:4201.v" type="{{Err}}" uid="4201" uid_parent="root" value="[false,0,]"/>
          <Indicator _name="error out" conIdx="{{errorOutConIdx}}" connection="recommended" inputs="value:4201.v" type="{{Err}}" uid="4202" uid_parent="root" value="[false,0,]"/>
        </VI>{{tail}}
        """;

    /// <summary>The composed VI's conId, or null where the bundled interpreter is missing.</summary>
    private async Task<int?> ComposedPaneAsync(string document, params string[] flags)
    {
        var setup = AixmlComposer.Locate(out _);
        var bundle = PyLabview.Locate();
        if (setup is null || bundle is null) return null;
        var case_ = Path.Combine(_dir, Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(case_, "x"));
        var xml = Path.Combine(case_, "probe.xml");
        var vi = Path.Combine(case_, "Pane Probe.vi");
        await File.WriteAllTextAsync(xml, document);
        var run = await PyLabview.RunAsync(bundle, Path.Combine(setup.ComposerDirectory, "aixml2vi.py"),
            [.. flags, xml, vi], 300, default);
        Assert.True(run.ExitCode == 0 && File.Exists(vi), run.StdErr);
        var extract = await PyLabview.RunAsync(bundle, bundle.ReadRsrcPy,
            ["-x", "-i", vi, "-m", Path.Combine(case_, "x", "PaneProbe.xml")], 120, default);
        Assert.True(extract.ExitCode == 0, extract.StdErr);
        var fp = XElement.Load(Directory.GetFiles(Path.Combine(case_, "x"), "*_FPHb.xml").Single());
        return int.Parse(fp.Descendants("conPane").Single().Element("conId")!.Value);
    }

    [Fact]
    public async Task The_default_pane_is_the_one_a_new_VI_gets()
    {
        if (await ComposedPaneAsync(Doc(0), "--default-pane", "4815") is not { } pane) return;
        Assert.Equal(4815, pane);
        Assert.Equal(4833, await ComposedPaneAsync(Doc(0)));          // no flag: the skeleton's, as before
    }

    /// <summary>
    /// A document authored against 4833 - every helper, error out at 15 - overflows 4815's twelve
    /// slots and keeps 4833, the smallest pattern that holds it, not 4834.
    /// </summary>
    [Fact]
    public async Task A_conIdx_past_the_default_takes_the_smallest_pattern_that_holds_it()
    {
        if (await ComposedPaneAsync(Doc(15), "--default-pane", "4815") is not { } pane) return;
        Assert.Equal(4833, pane);
    }

    [Fact]
    public async Task A_pane_the_document_names_wins_over_the_default()
    {
        if (await ComposedPaneAsync(Doc(0, "\n<?lvmcp pane=\"4834\"?>"), "--default-pane", "4815") is not { } pane) return;
        Assert.Equal(4834, pane);
    }
}
