// =============================================================================
// WindowShotTests.cs - the dependency-free PNG round trip and the dialog screenshot
// file, on a fake machine (no window is captured for real here).
// =============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class WindowShotTests
{
    private static WindowImage Gradient(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 7 % 251);
        return new WindowImage(w, h, rgb);
    }

    [Fact]
    public void A_picture_survives_the_png_round_trip_byte_for_byte()
    {
        var image = Gradient(37, 11);
        var back = WindowShot.DecodePng(WindowShot.EncodePng(image));
        Assert.NotNull(back);
        Assert.Equal(37, back!.Width);
        Assert.Equal(11, back.Height);
        Assert.Equal(image.Rgb, back.Rgb);
    }

    [Fact]
    public void A_crop_is_the_rectangle_it_names()
    {
        var image = Gradient(20, 10);
        var crop = image.Crop(3, 2, 5, 4);
        Assert.Equal(image.At(3, 2), crop.At(0, 0));
        Assert.Equal(image.At(7, 5), crop.At(4, 3));
    }

    [Fact]
    public void Garbage_is_not_a_png() => Assert.Null(WindowShot.DecodePng([1, 2, 3]));

    [Fact]
    public void A_captured_dialog_is_saved_and_a_window_that_cannot_be_captured_gives_no_path()
    {
        var m = new FakeLabViewMachine();
        m.AddProcess(10, "LabVIEW");
        var dialog = m.AddWindow(10, "LVDChild", "Save changes? (Exit)", true, handle: 77);
        var dir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(WindowShot.Save(m, dialog, dir));
            m.Images[77] = Gradient(8, 4);
            var path = WindowShot.Save(m, dialog, dir, "before");
            Assert.NotNull(path);
            Assert.EndsWith("_10_77_before.png", path);
            Assert.Equal(8, WindowShot.DecodePng(File.ReadAllBytes(path!))!.Width);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
