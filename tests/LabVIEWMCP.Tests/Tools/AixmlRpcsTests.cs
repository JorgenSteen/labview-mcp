// =============================================================================
// AixmlRpcsTests.cs - lvai_status / pylv_status tell a LISTED lvai.LVAI service from one that
// implements the AIXML RPCs (field report H11b): LabVIEW 2025 lists it with 11 RPCs, none AIXML.
// =============================================================================
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class AixmlRpcsTests
{
    // The 11 RPCs LabVIEW 2025's lvai.LVAI declared, read by lvai_dump_schema 2026-11-14.
    private static readonly string[] LabView2025 =
    [
        "GetDescribeVIPromptInfo", "FilterPaletteSearchCandidates", "FilterExampleSearchCandidates",
        "MonitorDiscussVI", "MonitorPaletteSearches", "MonitorExampleSearches", "LogUsageData",
        "GetApplicationConfiguration", "OpenFile", "FindPaletteItem", "DropPaletteItem",
    ];

    [Fact]
    public void LabVIEW_2025_lacks_every_AIXML_RPC() =>
        Assert.Equal(["ValidateAIXML", "ConvertAIXMLToVI", "ConvertVIToAIXML"],
                     StatusTools.MissingAixmlRpcs(LabView2025));

    [Fact]
    public void A_service_with_the_AIXML_RPCs_lacks_none() =>
        Assert.Empty(StatusTools.MissingAixmlRpcs(
            [.. LabView2025, "ValidateAIXML", "ConvertAIXMLToVI", "ConvertVIToAIXML", "ApplyAIXMLToVI"]));
}
