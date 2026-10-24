using D2Translator.Domain.Geometry;

namespace D2Translator.Domain.Tests.Geometry;

public class NormalizedRectTests
{
    private static readonly PixelSize Fhd = new(1920, 1080);

    [Fact]
    public void Full_covers_whole_image()
    {
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), NormalizedRect.Full.ToPixels(Fhd));
    }

    [Fact]
    public void ToPixels_does_not_grow_by_floating_point_error()
    {
        // 0.6 * 1920 = 1152.0000000000002 のような誤差で 1 ピクセル広がらない
        var rect = new NormalizedRect(0.1, 0.5, 0.5, 0.25);

        Assert.Equal(new PixelRect(192, 540, 960, 270), rect.ToPixels(Fhd));
    }

    [Fact]
    public void ToPixels_rounds_partial_pixels_outward()
    {
        var rect = new NormalizedRect(10.5 / 1920, 20.5 / 1080, 100.0 / 1920, 30.0 / 1080);

        Assert.Equal(new PixelRect(10, 20, 101, 31), rect.ToPixels(Fhd));
    }

    [Fact]
    public void FromPixels_round_trips()
    {
        var pixels = new PixelRect(33, 700, 960, 360);

        NormalizedRect normalized = NormalizedRect.FromPixels(pixels, Fhd);

        Assert.True(normalized.IsValid);
        Assert.Equal(pixels, normalized.ToPixels(Fhd));
    }

    [Fact]
    public void Same_normalized_rect_scales_to_other_resolution()
    {
        NormalizedRect normalized = NormalizedRect.FromPixels(new PixelRect(0, 540, 960, 540), Fhd);

        Assert.Equal(new PixelRect(0, 720, 1280, 720), normalized.ToPixels(new PixelSize(2560, 1440)));
    }

    [Theory]
    [InlineData(-0.1, 0, 0.5, 0.5)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(0.6, 0, 0.5, 0.5)]
    [InlineData(0, 0.9, 0.5, 0.2)]
    [InlineData(double.NaN, 0, 0.5, 0.5)]
    public void Invalid_rects_are_rejected(double x, double y, double w, double h)
    {
        var rect = new NormalizedRect(x, y, w, h);

        Assert.False(rect.IsValid);
        Assert.Throws<InvalidOperationException>(() => rect.ToPixels(Fhd));
    }

    [Fact]
    public void FromPixels_rejects_rect_outside_image()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NormalizedRect.FromPixels(new PixelRect(1900, 0, 100, 10), Fhd));
    }

    [Fact]
    public void FromCorners_orders_and_clamps_points()
    {
        PixelRect rect = PixelRect.FromCorners(500.4, 300.6, -20, 1200, Fhd);

        Assert.Equal(new PixelRect(0, 300, 501, 780), rect);
    }
}
