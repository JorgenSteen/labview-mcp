// =============================================================================
// LabViewStateTests.cs - the window classifier (invisible dialogs included), the
// LVAutoSave count, the CPU verdict and the ownership record, on a fake machine.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class LabViewWindowClassifierTests
{
    private static string Kind(string title, bool visible, string cls = "LVDChild") =>
        LabViewState.Classify(new MachineWindow(1, 1, cls, title, visible));

    /// <summary>The kit's own self-test samples, which the port must keep answering the same way.</summary>
    [Theory]
    [InlineData("ZM-receiver.vi", true, WindowKinds.Vi)]
    [InlineData("ZM-launcher.vi Front Panel on Zuehlke MCP.lvproj/My Computer", true, WindowKinds.Vi)]
    [InlineData("ZM-launcher.vi Block Diagram on Zuehlke MCP.lvproj/My Computer", true, WindowKinds.Vi)]
    [InlineData("Zuehlke MCP.lvproj - Project Explorer", true, WindowKinds.Project)]
    [InlineData("LabVIEW", true, WindowKinds.Frame)]
    [InlineData("Getting Started", true, WindowKinds.GettingStarted)]
    [InlineData("*ZM-ROOT-helper_loop.vi Block Diagram", true, WindowKinds.Unsaved)]
    [InlineData("Save Changes", true, WindowKinds.PossibleDialog)]
    [InlineData("Receiver", true, WindowKinds.PossibleDialog)]
    [InlineData("Context Help", false, WindowKinds.HiddenSystem)]
    [InlineData("RunService.vi", false, WindowKinds.HiddenSystem)]
    public void The_kit_samples_keep_their_kind(string title, bool visible, string expected) =>
        Assert.Equal(expected, Kind(title, visible));

    /// <summary>
    /// THE BUG THE PORT FIXES. The kit called a hidden titled window "hidden-other" and never flagged
    /// it; measured 2026-10-06, LabVIEW's Error list and Externally Changed Files List dialogs were on
    /// screen, unclickable, and reported IsWindowVisible FALSE.
    /// </summary>
    [Theory]
    [InlineData("Error list")]
    [InlineData("Externally Changed Files List")]
    [InlineData("Something else")]
    public void A_titled_window_that_reports_itself_invisible_is_still_a_possible_dialog(string title) =>
        Assert.Equal(WindowKinds.PossibleDialog, Kind(title, visible: false));

    /// <summary>The hidden windows of the measured 2026-10-07 listing that are not LabVIEW's at all.</summary>
    [Theory]
    [InlineData("OleAutomationWnd", "OleAutomation Window")]
    [InlineData("HwndWrapper[NILM UAU AppDomain;;482f77f2]", "SystemResourceNotifyWindow")]
    [InlineData("GDI+ Hook Window Class", "GDI+ Window (LabVIEW.exe)")]
    [InlineData("LVWinSessionMonitor", "LabVIEW")]
    [InlineData("OleDdeWndClass", "DDE Server Window")]
    [InlineData("IME", "Default IME")]
    [InlineData("MSCTFIME UI", "MSCTFIME UI")]
    [InlineData("ATL:131C4278", "Set Title")]
    public void Measured_hidden_system_classes_are_not_dialogs(string cls, string title) =>
        Assert.Equal(WindowKinds.HiddenSystem, Kind(title, false, cls));

    [Fact]
    public void The_same_system_class_VISIBLE_is_judged_by_its_title()
    {
        // The class list only excuses HIDDEN windows: a visible window is what a person sees.
        Assert.Equal(WindowKinds.PossibleDialog, Kind("Default IME", true, "IME"));
    }

    [Fact]
    public void The_service_s_own_hidden_vi_window_is_the_service()
    {
        Assert.Equal(WindowKinds.Service,
            Kind("LV AI gRPC Service.lvlibp:gRPC Implementations.lvlib:RunService.vi", false));
    }

    [Fact]
    public void An_untitled_visible_window_is_not_a_dialog()
    {
        Assert.Equal(WindowKinds.Untitled, Kind("", true, "Internet Explorer_Hidden"));
    }

    [Fact]
    public void A_hidden_unsaved_scratch_vi_is_unsaved_and_known_as_labview_mcp_s()
    {
        var w = new MachineWindow(1, 1, "LVDChild", "LVMCP Validate7.vi Front Panel *", false);
        var classified = new ClassifiedWindow(w, LabViewState.Classify(w));
        Assert.Equal(WindowKinds.Unsaved, classified.Kind);
        Assert.True(classified.IsLabViewMcpScratch);
    }

    [Fact]
    public void A_qualified_clone_title_is_a_vi()
    {
        Assert.Equal(WindowKinds.Vi,
            Kind("Lib.lvlib:A.lvclass:Check.vi:Instance:5bd28a13.vi:Instance:529e3ff3.vi", true));
    }
}

public class LabViewSnapshotTests
{
    private const string AutoSave = @"D:\Docs\LabVIEW Data\LVAutoSave";

    [Fact]
    public void The_snapshot_reads_processes_windows_and_the_auto_save_folder()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW", path: @"C:\Program Files (x86)\National Instruments\LabVIEW 2026\LabVIEW.exe");
        m.AddProcess(20, "NI.NigelLocalService");
        m.AddWindow(10, "LVFrame", "LabVIEW", true);
        m.AddWindow(10, "LVDChild", "Save changes before closing?", false);
        m.AddWindow(10, "LVDChild", "A.vi Front Panel *", true);
        m.Folders[AutoSave] = [AutoSave + @"\a.vi", AutoSave + @"\archives\b.vi"];

        var s = LabViewState.Take(m, AutoSave);

        Assert.Equal(10, s.Single?.Id);
        Assert.Single(s.NigelService);
        Assert.Empty(s.NigelChat);
        Assert.Single(s.Dialogs);
        Assert.Single(s.Unsaved);
        Assert.Equal(2, s.AutoSave.Count);
        Assert.Contains(@"archives\b.vi", s.AutoSave.Sample);
    }

    [Fact]
    public void A_missing_auto_save_folder_counts_zero_and_says_it_is_missing()
    {
        var s = LabViewState.Take(new FakeLabViewMachine(), AutoSave);
        Assert.False(s.AutoSave.Exists);
        Assert.Equal(0, s.AutoSave.Count);
        Assert.Null(s.Single);
    }

    [Fact]
    public async Task A_quiet_and_a_busy_window_are_told_apart_at_the_kit_s_threshold()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW");

        m.CpuRate[10] = 0.01;   // 0.15 s in 15 s
        var quiet = await LabViewState.SampleCpuAsync(m, 10, TimeSpan.FromSeconds(15), default);
        Assert.False(quiet.Busy);

        m.CpuRate[10] = 0.75;   // 11.3 s in 15 s, the measured busy sample
        var busy = await LabViewState.SampleCpuAsync(m, 10, TimeSpan.FromSeconds(15), default);
        Assert.True(busy.Busy);
        Assert.Equal(15, busy.WindowSeconds);
    }

    [Fact]
    public void The_threshold_scales_with_the_window()
    {
        Assert.False(LabViewState.IsBusy(0.4, 15));   // below 0.5 s per 15 s
        Assert.True(LabViewState.IsBusy(0.5, 15));
        Assert.False(LabViewState.IsBusy(0.9, 30));
    }

    [Fact]
    public async Task An_unreadable_cpu_clock_is_no_verdict_rather_than_quiet()
    {
        var sample = await LabViewState.SampleCpuAsync(new FakeLabViewMachine(), 99, TimeSpan.FromSeconds(5), default);
        Assert.Null(sample.Busy);
    }

    [Fact]
    public void A_message_box_is_named_with_its_buttons_and_text_and_a_drawn_dialog_says_it_cannot_be_read()
    {
        var m = new FakeLabViewMachine();
        var box = m.AddWindow(10, "#32770", "Error", true, handle: 77);
        m.ChildMap[77] = [new("Button", "OK"), new("Static", "Object reference not set to an instance of an object.")];
        var drawn = m.AddWindow(10, "LVDChild", "Find the VI Named", true, handle: 78);

        var named = LabViewState.DescribeDialog(m, new ClassifiedWindow(box, WindowKinds.PossibleDialog));
        Assert.Equal("OK", named["buttons"]![0]!.GetValue<string>());
        Assert.Contains("Object reference", named["texts"]![0]!.GetValue<string>());
        Assert.Null(named["note"]);

        var lv = LabViewState.DescribeDialog(m, new ClassifiedWindow(drawn, WindowKinds.PossibleDialog));
        Assert.Contains("draws its own dialogs", lv["note"]!.GetValue<string>());
    }

    [Fact]
    public void Probing_for_dialogs_answers_null_when_there_are_none()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW");
        m.AddWindow(10, "LVFrame", "LabVIEW", true);
        Assert.Null(LabViewState.ProbeDialogs(m));

        m.AddWindow(10, "LVDChild", "Error list", false);
        var found = LabViewState.ProbeDialogs(m);
        Assert.NotNull(found);
        Assert.False(found![0]!["visible"]!.GetValue<bool>());
    }
}

[Collection("LabViewOwnership")]
public class LabViewOwnershipTests
{
    [Fact]
    public void Only_the_recorded_pid_with_its_start_time_is_the_server_s()
    {
        var start = FakeLabViewMachine.Epoch;
        LabViewOwnership.Record(42, start);
        try
        {
            Assert.True(LabViewOwnership.IsServerStarted(new MachineProcess(42, "LabVIEW", start.AddMilliseconds(300), null)));
            // A reused pid is somebody else's LabVIEW.
            Assert.False(LabViewOwnership.IsServerStarted(new MachineProcess(42, "LabVIEW", start.AddMinutes(5), null)));
            Assert.False(LabViewOwnership.IsServerStarted(new MachineProcess(43, "LabVIEW", start, null)));
        }
        finally
        {
            LabViewOwnership.Forget();
        }
        Assert.False(LabViewOwnership.IsServerStarted(new MachineProcess(42, "LabVIEW", start, null)));
    }

    [Fact]
    public async Task The_state_tool_reports_windows_dialogs_and_cpu_without_a_labview_call()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW");
        m.AddWindow(10, "LVFrame", "LabVIEW", true);
        m.AddWindow(10, "IME", "Default IME", false);
        m.AddWindow(10, "LVDChild", "Error list", false);
        m.AddWindow(10, "LVDChild", "B.vi Block Diagram *", true);
        m.CpuRate[10] = 0.0;

        var state = await RestartTools.StateAsync(m, @"X:\none", 5, includeHiddenWindows: false, default);

        Assert.Equal("one", state["labviewProcesses"]!.GetValue<string>());
        Assert.Single(state["possibleDialogs"]!.AsArray());
        Assert.Single(state["unsaved"]!.AsArray());
        Assert.Equal("quiet", state["cpu"]!["verdict"]!.GetValue<string>());
        Assert.DoesNotContain(state["windows"]!.AsArray(), w => w!["kind"]!.GetValue<string>() == WindowKinds.HiddenSystem);
        Assert.False(state["labview"]![0]!["startedByThisServer"]!.GetValue<bool>());
    }
}
