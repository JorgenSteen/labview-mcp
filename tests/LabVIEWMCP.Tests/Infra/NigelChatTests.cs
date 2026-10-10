// =============================================================================
// NigelChatTests.cs - when the Nigel chat is started, with which arguments, once
// per LabVIEW, and that a message box after the start is named and never pressed.
// =============================================================================
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

[Collection("NigelChat")]
public class NigelChatTests
{
    private const string Lv = @"X:\Program Files (x86)\National Instruments\LabVIEW 2026\LabVIEW.exe";
    private const string Root = @"X:\Program Files\NI\LVAddons\lvai";
    private static readonly string Chat = Path.Combine(Root, "26.3", "resource", "AI", "LVNigelChat", "LVNigelChat.exe");

    private static FakeLabViewMachine Machine(int pid, double upSeconds)
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(pid, "LabVIEW", FakeLabViewMachine.Epoch.AddSeconds(-upSeconds), Lv);
        m.AddProcess(5, "NI.NigelLocalService");
        m.Versions[Lv] = (26, 3);
        m.Files.Add(Chat);
        m.Folders[Path.Combine(Root, "1")] = [];
        m.Folders[Path.Combine(Root, "26.1")] = [];
        m.Folders[Path.Combine(Root, "26.3")] = [];
        return m;
    }

    [Fact]
    public void The_target_is_the_add_on_folder_named_after_the_exe_version_with_both_arguments()
    {
        var m = Machine(100, 300);
        var (target, problem) = NigelChat.Resolve(m, m.ProcessList[0], Root);

        Assert.Null(problem);
        Assert.Equal(Chat, target!.Exe);
        // The exe does not exist on the test machine, so bitness falls back to the folder: x86.
        Assert.Equal("--lv-version 26.3 --lv-bitness x86", target.Arguments);
    }

    [Fact]
    public void A_missing_version_folder_is_refused_and_lists_what_is_there()
    {
        var m = Machine(101, 300);
        m.Versions[Lv] = (25, 3);
        var (target, problem) = NigelChat.Resolve(m, m.ProcessList[0], Root);

        Assert.Null(target);
        Assert.Contains("26.1", problem);
        Assert.Contains("26.3", problem);
    }

    [Theory]
    [InlineData(30, "cold start")]
    public void A_young_labview_gets_time_to_bring_its_service_up(double up, string expected)
    {
        var reason = NigelChat.ReasonNotToStart(LabViewState.Take(Machine(102, up), @"X:\n"));
        Assert.Contains(expected, reason);
    }

    [Fact]
    public void A_running_chat_or_a_dialog_or_two_labviews_means_no_start()
    {
        var chat = Machine(103, 300);
        chat.AddProcess(7, "LVNigelChat");
        Assert.Contains("already runs", NigelChat.ReasonNotToStart(LabViewState.Take(chat, @"X:\n")));

        var dialog = Machine(104, 300);
        dialog.AddWindow(104, "LVDChild", "Error list", false);
        Assert.Contains("dialog", NigelChat.ReasonNotToStart(LabViewState.Take(dialog, @"X:\n")));

        var two = Machine(105, 300);
        two.AddProcess(106, "LabVIEW");
        Assert.Contains("2 LabVIEW", NigelChat.ReasonNotToStart(LabViewState.Take(two, @"X:\n")));

        Assert.Null(NigelChat.ReasonNotToStart(LabViewState.Take(Machine(107, 300), @"X:\n")));
    }

    [Fact]
    public async Task A_start_is_made_once_and_a_message_box_is_named_not_pressed()
    {
        NigelChat.Reset();
        var m = Machine(110, 300);
        var (target, _) = NigelChat.Resolve(m, m.ProcessList[0], Root);
        var launches = new List<string>();

        Task<LaunchOutcome> Launch(string exe, string args, CancellationToken ct)
        {
            launches.Add($"{exe} {args}");
            m.AddProcess(900, "LVNigelChat");
            m.AddWindow(900, "#32770", "LVNigelChat", true, handle: 901);
            m.ChildMap[901] = [new("Button", "OK"), new("Static", "Object reference not set to an instance of an object.")];
            return Task.FromResult(new LaunchOutcome(true, "breakaway", 900, []));
        }

        var report = await NigelChat.StartAsync(m, m.ProcessList[0], target!, Launch, default);

        Assert.Equal("messageBox", report["state"]!.GetValue<string>());
        Assert.Single(launches);
        Assert.Contains("--lv-version 26.3 --lv-bitness x86", launches[0]);
        Assert.Contains("nothing was pressed", report["note"]!.GetValue<string>());

        // Once: the attempt is remembered for this LabVIEW, even with the chat process gone.
        m.ProcessList.RemoveAll(p => p.Id == 900);
        Assert.Contains("already started once", NigelChat.ReasonNotToStart(LabViewState.Take(m, @"X:\n")));
        Assert.Equal("messageBox", NigelChat.AttemptFor(110)!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_chat_that_did_not_survive_or_exited_is_reported_as_such()
    {
        NigelChat.Reset();
        var m = Machine(120, 300);
        var (target, _) = NigelChat.Resolve(m, m.ProcessList[0], Root);

        var failed = await NigelChat.StartAsync(m, m.ProcessList[0], target!,
            (_, _, _) => Task.FromResult(new LaunchOutcome(false, null, null,
                [new LaunchAttempt("breakaway", true, true, false, "pid 1")])), default);
        Assert.Equal("didNotSurvive", failed["state"]!.GetValue<string>());

        var m2 = Machine(121, 300);
        var exited = await NigelChat.StartAsync(m2, m2.ProcessList[0], target!,
            (_, _, _) => Task.FromResult(new LaunchOutcome(true, "shell", 950, [])), default);
        Assert.Equal("exited", exited["state"]!.GetValue<string>());
    }
}
