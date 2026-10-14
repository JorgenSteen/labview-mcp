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

    /// <summary>
    /// The shipped Don't Save template, decoded. The expected hash was taken 2026-10-14 with an
    /// independent decoder (Python zlib over the base64 in DialogButtons.cs), so a change of
    /// decoder cannot move the pixels the button search compares against.
    /// </summary>
    [Fact]
    public void The_shipped_button_template_decodes_to_its_measured_pixels()
    {
        var button = DialogButtons.For(DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave)!;
        Assert.Equal((58, 15), (button.Width, button.Height));
        Assert.Equal("94de7ba35f4fe0e9f634c7db6b873413e04e0bd6dad9c1f9ea49cfbf3a5c7bb7",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(button.Rgb)).ToLowerInvariant());
    }

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
