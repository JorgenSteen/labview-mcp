// =============================================================================
// LabViewRestartTests.cs - every guard of the close/restart route on a fake machine:
// which one refuses, what a go-ahead opens and what it never opens, and that no close is sent.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class LabViewRestartTests
{
    private const string AutoSave = @"X:\Docs\LabVIEW Data\LVAutoSave";
    private const string Exe = @"X:\NI\LabVIEW 2026\LabVIEW.exe";
    private const string MyProject = @"X:\work\Mine.lvproj";

    /// <summary>A scripted LabVIEW world plus a log of every outside call the route makes.</summary>
    private sealed class World
    {
        public FakeLabViewMachine M { get; } = new();
        public List<string> Log { get; } = [];
        public bool ServerStarted { get; set; }
        public ServiceProbe Service { get; set; } = new(true, 50123, null);
        public ActiveProjectProbe Active { get; set; } = new(false, null, "NO project is active");
        public VisInMemoryReading? Memory { get; set; } = VisInMemory.Parse(
            ["0\t0\t0\tLV AI Core.lvlibp:A.vi", "0\t0\t0\tGSW.lvlibp:B.vi"], _ => false);
        public string? MemoryNotRun { get; set; }
        public Action? OnProjectClose { get; set; }
        public (int Pid, DateTime? Start)? Recorded { get; private set; }

        public World()
        {
            M.Folders[AutoSave] = [];
            M.AddProcess(10, "LabVIEW", FakeLabViewMachine.Epoch.AddHours(-2), Exe);
            M.AddProcess(5, "NI.NigelLocalService");
            M.AddWindow(10, "LVFrame", "LabVIEW", true);
            M.AddWindow(10, "LVDChild", "LabVIEW", true);
            M.AddWindow(10, "LVDChild", "Context Help", false);
            M.AddWindow(10, "IME", "Default IME", false);
            M.CpuRate[10] = 0.0;
        }

        public RestartDependencies Deps() => new()
        {
            Machine = M,
            AutoSaveDirectory = AutoSave,
            IsServerStarted = _ => ServerStarted,
            RecordOwnership = (pid, start) => Recorded = (pid, start),
            ProbeService = _ => { Log.Add("probe"); return Task.FromResult(Service); },
            ActiveProject = _ => { Log.Add("active"); return Task.FromResult(Active); },
            CloseProject = (path, _) =>
            {
                Log.Add("closeProject " + path);
                OnProjectClose?.Invoke();
                return Task.FromResult(new JsonObject { ["closed"] = true });
            },
            ReadMemory = _ => { Log.Add("memory"); return Task.FromResult((Memory, MemoryNotRun)); },
            Start = (exe, _) =>
            {
                Log.Add("start " + exe);
                M.AddProcess(11, "LabVIEW", M.Now, exe);
                return Task.FromResult(new StartResult(true, 11, M.Now, 50999, new JsonObject { ["seconds"] = 33 }));
            },
        };

        public Task<JsonObject> Run(string mode, string? goAhead = null, string? project = null) =>
            LabViewRestart.RunAsync(new RestartRequest(mode, goAhead, project), Deps(), default);
    }

    private static string Str(JsonObject o, string key) => o[key]!.GetValue<string>();

    // ------------------------------------------------------------------ the three hard guards

    [Theory]
    [InlineData("close")]
    [InlineData("restart")]
    public async Task No_labview_is_refused_by_the_process_guard(string mode)
    {
        var w = new World();
        w.M.ProcessList.RemoveAll(p => p.Name == "LabVIEW");
        var a = await w.Run(mode, goAhead: "yes, restart it - the user, 2026-10-10");

        Assert.Equal("processes", Str(a, "stoppedBy"));
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task Two_labviews_are_refused_whatever_the_go_ahead_and_both_are_listed()
    {
        var w = new World();
        w.M.AddProcess(12, "LabVIEW");
        var a = await w.Run("restart", goAhead: "restart LabVIEW (the user, 2026-10-10)");

        Assert.Equal("processes", Str(a, "stoppedBy"));
        Assert.Equal(2, a["labview"]!.AsArray().Count);
        Assert.Empty(w.M.ClosesSent);
        Assert.Empty(w.Log);   // not even the service was asked
    }

    [Fact]
    public async Task A_visible_dialog_is_refused_and_named_whatever_the_go_ahead()
    {
        var w = new World();
        w.M.AddWindow(10, "LVDChild", "Save changes before closing?", true);
        var a = await w.Run("close", goAhead: "close it (the user)");

        Assert.Equal("dialog", Str(a, "stoppedBy"));
        Assert.Contains("Save changes before closing?", Str(a, "error"));
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task An_untitled_modal_dialog_owned_by_the_frame_is_refused_whatever_the_go_ahead()
    {
        // Measured 2026-10-10: "... has changed on disk" was a visible UNTITLED window owned by the frame.
        var w = new World { ServerStarted = true };
        var frame = w.M.WindowList.First(x => x.ClassName == "LVFrame");
        w.M.WindowList[w.M.WindowList.IndexOf(frame)] = frame with { Enabled = false };
        w.M.AddWindow(10, "LVDChild", "", true, owner: frame.Handle);
        var a = await w.Run("close", goAhead: "close it (the user)");

        Assert.Equal("dialog", Str(a, "stoppedBy"));
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task An_unrecognised_hidden_window_needs_the_go_ahead_even_on_the_server_s_labview()
    {
        var w = new World { ServerStarted = true };
        w.M.AddWindow(10, "LVDChild", "Externally Changed Files List", false);
        var a = await w.Run("close");

        Assert.Equal("needsUserGoAhead", Str(a, "errorKind"));
        Assert.Equal("hiddenWindows", Str(a, "stoppedBy"));
        Assert.Contains("class LVDChild", a["needsGoAheadFor"]!["hiddenWindows"]!.GetValue<string>());
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task The_go_ahead_opens_an_unrecognised_hidden_window_and_names_each_one()
    {
        var w = new World { ServerStarted = true };
        w.M.AddWindow(10, "SomeNewClass", "NI Licence Notifier", false);
        w.M.AddWindow(10, "LVDChild", "Externally Changed Files List", false);
        var a = await w.Run("close", goAhead: "close it (the user, 2026-10-10)");

        Assert.True(a["ok"]!.GetValue<bool>());
        var named = a["overriddenHiddenWindows"]!.AsArray();
        Assert.Equal(2, named.Count);
        Assert.Contains(named, n => n!["class"]!.GetValue<string>() == "SomeNewClass");
        Assert.Equal([10], w.M.ClosesSent);
    }

    [Fact]
    public async Task A_hidden_window_raised_while_the_guards_ran_stops_the_close_without_a_go_ahead()
    {
        var w = new World { ServerStarted = true, Active = new ActiveProjectProbe(true, MyProject, "active") };
        w.OnProjectClose = () => w.M.AddWindow(10, "LVDChild", "Save changes before closing?", false);
        var a = await w.Run("close", project: MyProject);

        Assert.Equal("hiddenWindows", Str(a, "stoppedBy"));
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task A_busy_labview_is_refused_with_its_samples_whatever_the_go_ahead()
    {
        var w = new World { ServerStarted = true };
        w.M.CpuRate[10] = 0.75;
        var a = await w.Run("restart", goAhead: "restart (the user)");

        Assert.Equal("busy", Str(a, "stoppedBy"));
        var sample = a["guards"]!["cpu"]![0]!;
        Assert.Equal("busy", sample["verdict"]!.GetValue<string>());
        Assert.Equal(11.25, sample["cpuSeconds"]!.GetValue<double>());
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task A_dialog_raised_while_the_guards_ran_still_stops_the_close()
    {
        var w = new World { ServerStarted = true };
        w.Active = new ActiveProjectProbe(true, MyProject, "active");
        w.OnProjectClose = () => w.M.AddWindow(10, "LVDChild", "Save changes before closing?", true);
        var a = await w.Run("close", project: MyProject);

        Assert.Equal("dialog", Str(a, "stoppedBy"));
        Assert.Empty(w.M.ClosesSent);
    }

    // ------------------------------------------------------------------ ownership

    [Fact]
    public async Task The_user_s_labview_with_a_vi_open_needs_the_user_s_words_and_nothing_is_closed()
    {
        var w = new World();
        w.M.AddWindow(10, "LVDChild", "Main.vi Front Panel", true);
        var a = await w.Run("restart");

        Assert.Equal("needsUserGoAhead", Str(a, "errorKind"));
        Assert.Equal("ownership", Str(a, "stoppedBy"));
        Assert.False(a["emptyChecks"]!["windows"]!["empty"]!.GetValue<bool>());
        Assert.Empty(w.M.ClosesSent);
        Assert.DoesNotContain(w.Log, l => l.StartsWith("closeProject") || l.StartsWith("start"));
    }

    [Fact]
    public async Task The_user_s_words_open_the_ownership_guard_and_are_echoed()
    {
        var w = new World();
        w.M.AddWindow(10, "LVDChild", "Main.vi Front Panel", true);
        const string words = "\"ja, start LabVIEW på nytt\" (the user, 2026-10-10 14:02)";
        var a = await w.Run("restart", goAhead: words);

        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal("restarted", Str(a, "state"));
        Assert.Equal(words, a["ownership"]!["goAhead"]!["words"]!.GetValue<string>());
        Assert.Contains("ownership", a["ownership"]!["goAhead"]!["covers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal([10], w.M.ClosesSent);
        // The user's LabVIEW restarted stays the user's: no ownership recorded for the new one.
        Assert.Null(w.Recorded);
        Assert.Equal(11, a["after"]!["labview"]!["pid"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_empty_labview_is_restarted_with_no_go_ahead_and_the_checks_say_why()
    {
        var w = new World();
        var a = await w.Run("restart");

        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal("empty", a["ownership"]!["whose"]!.GetValue<string>());
        Assert.True(a["emptyChecks"]!["memory"]!["empty"]!.GetValue<bool>());
        Assert.Equal(["probe", "active", "memory", "start " + Exe], w.Log);
        Assert.Null(w.Recorded);
        Assert.Contains("0 before -> 0 after", Str(a, "autoSave"));
    }

    [Fact]
    public async Task An_empty_window_list_with_somebody_s_vi_in_memory_is_not_empty()
    {
        var w = new World
        {
            Memory = VisInMemory.Parse(["0\t0\t0\tLV AI Core.lvlibp:A.vi", "0\t0\t0\tTheirs.vi"], _ => false),
        };
        var a = await w.Run("close");

        Assert.Equal("ownership", Str(a, "stoppedBy"));
        Assert.False(a["emptyChecks"]!["memory"]!["empty"]!.GetValue<bool>());
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task A_memory_check_that_cannot_run_makes_it_not_empty()
    {
        var w = new World { Memory = null, MemoryNotRun = "the memory helper is not generated yet" };
        var a = await w.Run("close");

        Assert.Equal("ownership", Str(a, "stoppedBy"));
        Assert.Contains("not generated", a["emptyChecks"]!["memory"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_server_s_own_labview_is_closed_with_no_go_ahead_even_with_vis_open()
    {
        var w = new World { ServerStarted = true };
        w.M.AddWindow(10, "LVDChild", "Main.vi Front Panel", true);
        var a = await w.Run("close");

        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal("closed", Str(a, "state"));
        Assert.Equal("started by this server", a["ownership"]!["whose"]!.GetValue<string>());
        Assert.False(a["ownership"]!["goAhead"]!["needed"]!.GetValue<bool>());
        Assert.Equal(4.3, a["close"]!["seconds"]!.GetValue<double>());
        Assert.DoesNotContain(w.Log, l => l.StartsWith("start"));
    }

    [Fact]
    public async Task A_restart_of_the_server_s_labview_records_the_new_one_as_the_server_s()
    {
        var w = new World { ServerStarted = true };
        var a = await w.Run("restart");

        Assert.Equal("restarted", Str(a, "state"));
        Assert.Equal(11, w.Recorded?.Pid);
    }

    // ------------------------------------------------------------------ the go-ahead guards

    [Fact]
    public async Task Unsaved_windows_need_the_go_ahead_even_on_the_server_s_own_labview()
    {
        var w = new World { ServerStarted = true };
        w.M.AddWindow(10, "LVDChild", "Main.vi Block Diagram *", true);
        var a = await w.Run("close");

        Assert.Equal("unsaved", Str(a, "stoppedBy"));
        Assert.Single(a["guards"]!["windows"]!["unsaved"]!.AsArray());
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task Unsaved_scratch_vis_say_that_don_t_save_all_is_safe()
    {
        var w = new World { ServerStarted = true };
        w.M.AddWindow(10, "LVDChild", "LVMCP Validate4.vi Front Panel *", false);
        var a = await w.Run("close");

        Assert.Equal("unsaved", Str(a, "stoppedBy"));
        Assert.Contains("Don't Save - All", a["needsGoAheadFor"]!["unsaved"]!.GetValue<string>());
        // Not closed by the tool, and the answer says why: no route reaches them without a prompt.
        Assert.False(a["guards"]!["scratchVis"]!["closedByThisTool"]!.GetValue<bool>());
        Assert.Contains("1004", a["guards"]!["scratchVis"]!["why"]!.GetValue<string>());
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task Auto_save_files_mean_somebody_s_work_and_need_the_go_ahead()
    {
        var w = new World { ServerStarted = true };
        w.M.Folders[AutoSave] = [AutoSave + @"\Main.vi"];
        var a = await w.Run("close");

        Assert.Equal("autoSave", Str(a, "stoppedBy"));
        Assert.Empty(w.M.ClosesSent);
    }

    [Fact]
    public async Task A_dead_service_needs_the_go_ahead_and_skips_project_and_memory()
    {
        var w = new World { ServerStarted = true, Service = new ServiceProbe(false, null, "DeadlineExceeded") };
        var a = await w.Run("close");

        Assert.Equal("service", Str(a, "stoppedBy"));
        Assert.Equal(["probe"], w.Log);

        var b = await w.Run("close", goAhead: "close it anyway (the user, 2026-10-10)");
        Assert.True(b["ok"]!.GetValue<bool>());
        Assert.Contains("service", b["ownership"]!["goAhead"]!["covers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain("memory", w.Log);
    }

    [Fact]
    public async Task Another_active_project_is_never_closed_by_the_tool_and_needs_the_go_ahead()
    {
        var w = new World { ServerStarted = true, Active = new ActiveProjectProbe(true, @"X:\theirs\Their.lvproj", "active") };
        w.M.AddWindow(10, "LVDChild", "Their.lvproj - Project Explorer", true);
        var a = await w.Run("close", project: MyProject);
        Assert.Equal("activeProject", Str(a, "stoppedBy"));

        var b = await w.Run("close", goAhead: "close LabVIEW, my project too (the user)", project: MyProject);
        Assert.True(b["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(w.Log, l => l.StartsWith("closeProject"));
        // A probe run now would be adopted into their project.
        Assert.DoesNotContain("memory", w.Log);
    }

    [Fact]
    public async Task This_session_s_active_project_is_saved_and_closed_before_the_memory_check()
    {
        var w = new World { Active = new ActiveProjectProbe(true, MyProject, "active") };
        w.M.AddWindow(10, "LVDChild", "Mine.lvproj - Project Explorer", true);
        w.M.AddWindow(10, "LVDChild", "A.vi Front Panel on Mine.lvproj/My Computer", true);
        var a = await w.Run("close", project: MyProject);

        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal("empty", a["ownership"]!["whose"]!.GetValue<string>());
        Assert.Equal(["probe", "active", "closeProject " + MyProject, "memory"], w.Log);
        Assert.Contains(a["sideEffects"]!.AsArray(), s => s!.GetValue<string>().Contains("SAVED and closed"));
    }

    [Fact]
    public async Task A_modified_vi_in_memory_needs_the_go_ahead()
    {
        var w = new World
        {
            ServerStarted = true,
            Memory = VisInMemory.Parse(["2\t8\t0\tMain.vi"], _ => false),
        };
        var a = await w.Run("close");

        Assert.Equal("memory", Str(a, "stoppedBy"));
        Assert.Contains("Main.vi", a["needsGoAheadFor"]!["memory"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ the close itself

    [Theory]
    [InlineData(true, "a dialog came up after the request")]
    [InlineData(false, "did not take the close message")]
    public async Task A_close_that_does_not_finish_is_reported_and_nothing_more_is_done(bool taken, string meaning)
    {
        var w = new World { ServerStarted = true };
        w.M.OnClose = (pid, wait) =>
        {
            w.M.AddWindow(10, "LVDChild", "Save changes before closing?", true);
            w.M.Now += wait;
            return new CloseOutcome(true, taken, false, wait.TotalSeconds, null, w.M.Now - wait, w.M.Now);
        };
        var a = await w.Run("restart");

        Assert.False(a["ok"]!.GetValue<bool>());
        Assert.Equal("closeDidNotFinish", Str(a, "state"));
        Assert.Contains(meaning, Str(a, "error"));
        Assert.Single(a["close"]!["dialogsNow"]!.AsArray());
        Assert.NotNull(a["close"]!["cpuNow"]);
        Assert.Single(w.M.ClosesSent);
        Assert.Contains(w.M.ProcessList, p => p.Id == 10);   // never killed
        Assert.DoesNotContain(w.Log, l => l.StartsWith("start"));
    }

    [Fact]
    public async Task Auto_save_files_after_the_close_stop_the_start()
    {
        var w = new World { ServerStarted = true };
        w.M.OnClose = (pid, wait) =>
        {
            w.M.ProcessList.RemoveAll(p => p.Id == pid);
            w.M.Folders[AutoSave] = [AutoSave + @"\x.vi"];
            return new CloseOutcome(true, true, true, 5, 0, w.M.Now, w.M.Now);
        };
        var a = await w.Run("restart");

        Assert.Equal("closedNotStarted", Str(a, "state"));
        Assert.DoesNotContain(w.Log, l => l.StartsWith("start"));
        Assert.Contains("0 before -> 1 after", Str(a, "autoSave"));
    }

    [Fact]
    public async Task The_report_carries_pids_ports_and_the_nigel_service_before_and_after()
    {
        var w = new World { ServerStarted = true };
        var a = await w.Run("restart");

        Assert.Equal(10, a["before"]!["labview"]!["pid"]!.GetValue<int>());
        Assert.Equal(50123, a["before"]!["port"]!.GetValue<int>());
        Assert.Equal(50999, a["after"]!["port"]!.GetValue<int>());
        Assert.Equal(5, a["after"]!["nigelLocalService"]![0]!["pid"]!.GetValue<int>());
        Assert.Equal("no kill, no dialog answered, no Nigel process touched", Str(a, "neverDone"));
    }
}

/// <summary>The tool refuses a bad request before it reads, samples or closes anything.</summary>
public class RestartToolArgumentTests
{
    private static LabVIEWMcp.Tools.RestartTools Tool() =>
        new(new LabVIEWMcp.Grpc.LvaiConnection(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LabVIEWMcp.Grpc.LvaiConnection>.Instance, 1));

    [Theory]
    [InlineData("reboot")]
    [InlineData("")]
    public async Task An_unknown_mode_is_refused(string mode)
    {
        var o = JsonNode.Parse(await Tool().RestartLabViewAsync(mode))!.AsObject();
        Assert.Equal("badMode", o["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_project_path_that_is_not_an_existing_lvproj_is_refused()
    {
        var o = JsonNode.Parse(await Tool().RestartLabViewAsync("close", projectPath: @"X:\nowhere\P.lvproj"))!.AsObject();
        Assert.Equal("noSuchProject", o["errorKind"]!.GetValue<string>());
    }
}
