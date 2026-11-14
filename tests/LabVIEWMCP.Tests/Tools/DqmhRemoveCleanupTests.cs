// =============================================================================
// DqmhRemoveCleanupTests.cs - which frames lvai_dqmh_remove_event takes out of
// the tester and Main.vi before Delacor's Remove Event, chosen from the reader's
// export of the real VIs.
// =============================================================================
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// The fixtures are the AIXML reader's exports of a DQMH 7.1.2 module (`Pump`, LabVIEW 2026,
/// 2026-11-14). Main.vi, whole, as it was right before `Probe Req` was removed: its Event
/// Structure (9 frames) handles the request in frame 8, its message-handling Case Structure (9
/// frames, among seven Case Structures) has `"Probe Req"` at 2 - the two frames taken out before
/// Delacor ran, after which Remove Event ran clean and Main.vi stayed executable. The tester's
/// Event Structure, cut verbatim: frame 12 handles the broadcast `Level Low`, frame 14 calls
/// `Set Speed.vi` - frames Delacor leaves in place.
/// </summary>
public class DqmhRemoveCleanupTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        LabVIEWMcp.Tests.Support.RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "dqmh-remove", name));

    private static string Main => Fixture("Main before Remove Event.xml");

    [Fact]
    public void The_requests_event_case_in_Main_is_found_by_its_user_event()
    {
        var choice = DqmhEventEditTools.MainEventFramesOf(Main, "Pump", "Probe Req");

        Assert.Null(choice.Problem);
        Assert.Equal("EventStructure", choice.StructureClass);
        Assert.Equal(9, choice.FrameCount);
        Assert.Equal([8], choice.Frames);
    }

    [Fact]
    public void The_message_frame_is_found_in_the_one_case_structure_holding_it()
    {
        var choice = DqmhEventEditTools.MainMessageFramesOf(Main, "Probe Req");

        Assert.Null(choice.Problem);
        Assert.Equal("CaseStructure", choice.StructureClass);
        Assert.Equal(9, choice.FrameCount);
        Assert.Equal([2], choice.Frames);
    }

    /// <summary>The control: a name with no frame finds nothing, and a module prefix must match whole.</summary>
    [Fact]
    public void Nothing_is_taken_for_an_event_Main_does_not_handle()
    {
        Assert.Empty(DqmhEventEditTools.MainEventFramesOf(Main, "Pump", "Probe").Frames);
        Assert.Empty(DqmhEventEditTools.MainEventFramesOf(Main, "Pum", "Probe Req").Frames);
        Assert.Empty(DqmhEventEditTools.MainMessageFramesOf(Main, "Probe").Frames);
    }

    [Fact]
    public void The_testers_frames_of_an_event_come_highest_first()
    {
        var tester = Fixture("Tester after Remove Event.xml");

        var request = DqmhEventEditTools.TesterFramesOf(tester, "Pump", "Pump.lvlib:Set Speed.vi", "Set Speed");
        var broadcast = DqmhEventEditTools.TesterFramesOf(tester, "Pump", "Pump.lvlib:Level Low.vi", "Level Low");
        var kept = DqmhEventEditTools.TesterFramesOf(tester, "Pump", "Pump.lvlib:Tank Empty.vi", "Tank");

        Assert.Equal([14], request.Frames);
        Assert.Equal(15, request.FrameCount);
        Assert.Equal([12], broadcast.Frames);
        Assert.Empty(kept.Frames);   // "Tank" is not "Tank Empty": a broadcast is matched by its whole name
    }
}
