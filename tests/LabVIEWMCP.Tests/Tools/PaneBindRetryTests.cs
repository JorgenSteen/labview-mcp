// PaneBindRetryTests.cs - when lvai_bind_pane_typedef binds again through NI's service: only an unverified default-route run.
//
// Measured 2026-11-23 on LabVIEW 2026: the ActiveX route binds (verified true), so the default
// route runs first; a saved pane without the binding is the one case retried.
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class PaneBindRetryTests
{
    [Fact]
    public void Only_a_saved_pane_without_the_binding_on_the_default_route_is_retried()
    {
        if (LabVIEWMcp.Infra.AixmlBackend.ServerDefault().Name != LabVIEWMcp.Infra.AixmlBackend.PyLabview) return;
        Assert.True(TypedefTools.ShouldRetryThroughService(null, """{"ok":false,"verified":false}"""));
        Assert.False(TypedefTools.ShouldRetryThroughService(null, """{"ok":true,"verified":true}"""));
        // null: the pane could not be read - a second run would not tell more
        Assert.False(TypedefTools.ShouldRetryThroughService(null, """{"ok":false,"verified":null}"""));
        Assert.False(TypedefTools.ShouldRetryThroughService(null, """{"ok":false,"errorKind":"badArguments"}"""));
        Assert.False(TypedefTools.ShouldRetryThroughService(null, "not json"));
        // a call that named its backend gets exactly that backend
        Assert.False(TypedefTools.ShouldRetryThroughService("pylabview", """{"verified":false}"""));
        Assert.False(TypedefTools.ShouldRetryThroughService("grpc", """{"verified":false}"""));
    }
}
