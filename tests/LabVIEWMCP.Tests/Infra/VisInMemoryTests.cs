// =============================================================================
// VisInMemoryTests.cs - parsing and sorting the lvai_vis_modified helper's lines:
// service, LabVIEW, helper and other VIs, and which carry unsaved changes.
// =============================================================================
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class VisInMemoryTests
{
    private static readonly Func<string, bool> Helpers =
        VisInMemory.HelperNames([@"C:\x\scripts\lvai_exec_state.xml", @"C:\x\scripts\README.md"],
                                [@"C:\t\helpers\lvai_run_and_read_typed.vi"]);

    [Fact]
    public void The_measured_empty_instance_is_service_and_start_window_only()
    {
        // The three libraries of the measured 419-line listing (2026-10-07), none modified.
        var reading = VisInMemory.Parse(
        [
            "0\t0\t0\tLV AI gRPC Service.lvlibp:lvai_ProjectItemUpdateType.ctl",
            "0\t0\t0\tLV AI Core.lvlibp:Is Nigel sharing enabled.vi",
            "0\t0\t0\tGSW.lvlibp:Something.vi",
            "0\t0\t0\tlvai_run_and_read_typed.vi",
            "0\t0\t0\tLVMCP Validate3.vi",
        ], Helpers);

        Assert.Equal(5, reading.Count);
        Assert.True(reading.OnlyServiceAndHelpers);
        Assert.Equal(VisInMemory.OriginLabView, reading.Entries[2].Origin);
        Assert.Equal(VisInMemory.OriginHelper, reading.Entries[3].Origin);
        Assert.Equal(VisInMemory.OriginHelper, reading.Entries[4].Origin);
    }

    [Fact]
    public void A_modified_vi_of_somebody_else_s_makes_it_not_empty_and_is_listed()
    {
        var reading = VisInMemory.Parse(
        [
            "0\t0\t0\tLV AI Core.lvlibp:A.vi",
            "4\t0\t0\tMy.lvlib:Main.vi",
            "0\t0\t0\tOther.vi",
        ], Helpers);

        Assert.False(reading.OnlyServiceAndHelpers);
        var modified = Assert.Single(reading.Modified);
        Assert.Equal("My.lvlib:Main.vi", modified.Name);
        Assert.Equal(2, reading.Other.Count);
        Assert.Single(reading.ToJson()["modified"]!.AsArray());
    }

    [Fact]
    public void An_unreadable_or_unparsable_line_is_kept_and_stops_it_being_empty()
    {
        var reading = VisInMemory.Parse(["0\t0\t1004\tlvai_exec_state.vi", "garbage"], Helpers);
        Assert.Single(reading.Unreadable);
        Assert.Single(reading.Unparsed);
        Assert.False(reading.OnlyServiceAndHelpers);
    }

    /// <summary>Measured 2026-10-10: 20 of 419 lines on a fresh LabVIEW, all NI packed-library .ctl, answered 1035.</summary>
    [Fact]
    public void Unreadable_lines_of_NI_s_packed_libraries_do_not_stop_it_being_empty()
    {
        var reading = VisInMemory.Parse(
        [
            "0	0	1035	LV AI gRPC Service.lvlibp:lvai_DiscussFileType.ctl",
            "0	0	1035	LV AI Core.lvlibp:VI Info.ctl",
            "0	0	0	GSW.lvlibp:Main.vi",
        ], Helpers);
        Assert.Equal(2, reading.Unreadable.Count);
        Assert.Empty(reading.UnreadableThatMatters);
        Assert.True(reading.OnlyServiceAndHelpers);
        Assert.Equal(0, reading.ToJson()["unreadableOutsidePackedLibraries"]!.GetValue<int>());

        var mine = VisInMemory.Parse(["0	0	1035	My.lvlib:Type.ctl"], Helpers);
        Assert.False(mine.OnlyServiceAndHelpers);
    }

    [Fact]
    public void A_helper_name_is_bare_never_library_qualified()
    {
        Assert.True(Helpers("lvai_exec_state.vi"));
        Assert.False(Helpers("Lib.lvlib:lvai_exec_state.vi"));
        Assert.False(Helpers("README.vi"));
    }
}
