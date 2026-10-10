// =============================================================================
// DialogAnswerTests.cs - the dialog allowlist on a fake machine: what is refused, what
// is pressed and how, and that a button is clicked only where its picture is found.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class DialogAnswerTests
{
    private const int Lv = 20592;
    private const long Prompt = 7736030;
    private const string Note = "started by lvai_ensure_labview in this session, 14:03:05";

    /// <summary>The measured 14:12 world: frame, two hidden scratch windows, the exit prompt.</summary>
    private static FakeLabViewMachine World(bool withPicture = true, string title = DialogAnswer.ExitSaveTitle)
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(Lv, "LabVIEW");
        m.AddWindow(Lv, "LVFrame", "LabVIEW", true, handle: 61, enabled: false);
        m.AddWindow(Lv, "LVDChild", "LVMCP Validate.vi Front Panel *", false, owner: 61);
        m.AddWindow(Lv, "LVDChild", "LVMCP Validate.vi Block Diagram *", false, owner: 61);
        m.AddWindow(Lv, "LVDChild", title, true, handle: Prompt, owner: 61);
        if (withPicture) m.Images[Prompt] = PromptPicture();
        return m;
    }

    /// <summary>A 412x249 grey dialog with the shipped "Don't Save" picture where it was measured.</summary>
    private static WindowImage PromptPicture()
    {
        var button = DialogButtons.For(DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave)!;
        var rgb = Enumerable.Repeat((byte)240, 412 * 249 * 3).ToArray();
        for (var y = 0; y < button.Height; y++)
            Array.Copy(button.Rgb, y * button.Width * 3, rgb, ((174 + y) * 412 + 34) * 3, button.Width * 3);
        return new WindowImage(412, 249, rgb);
    }

    private static Task<JsonObject> Ask(FakeLabViewMachine m, string kind, string action, long handle = Prompt,
        int? pid = Lv, string? note = Note, string? listed = "LVMCP Validate.vi", string? vi = null,
        Func<int, bool>? chats = null) =>
        DialogAnswer.RunAsync(new DialogAnswerRequest(handle, kind, action, pid, note, listed, vi), m,
                              chats ?? (_ => false), default);

    private static string Kind(JsonObject a) => a["errorKind"]?.GetValue<string>() ?? "";

    [Fact]
    public void The_shipped_dont_save_picture_decodes()
    {
        var b = DialogButtons.For(DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave);
        Assert.Equal((58, 15), (b!.Width, b.Height));
        Assert.Null(DialogButtons.For(DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSaveAll));
    }

    [Fact]
    public async Task Dont_save_clicks_the_centre_of_the_found_picture_and_waits_for_the_exit()
    {
        var m = World();
        m.OnInput = _ => { m.WindowList.Clear(); m.ProcessList.Clear(); };
        var a = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave);

        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal([$"click 63,181 @{Prompt}"], m.Inputs);       // the measured 14:15 click
        Assert.True(a["dialogGone"]!.GetValue<bool>());
        Assert.True(a["processExited"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Cancel_posts_esc_and_needs_no_list()
    {
        var m = World();
        m.OnInput = _ => m.WindowList.RemoveAll(w => w.Handle == Prompt);
        var a = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.Cancel, listed: null);

        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal([$"key 27 @{Prompt}"], m.Inputs);
        Assert.Null(a["processExited"]);
    }

    [Fact]
    public async Task Somebody_elses_labview_is_refused_with_the_screenshot()
    {
        var m = World();
        var noPid = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave, pid: null);
        var noNote = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave, note: " ");
        var otherPid = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave, pid: 1);

        Assert.All(new[] { noPid, noNote, otherPid }, a => Assert.Equal("notOwnLabview", Kind(a)));
        Assert.Empty(m.Inputs);
    }

    [Theory]
    [InlineData("Main.vi")]                                // the user's VI in the list
    [InlineData("")]                                       // nothing read from the picture
    [InlineData("LVMCP Validate.vi\nPilot.lvclass:Pass.vi")]
    public async Task Dont_save_needs_every_listed_vi_to_be_a_scratch_vi(string listed)
    {
        var m = World();
        var a = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave, listed: listed);
        Assert.Equal("notAllowlisted", Kind(a));
        Assert.Empty(m.Inputs);
    }

    [Fact]
    public async Task Dont_save_is_refused_while_a_users_window_is_unsaved()
    {
        var m = World();
        m.AddWindow(Lv, "LVDChild", "Pass.vi Front Panel *", true, owner: 61, enabled: false);
        var a = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave);
        Assert.Equal("notAllowlisted", Kind(a));
        Assert.Contains("Pass.vi", a["error"]!.GetValue<string>());
        Assert.Empty(m.Inputs);
    }

    [Fact]
    public async Task Another_title_kind_or_action_is_not_on_the_allowlist()
    {
        var other = World(title: "Save changes before closing?");
        Assert.Equal("notAllowlisted", Kind(await Ask(other, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave)));

        var m = World();
        Assert.Equal("notAllowlisted", Kind(await Ask(m, "findTheVi", DialogAnswer.Cancel)));
        Assert.Equal("notAllowlisted", Kind(await Ask(m, DialogAnswer.ExitSaveScratchOnly, "Save")));
        Assert.Empty(m.Inputs);
        Assert.Empty(other.Inputs);
    }

    [Fact]
    public async Task No_picture_or_picture_not_found_presses_nothing()
    {
        var all = World();
        Assert.Equal("buttonNotFound", Kind(await Ask(all, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSaveAll)));

        var plain = World(withPicture: false);
        plain.Images[Prompt] = new WindowImage(412, 249, Enumerable.Repeat((byte)240, 412 * 249 * 3).ToArray());
        Assert.Equal("buttonNotFound", Kind(await Ask(plain, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave)));
        Assert.Empty(all.Inputs);
        Assert.Empty(plain.Inputs);
    }

    [Fact]
    public async Task A_press_that_does_not_take_is_reported_with_an_after_picture()
    {
        var m = World();   // OnInput does nothing: the dialog stays
        var a = await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.Cancel);
        Assert.False(a["ok"]!.GetValue<bool>());
        Assert.Equal("dialogStillThere", Kind(a));
        Assert.Single(m.Inputs);   // nothing else was tried
    }

    [Fact]
    public async Task No_such_window_is_an_error_not_a_press()
    {
        var m = World();
        Assert.Equal("noSuchDialog", Kind(await Ask(m, DialogAnswer.ExitSaveScratchOnly, DialogAnswer.Cancel, handle: 5)));
    }

    [Fact]
    public async Task Changed_on_disk_cancels_the_untitled_modal_of_a_named_vi_only()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(Lv, "LabVIEW");
        m.AddWindow(Lv, "LVFrame", "LabVIEW", true, handle: 61, enabled: false);
        m.AddWindow(Lv, "LVDChild", "", true, handle: 50, owner: 61);
        m.OnInput = _ => m.WindowList.RemoveAll(w => w.Handle == 50);

        Assert.Equal("notAllowlisted", Kind(await Ask(m, DialogAnswer.ChangedOnDisk, DialogAnswer.Cancel, handle: 50)));
        Assert.Equal("notAllowlisted", Kind(await Ask(m, DialogAnswer.ChangedOnDisk, "Revert", handle: 50, vi: "A.vi")));
        Assert.Empty(m.Inputs);

        var a = await Ask(m, DialogAnswer.ChangedOnDisk, DialogAnswer.Cancel, handle: 50, vi: "A.vi");
        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal(["key 27 @50"], m.Inputs);
    }

    [Fact]
    public async Task A_titled_window_is_not_the_changed_on_disk_modal()
    {
        var m = World();
        Assert.Equal("notAllowlisted", Kind(await Ask(m, DialogAnswer.ChangedOnDisk, DialogAnswer.Cancel, vi: "A.vi")));
    }

    [Fact]
    public async Task A_nigel_box_gets_bm_click_only_when_this_server_started_the_chat()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(900, "LVNigelChat");
        m.AddWindow(900, "#32770", "LVNigelChat", true, handle: 88);
        m.ChildMap[88] = [new("Static", "Object reference not set to an instance of an object."), new("Button", "OK", 89)];
        m.OnInput = _ => m.WindowList.RemoveAll(w => w.Handle == 88);

        Assert.Equal("notOwnLabview", Kind(await Ask(m, DialogAnswer.NigelBox, DialogAnswer.Ok, handle: 88)));
        Assert.Empty(m.Inputs);

        var a = await Ask(m, DialogAnswer.NigelBox, DialogAnswer.Ok, handle: 88, chats: pid => pid == 900);
        Assert.True(a["ok"]!.GetValue<bool>());
        Assert.Equal(["button 89"], m.Inputs);
    }

    [Fact]
    public void A_picture_found_in_two_separate_places_is_not_found()
    {
        var needle = new WindowImage(2, 1, [10, 10, 10, 200, 200, 200]);
        var hay = new WindowImage(6, 1, [10, 10, 10, 200, 200, 200, 0, 0, 0, 0, 0, 0, 10, 10, 10, 200, 200, 200]);
        Assert.Null(DialogButtons.Find(hay, needle));
        var once = new WindowImage(6, 1, [10, 10, 10, 200, 200, 200, 0, 0, 0, 0, 0, 0, 90, 90, 90, 90, 90, 90]);
        Assert.Equal((0, 0), DialogButtons.Find(once, needle));
    }
}
