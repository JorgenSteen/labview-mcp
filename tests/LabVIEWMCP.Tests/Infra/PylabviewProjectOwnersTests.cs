// ============================================================================
// The composer finds a project's classes, and their ancestors, from deep below the project.
// ============================================================================
using System.Text;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Session-7 report M2, 2026-11-03: a DQMH handler four folders below its repository refused "no
/// Multiplexer.lvclass in the installation or near the caller" - the near index reaches three
/// levels - and generated one level up, "no Instrument.lvclass": the parent class lives in a SIBLING
/// repository the .lvproj does not list, linked only by Multiplexer.lvclass's
/// NI.LVClass.ParentClassLinkInfo (a LabVIEW 18 class file has no Parent items). The server now
/// passes the nearest .lvproj above the VI (up to eight levels) as `--project`, and an OWNER lookup
/// uses the classes and libraries it lists plus every listed class's ancestors - Parent items, or
/// the link info when there are none. The fixture is the repository's basic-interfaces classes
/// (Flathead -> Rotating Tool -> Tool), placed OUTSIDE the project like the sibling repository.
/// Measured on the user's own handler the same day: Call 112 and 113 resolved and the VI composed.
/// These run the bundled interpreter and SKIP without it.
/// </summary>
public sealed class PylabviewProjectOwnersTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private const string Caller = """
        <VI _name="Handler.vi" description="Calls a project class and its grandparent.">
          <Call target="Flathead.lvclass\3ATurn.vi" inputs="" outputs="" uid="4210" uid_parent="root"/>
          <Call target="Tool.lvclass\3ARead Purchase Price.vi" inputs="" outputs="" uid="4220" uid_parent="root"/>
        </VI>
        """;

    /// <summary>repo\Repo.lvproj listing ..\lib\Flathead\Flathead.lvclass; the caller four folders down.</summary>
    private static (string Repo, string CallerXml, string CallerVi) Tree(LvaiTestServer server, string name,
                                                                         bool withProject, bool linkInfoOnly = false)
    {
        var root = server.TempPath(name);
        var lib = Path.Combine(root, "lib");
        foreach (var dir in Directory.GetDirectories(RepoTree.Path("tests", "LabVIEWMCP.Tests", "Fixtures", "basic-interfaces")))
        {
            var to = Path.Combine(lib, Path.GetFileName(dir));
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(dir)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        if (linkInfoOnly) ToLinkInfo(Path.Combine(lib, "Flathead", "Flathead.lvclass"));

        var repo = Path.Combine(root, "repo");
        var deep = Path.Combine(repo, "DQMH", "Libraries", "Module", "Message Handlers");
        Directory.CreateDirectory(deep);
        if (withProject)
            File.WriteAllText(Path.Combine(repo, "Repo.lvproj"), """
                <?xml version='1.0' encoding='UTF-8'?>
                <Project Type="Project" LVVersion="26008000">
                	<Item Name="My Computer" Type="My Computer">
                		<Item Name="Flathead.lvclass" Type="LVClass" URL="../../lib/Flathead/Flathead.lvclass"/>
                		<Item Name="Dependencies" Type="Dependencies"/>
                		<Item Name="Build Specifications" Type="Build"/>
                	</Item>
                </Project>
                """);
        var xml = Path.Combine(deep, "Handler.xml");
        File.WriteAllText(xml, Caller);
        return (repo, xml, Path.Combine(deep, "Handler.vi"));
    }

    /// <summary>
    /// Flathead's Parent items replaced by the link info a LabVIEW 18 class file carries instead:
    /// a header, the parent's qualified name, then a PTH0 path ref of type 1 - relative to the class
    /// file treated as a directory, each empty component one level up - laid out as measured in the
    /// session-7 Multiplexer.lvclass, with this fixture's names.
    /// </summary>
    private static void ToLinkInfo(string lvclass)
    {
        static byte[] Pascal(string s) => [(byte)s.Length, .. Encoding.Latin1.GetBytes(s)];
        string[] comps = ["", "", "Rotating Tool", "Rotating Tool.lvclass"];
        var path = new List<byte> { 0x00, 0x01, 0x00, (byte)comps.Length };
        foreach (var c in comps) path.AddRange(Pascal(c));
        var blob = new List<byte> { 0x18, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x05, 0x00, 0x05,
                                    0x00, 0x00, 0x0c, 0x00, 0x40, 0x00, 0x01, 0xff, 0xff, 0xff, 0xff, 0x00,
                                    0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x72, 0x00, 0x00, 0x00, 0x01 };
        blob.AddRange(Pascal("Rotating Tool.lvclass"));
        blob.Add(0x00);
        blob.AddRange("PTH0"u8.ToArray());
        blob.AddRange([0x00, 0x00, 0x00, (byte)path.Count]);
        blob.AddRange(path);
        blob.AddRange([0x00, 0x00, 0x00, 0x00]);

        var text = File.ReadAllText(lvclass);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*<Item Name=""[^""]*"" Type=""Parent""[^>]*/>", "");
        var encoded = System.Security.SecurityElement.Escape(LvClass.Encode(blob.ToArray()));
        text = text.Replace("<Property Name=\"NI.Lib.Version\"",
            $"<Property Name=\"NI.LVClass.ParentClassLinkInfo\" Type=\"Bin\">{encoded}</Property>\n\t<Property Name=\"NI.Lib.Version\"",
            StringComparison.Ordinal);
        Assert.Contains("ParentClassLinkInfo", text);
        Assert.DoesNotContain("Type=\"Parent\"", text);
        File.WriteAllText(lvclass, text);
    }

    private static async Task<string> ComposeAsync(LvaiTestServer server, string xml, string vi) =>
        await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");

    [Fact]
    public async Task Without_a_project_the_class_four_folders_up_is_not_found()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var (_, xml, vi) = Tree(server, "none", withProject: false);

        var composed = await ComposeAsync(server, xml, vi);

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains("no Flathead.lvclass", Res.Str(composed, "errorMessage"));
    }

    [Fact]
    public async Task The_project_s_class_and_its_grandparent_resolve_from_four_folders_below()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var (_, xml, vi) = Tree(server, "items", withProject: true);

        var composed = await ComposeAsync(server, xml, vi);

        Assert.Equal(0, Res.Int(composed, "errorCode"));
    }

    [Fact]
    public async Task A_parent_linked_only_by_ParentClassLinkInfo_resolves_too()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var (_, xml, vi) = Tree(server, "linkinfo", withProject: true, linkInfoOnly: true);

        var composed = await ComposeAsync(server, xml, vi);

        Assert.Equal(0, Res.Int(composed, "errorCode"));
    }

    [Fact]
    public void The_nearest_project_is_found_eight_levels_up_and_never_in_our_scratch()
    {
        var root = Directory.CreateTempSubdirectory("nearest-projects").FullName;
        try
        {
            var deep = Path.Combine(root, "a", "b", "c", "d", "e");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(root, "P.lvproj"), "<Project/>");
            Assert.Equal([Path.Combine(root, "P.lvproj")], AixmlComposer.NearestProjects(Path.Combine(deep, "x.vi")));

            var scratch = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try { Assert.Empty(AixmlComposer.NearestProjects(Path.Combine(scratch, "x.vi"))); }
            finally { Directory.Delete(scratch, true); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SearchFolders_must_be_existing_absolute_folders()
    {
        Assert.Null(AixmlComposer.UseSearchFolders(null));
        Assert.Null(AixmlComposer.UseSearchFolders(" \n "));
        Assert.Contains("badArguments", AixmlComposer.UseSearchFolders(@"relative\folder"));
        Assert.Contains("badArguments", AixmlComposer.UseSearchFolders(@"C:\no\such\folder\7f3a"));
        Assert.Null(AixmlComposer.UseSearchFolders(Path.GetTempPath()));
    }
}
