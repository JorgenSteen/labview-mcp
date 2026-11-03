// ============================================================================
// lvai_check_aixml warns when a DQMH call is not wired before Stop Module (session-7 report M7).
// ============================================================================
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Session-7 report M7, 2026-11-03: every Request and Wait for Reply of a headless caller timed
/// out (403686) - on the user's module and on Delacor's untouched template - and the MCP's run
/// route was suspected. The FIXTURE IS THE USER'S CALLER, verbatim (driverupdate
/// mcp_temp\s7\run\s7_run_fresh.xml): Stop Module takes its `error in` from Synchronize Module
/// Events (111.err), beside the request, so nothing orders the two. Measured on LabVIEW 2026 over
/// the same ActiveX run route: this file, 403686 after 5.4 s; with Stop Module on 113.err, the
/// reply in 1.2 s, twice. The lint over 2563 documents (the repository and every cached NI export,
/// 11 of them calling Stop Module) flags none.
/// </summary>
public sealed class StopModuleCheckTests
{
    private const string UsersCaller = """
        <VI _name="s7_run_fresh.vi" description="Session-7 control run on a fresh, untouched DQMH 7.1.2 cloneable module (S7Probe, Delacor template with its examples)\3A Start Module + Synchronize Module Events\2C Delacor's own pre-coded Do Something Else and Wait for Reply (X = 2)\2C then Stop Module on the sync error chain.">
          <Constant _name="wait for stop" type="bool" uid="102" uid_parent="root" value="true" outputs="value:102.value"/>
          <Constant _name="origin" type="string" uid="103" uid_parent="root" value="s7 fresh" outputs="value:103.value"/>
          <Constant _name="message" type="string" uid="104" uid_parent="root" value="hello" outputs="value:104.value"/>
          <Constant _name="x" type="double" uid="105" uid_parent="root" value="2" outputs="value:105.value"/>
          <Call target="S7Probe.lvlib\3AStart Module.vi" inputs="error in:,Run as Singleton? (F):" outputs="error out:110.err,S7Probe Broadcast Events:,Wait for Event Sync?:110.sync,Module ID:110.id,Module Name:110.name" uid="110" uid_parent="root"/>
          <Call target="S7Probe.lvlib\3ASynchronize Module Events.vi" inputs="Wait for Event Sync?:110.sync,error in:110.err,Module ID in:110.id" outputs="error out:111.err,Module ID out:111.id" uid="111" uid_parent="root"/>
          <Call target="S7Probe.lvlib\3ADo Something Else and Wait for Reply.vi" inputs="error in:111.err,Do Something Else Message:104.value,X:105.value,Module ID:111.id" outputs="error out:113.err,timed out?:113.to,Value:113.val" uid="113" uid_parent="root"/>
          <Call target="S7Probe.lvlib\3AStop Module.vi" inputs="error in:111.err,Origin:103.value,Module ID:111.id,Wait for Module to stop? (F):102.value" outputs="error out:114.err" uid="114" uid_parent="root"/>
          <Node _name="Unbundle By Name" fields="status,code,source" inputs="input cluster:113.err" outputs="status:121.s,code:121.c,source:121.src" uid="121" uid_parent="root"/>
          <Node _name="Unbundle By Name" fields="status,code,source" inputs="input cluster:114.err" outputs="status:122.s,code:122.c,source:122.src" uid="122" uid_parent="root"/>
          <Indicator _name="module id" inputs="value:111.id" type="int32" uid="201" uid_parent="root" value="0"/>
          <Indicator _name="reply value" inputs="value:113.val" type="double" uid="203" uid_parent="root" value="0"/>
          <Indicator _name="reply error" inputs="value:121.s" type="bool" uid="204" uid_parent="root" value="false"/>
          <Indicator _name="reply code" inputs="value:121.c" type="int32" uid="205" uid_parent="root" value="0"/>
          <Indicator _name="reply source" inputs="value:121.src" type="string" uid="206" uid_parent="root" value=""/>
          <Indicator _name="timed out" inputs="value:113.to" type="bool" uid="207" uid_parent="root" value="false"/>
          <Indicator _name="stop error code" inputs="value:122.c" type="int32" uid="208" uid_parent="root" value="0"/>
        </VI>
        """;

    private static readonly string Fixed = UsersCaller.Replace(
        "inputs=\"error in:111.err,Origin:", "inputs=\"error in:113.err,Origin:", StringComparison.Ordinal);

    private static List<AixmlCheck.Finding> StopFindings(string xml) =>
        AixmlCheck.Check(xml).Where(f => f.Code == AixmlCheck.StopModuleNotLast).ToList();

    [Fact]
    public void The_users_caller_is_flagged_on_its_request_and_only_there()
    {
        var finding = Assert.Single(StopFindings(UsersCaller));
        Assert.Equal(AixmlCheck.Severity.Warning, finding.Severity);
        Assert.Equal("113", finding.Uid);
        Assert.Contains("Do Something Else and Wait for Reply.vi", finding.Message);
        Assert.Contains("run in parallel", finding.Message);
    }

    [Fact]
    public void Stop_Module_wired_after_the_request_is_not_flagged()
    {
        Assert.Empty(StopFindings(Fixed));
    }

    [Fact]
    public void A_call_wired_after_Stop_Module_is_flagged_as_running_after_it()
    {
        var after = Fixed
            .Replace("inputs=\"error in:111.err,Do Something Else Message:", "inputs=\"error in:114.err,Do Something Else Message:", StringComparison.Ordinal)
            .Replace("inputs=\"error in:113.err,Origin:", "inputs=\"error in:111.err,Origin:", StringComparison.Ordinal);

        var finding = Assert.Single(StopFindings(after));
        Assert.Contains("AFTER the module was stopped", finding.Message);
    }

    /// <summary>A request inside a structure is ordered by the structure's own wires.</summary>
    [Fact]
    public void A_request_inside_a_structure_upstream_of_Stop_Module_is_not_flagged()
    {
        const string xml = """
            <VI _name="loop.vi" description="Requests in a loop, then Stop Module on the loop's error out.">
              <Call target="M.lvlib\3AStart Module.vi" inputs="error in:" outputs="error out:4200.err,Module ID:4200.id" uid="4200" uid_parent="root"/>
              <Structure _name="For Loop" count="" uid="4210" uid_parent="root">
                <ShiftReg uid="4211" uid_parent="4210">
                  <Left inputs="value:4200.err" outputs="value:4212.err" uid="4212" uid_parent="4211"/>
                  <Right inputs="value:4220.err" outputs="value:4213.err" uid="4213" uid_parent="4211"/>
                </ShiftReg>
                <Call target="M.lvlib\3AGet Value.vi" inputs="error in:4212.err" outputs="error out:4220.err" uid="4220" uid_parent="4210"/>
              </Structure>
              <Call target="M.lvlib\3AStop Module.vi" inputs="error in:4213.err" outputs="error out:4230.err" uid="4230" uid_parent="root"/>
            </VI>
            """;
        Assert.Empty(StopFindings(xml));
        Assert.Single(StopFindings(xml.Replace("inputs=\"error in:4213.err\"", "inputs=\"error in:4200.err\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_python_lint_carries_the_same_rule()
    {
        var lint = File.ReadAllText(Path.Combine(Support.RepoTree.Root, "scripts", "aixml_lint.py"));
        Assert.Contains("stop-module-not-last", lint);
        Assert.Contains("Kept equal to AixmlCheck.CheckStopModuleNotLast", lint);
    }
}
