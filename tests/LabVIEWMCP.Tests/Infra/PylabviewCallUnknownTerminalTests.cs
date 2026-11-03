using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A Call that LISTS an unwired terminal its callee's pane does not have - one a newer release of
/// the callee added - composes since 2026-10-30. Clear Errors.vi's `specific error code to clear
/// (0: all)` is not on LabVIEW 2020's, and the AF hook helpers (lvai_af_hook_read, lvai_af_add_hook,
/// lvai_af_remove_hook) all list it unwired, so lvai_af_add_hook could not run on 2020 at all ("Call
/// 'Clear Errors.vi' has no input ... on its pane"). A WIRED unknown terminal is still refused: it
/// would drop a wire. These run the bundled interpreter and SKIP without it.
/// </summary>
public sealed class PylabviewCallUnknownTerminalTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private const string Callee = """
        <VI _name="Callee.vi" description="Passes its error through.">
          <Control _name="error in" conIdx="8" connection="recommended" type="cluster{bool.status,int32.code,string.source}" value="[false,0,]" outputs="value:4101.v" uid="4101" uid_parent="root"/>
          <Indicator _name="error out" conIdx="0" connection="recommended" type="cluster{bool.status,int32.code,string.source}" value="[false,0,]" inputs="value:4101.v" uid="4102" uid_parent="root"/>
        </VI>
        """;

    private static string Caller(string extraInput) => $$"""
        <VI _name="Caller.vi" description="Calls Callee.vi, listing a terminal it does not have.">
          <Control _name="error in" conIdx="8" connection="recommended" type="cluster{bool.status,int32.code,string.source}" value="[false,0,]" outputs="value:4201.v" uid="4201" uid_parent="root"/>
          <Control _name="code" conIdx="4" connection="recommended" type="int32" value="0" outputs="value:4203.v" uid="4203" uid_parent="root"/>
          <Call target="Callee.vi" inputs="{{extraInput}},error in:4201.v" outputs="newer output:,error out:4210.err" uid="4210" uid_parent="root"/>
          <Indicator _name="error out" conIdx="0" connection="recommended" type="cluster{bool.status,int32.code,string.source}" value="[false,0,]" inputs="value:4210.err" uid="4202" uid_parent="root"/>
        </VI>
        """;

    private static async Task<string> ComposeAsync(LvaiTestServer server, string extraInput, string name)
    {
        var tools = new AixmlTools(server.Connection);
        var calleeXml = server.TempPath(name + "-callee.xml");
        await File.WriteAllTextAsync(calleeXml, Callee);
        var callee = await tools.ConvertAixmlToViAsync(
            calleeXml, server.TempPath("Callee.vi"), checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(callee, "errorCode"));

        var callerXml = server.TempPath(name + "-caller.xml");
        await File.WriteAllTextAsync(callerXml, Caller(extraInput));
        return await tools.ConvertAixmlToViAsync(
            callerXml, server.TempPath(name + ".vi"), checkExecutable: false, backend: "pylabview");
    }

    [Fact]
    public async Task An_unwired_terminal_the_callee_does_not_have_is_skipped()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();

        var composed = await ComposeAsync(server, "newer input (0\\3A all):", "unwired");

        Assert.Equal(0, Res.Int(composed, "errorCode"));
    }

    [Fact]
    public async Task A_wired_terminal_the_callee_does_not_have_is_still_refused()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();

        var composed = await ComposeAsync(server, "newer input (0\\3A all):4203.v", "wired");

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains("has no input 'newer input (0: all)'", Res.Str(composed, "errorMessage"));
    }
}
