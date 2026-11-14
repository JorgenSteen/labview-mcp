// =============================================================================
// ComposerLibraryMemberTests.cs - a VI composed over a path a library lists stays
// its member: the bare-named document is composed under the owner's qualified name.
// =============================================================================
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A field project regenerated library members with a bare `_name` and got LOOSE VIs while the
/// .lvlib still listed them (2026-11). The composer writes the membership (LIBN) only for a
/// qualified name, so the server qualifies a bare one when exactly one owner lists the target.
/// Composed for real with the bundled pylabview and read back from the file's own LIBN; SKIPS
/// where the interpreter or the composer is missing.
/// </summary>
public sealed class ComposerLibraryMemberTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("composer-member").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Document = """
        <VI _name="Clean Up View.vi" description="x">
          <Control _name="input" type="string" uid="4201" uid_parent="root" value="abc" outputs="value:4201.value" />
          <Node _name="String Length" uid="4202" uid_parent="root" inputs="string:4201.value" outputs="length:4202.length" />
          <Indicator _name="length" type="int32" uid="4203" uid_parent="root" value="0" inputs="value:4202.length" />
        </VI>
        """;

    private async Task<(AixmlComposer.Result Result, string Vi)?> ComposeAsync(string folder, string document)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null || PyLabview.Locate() is null) return null;
        Directory.CreateDirectory(folder);
        var xml = Path.Combine(_dir, Path.GetFileName(folder) + ".xml");
        await File.WriteAllTextAsync(xml, document);
        var vi = Path.Combine(folder, "Clean Up View.vi");
        return (await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default), vi);
    }

    [Fact]
    public async Task A_bare_named_vi_over_a_listed_path_is_composed_as_the_librarys_member()
    {
        var folder = Path.Combine(_dir, "member");
        Directory.CreateDirectory(folder);
        File.Copy(OwnerHousekeepingTests.Fixture("Weather Monitor Views.lvlib"),
                  Path.Combine(folder, "Weather Monitor Views.lvlib"));

        var composed = await ComposeAsync(folder, Document);
        if (composed is null) return;
        var (result, vi) = composed.Value;

        Assert.True(result.Written, result.Message);
        Assert.Equal(["Weather Monitor Views.lvlib"], ViFile.Owners(vi));
        Assert.Contains(result.Report, r => r.Contains("is listed by Weather Monitor Views.lvlib", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_dir, "*.member-qualified.xml"));   // the copy is cleaned up
    }

    /// <summary>The control arm: the same document with no library beside it stays loose.</summary>
    [Fact]
    public async Task The_same_vi_with_no_owner_listing_it_stays_loose()
    {
        var composed = await ComposeAsync(Path.Combine(_dir, "loose"), Document);
        if (composed is null) return;
        var (result, vi) = composed.Value;

        Assert.True(result.Written, result.Message);
        Assert.Empty(ViFile.Owners(vi));
        Assert.DoesNotContain(result.Report, r => r.Contains("is listed by", StringComparison.Ordinal));
    }

    [Fact]
    public void A_qualified_name_is_left_as_the_document_gives_it()
    {
        var folder = Path.Combine(_dir, "qualified");
        Directory.CreateDirectory(folder);
        File.Copy(OwnerHousekeepingTests.Fixture("Weather Monitor Views.lvlib"),
                  Path.Combine(folder, "Weather Monitor Views.lvlib"));
        var xml = Path.Combine(_dir, "q.xml");
        File.WriteAllText(xml, Document.Replace("_name=\"Clean Up View.vi\"",
                                                "_name=\"Weather Monitor Views.lvlib\\3AClean Up View.vi\""));

        var decided = AixmlComposer.QualifyForOwner(xml, Path.Combine(folder, "Clean Up View.vi"));

        Assert.Equal(xml, decided.AixmlPath);
        Assert.Null(decided.TempCopy);
    }

    [Fact]
    public void A_class_inside_a_library_qualifies_with_both_names()
    {
        var lvclass = Path.Combine(_dir, "Heater.lvclass");
        File.WriteAllText(lvclass, """
            <?xml version='1.0' encoding='UTF-8'?>
            <LVClass LVVersion="26008000">
            	<Property Name="NI.Lib.ContainingLib" Type="Str">Oven.lvlib</Property>
            </LVClass>
            """);

        Assert.Equal("Oven.lvlib:Heater.lvclass", AixmlComposer.OwnerQualifier(lvclass));
    }
}
