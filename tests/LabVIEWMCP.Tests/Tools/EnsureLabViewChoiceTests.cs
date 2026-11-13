// =============================================================================
// EnsureLabViewChoiceTests.cs - lvai_ensure_labview's per-call `labview` argument:
// which installation an exe path or a release year names.
// =============================================================================
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class EnsureLabViewChoiceTests
{
    private static readonly LabViewInstall[] Installs =
    [
        new(@"C:\Program Files\National Instruments\LabVIEW 2024\LabVIEW.exe", 2024, false, "LabVIEW 2024"),
        new(@"C:\Program Files (x86)\National Instruments\LabVIEW 2020\LabVIEW.exe", 2020, true, "LabVIEW 2020"),
        new(@"C:\Program Files (x86)\National Instruments\LabVIEW 2026\LabVIEW.exe", 2026, true, "LabVIEW 2026"),
        new(@"C:\Program Files\National Instruments\LabVIEW 2026\LabVIEW.exe", 2026, false, "LabVIEW 2026"),
    ];

    [Fact]
    public void A_release_year_names_that_release_32_bit_first()
    {
        var (install, problem) = LifecycleTools.RequestedInstall("2026", Installs);
        Assert.Null(problem);
        Assert.True(install!.Is32Bit);
        Assert.Equal(2020, LifecycleTools.RequestedInstall(" 2020 ", Installs).Install!.Release);
    }

    [Fact]
    public void An_exe_path_names_exactly_that_installation()
    {
        var (install, _) = LifecycleTools.RequestedInstall(
            "\"c:\\program files\\national instruments\\labview 2024\\LabVIEW.exe\"", Installs);
        Assert.Equal(2024, install!.Release);
        Assert.False(install.Is32Bit);
    }

    [Theory]
    [InlineData("2018")]
    [InlineData(@"C:\LabVIEW 2018\LabVIEW.exe")]
    public void Something_not_installed_is_refused_with_what_is(string text)
    {
        var (install, problem) = LifecycleTools.RequestedInstall(text, Installs);
        Assert.Null(install);
        Assert.Contains("LabVIEW 2020", problem);
    }
}
