using D2Translator.Domain.Capture;
using D2Translator.Domain.Geometry;

namespace D2Translator.Domain.Tests.Capture;

public class RoiRulesTests
{
    private static CaptureProfile Profile(NormalizedRect roi, PixelSize frame) => new()
    {
        Id = Guid.NewGuid(),
        Roi = roi,
        FrameSize = frame,
    };

    [Fact]
    public void Same_aspect_ratio_at_higher_resolution_is_accepted()
    {
        CaptureProfile profile = Profile(new NormalizedRect(0, 0.6, 0.4, 0.3), new PixelSize(1920, 1080));

        RoiCheck check = RoiRules.Check(profile, new PixelSize(2560, 1440));

        Assert.True(check.IsOk);
        Assert.Equal(new PixelRect(0, 864, 1024, 432), check.PixelRoi);
    }

    [Fact]
    public void Changed_aspect_ratio_requires_reconfiguration()
    {
        CaptureProfile profile = Profile(new NormalizedRect(0, 0.6, 0.4, 0.3), new PixelSize(1920, 1080));

        RoiCheck check = RoiRules.Check(profile, new PixelSize(3440, 1440));

        Assert.Equal(RoiCheckStatus.AspectRatioChanged, check.Status);
    }

    [Fact]
    public void Window_border_difference_within_tolerance_is_accepted()
    {
        CaptureProfile profile = Profile(new NormalizedRect(0, 0.6, 0.4, 0.3), new PixelSize(1920, 1080));

        Assert.True(RoiRules.Check(profile, new PixelSize(1922, 1081)).IsOk);
    }

    [Fact]
    public void Empty_frame_is_rejected()
    {
        CaptureProfile profile = Profile(NormalizedRect.Full, new PixelSize(1920, 1080));

        Assert.Equal(RoiCheckStatus.EmptyFrame, RoiRules.Check(profile, new PixelSize(0, 0)).Status);
    }

    [Fact]
    public void Too_small_roi_is_rejected()
    {
        CaptureProfile profile = Profile(new NormalizedRect(0, 0, 0.01, 0.005), new PixelSize(1920, 1080));

        Assert.Equal(RoiCheckStatus.TooSmall, RoiRules.Check(profile, new PixelSize(1920, 1080)).Status);
    }

    [Fact]
    public void Roi_over_ocr_limit_is_rejected()
    {
        CaptureProfile profile = Profile(NormalizedRect.Full, new PixelSize(1920, 1080));

        Assert.Equal(RoiCheckStatus.ExceedsOcrLimit,
            RoiRules.Check(profile, new PixelSize(1920, 1080), ocrMaxImageDimension: 1000).Status);
    }

    [Fact]
    public void Invalid_roi_is_rejected()
    {
        CaptureProfile profile = Profile(new NormalizedRect(0.8, 0, 0.5, 0.5), new PixelSize(1920, 1080));

        Assert.Equal(RoiCheckStatus.InvalidRoi, RoiRules.Check(profile, new PixelSize(1920, 1080)).Status);
    }
}
