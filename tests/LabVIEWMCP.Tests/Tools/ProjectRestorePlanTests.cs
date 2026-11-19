// =============================================================================
// ProjectRestorePlanTests.cs - what a project listing puts back after LabVIEW's close:
// never a VI a listed library or class owns, and every other VI into the folder it was in.
// Why: a timed build of 2026-11-18 got four fresh library members back as loose entries,
// in the folder of an unrelated call.
// =============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class ProjectRestorePlanTests
{
    /// <summary>The build's own shape: a project beside `SubVIs`, a library in `SubVIs` owning two
    /// VIs in `SubVIs\Accounts`, one loose VI, URLs relative to the referencing FILE.</summary>
    private static (string Project, string Dir) Layout()
    {
        var dir = Directory.CreateTempSubdirectory("restore-plan").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "SubVIs", "Accounts"));
        Directory.CreateDirectory(Path.Combine(dir, "SubVIs", "Main"));
        foreach (var vi in new[] { @"SubVIs\Accounts\Find Account.vi", @"SubVIs\Accounts\Update Balance.vi", @"SubVIs\Main\Parse Amount.vi" })
            File.WriteAllText(Path.Combine(dir, vi), "not really a VI");
        File.WriteAllText(Path.Combine(dir, "SubVIs", "App.lvlib"), string.Join("\r\n",
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Library LVVersion=\"26008000\">",
            "\t<Item Name=\"Accounts\" Type=\"Folder\">",
            "\t\t<Item Name=\"Find Account.vi\" Type=\"VI\" URL=\"../Accounts/Find Account.vi\"/>",
            "\t\t<Item Name=\"Update Balance.vi\" Type=\"VI\" URL=\"../Accounts/Update Balance.vi\"/>",
            "\t</Item>",
            "</Library>"));
        var project = Path.Combine(dir, "App.lvproj");
        File.WriteAllText(project, string.Join("\r\n",
            "<?xml version='1.0' encoding='UTF-8'?>",
            "<Project Type=\"Project\" LVVersion=\"26008000\">",
            "\t<Item Name=\"My Computer\" Type=\"My Computer\">",
            "\t\t<Item Name=\"App.lvlib\" Type=\"Library\" URL=\"../SubVIs/App.lvlib\"/>",
            "\t\t<Item Name=\"Dependencies\" Type=\"Dependencies\"/>",
            "\t\t<Item Name=\"Build Specifications\" Type=\"Build\"/>",
            "\t</Item>",
            "</Project>"));
        return (project, dir);
    }

    [Fact]
    public void The_members_of_a_listed_library_are_read_from_the_library_file()
    {
        var (project, dir) = Layout();
        try
        {
            var owned = LvClass.OwnedMemberPaths(project);
            Assert.Equal(2, owned.Count);
            Assert.Contains(Path.Combine(dir, "SubVIs", "Accounts", "Find Account.vi"), owned);
            Assert.DoesNotContain(Path.Combine(dir, "SubVIs", "Main", "Parse Amount.vi"), owned);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_library_member_is_not_restored_and_the_rest_goes_back_where_it_was()
    {
        var (project, dir) = Layout();
        try
        {
            // listed before the close: the two fresh members and a loose VI, all under SubVIs,
            // one more at target level and one in a nested folder
            var before = new List<(string Name, string Url, string Folder)>
            {
                ("Find Account.vi", "../SubVIs/Accounts/Find Account.vi", "SubVIs"),
                ("Update Balance.vi", "../SubVIs/Accounts/Update Balance.vi", "SubVIs"),
                ("Parse Amount.vi", "../SubVIs/Main/Parse Amount.vi", "SubVIs"),
                ("Top.vi", "../Top.vi", ""),
                ("Deep.vi", "../Deep.vi", "Tests/Unit"),
            };
            var (plan, skipped) = TestTools.RestorePlan(before, [], LvClass.OwnedMemberPaths(project), project);

            Assert.Equal(["Find Account.vi", "Update Balance.vi"], skipped);
            Assert.Equal(["SubVIs", "", "Unit"], plan.Select(g => g.Folder));
            Assert.Equal(["Parse Amount.vi"], plan[0].Vis.Select(v => v.Name));
            Assert.Equal(["Top.vi"], plan[1].Vis.Select(v => v.Name));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_VI_this_call_moved_and_a_second_listing_of_one_file_are_left_out()
    {
        var (project, dir) = Layout();
        try
        {
            var before = new List<(string Name, string Url, string Folder)>
            {
                ("Run Tests.vi", "../Run Tests.vi", ""),
                ("Parse Amount.vi", "../SubVIs/Main/Parse Amount.vi", "SubVIs"),
                ("Parse Amount.vi", "../SubVIs/Main/Parse Amount.vi", ""),
            };
            var (plan, skipped) = TestTools.RestorePlan(before, ["run tests.vi"], new HashSet<string>(), project);

            Assert.Empty(skipped);
            var only = Assert.Single(plan);
            Assert.Equal("SubVIs", only.Folder);
            Assert.Single(only.Vis);
        }
        finally { Directory.Delete(dir, true); }
    }
}
