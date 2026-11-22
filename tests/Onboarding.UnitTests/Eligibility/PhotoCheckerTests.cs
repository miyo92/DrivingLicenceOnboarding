using Onboarding.Core.Applications;
using Onboarding.Core.Eligibility;

namespace Onboarding.UnitTests.Eligibility;

public class PhotoCheckerTests
{
    private readonly PhotoChecker _checker = new(new EligibilityOptions());

    private static PhotoMetadata Photo(string contentType = "image/jpeg", long size = 20_000) => new("key", contentType, size);

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("IMAGE/PNG")]
    public void Supported_photo_with_good_score_passes(string contentType)
    {
        Assert.Null(_checker.Check(Photo(contentType), 85));
    }

    [Fact]
    public void Missing_photo_fails()
    {
        Assert.Equal("No photo provided", _checker.Check(null, null));
    }

    [Fact]
    public void Unsupported_type_fails_naming_the_type()
    {
        Assert.Equal("Unsupported photo content type: image/gif", _checker.Check(Photo("image/gif"), null));
    }

    [Fact]
    public void Too_small_fails()
    {
        Assert.Equal("Photo is too small: 10239 bytes (minimum 10240)", _checker.Check(Photo(size: 10239), 85));
        Assert.Null(_checker.Check(Photo(size: 10240), 85));
    }

    [Fact]
    public void Low_quality_fails_below_minimum_only()
    {
        Assert.Equal("Photo quality score 39 is below minimum 40", _checker.Check(Photo(), 39));
        Assert.Null(_checker.Check(Photo(), 40));
    }

    [Fact]
    public void Stops_at_first_failure_in_rule_order()
    {
        // Wrong type, too small and low score: only the type failure is reported.
        Assert.Equal("Unsupported photo content type: image/gif", _checker.Check(Photo("image/gif", 5), 0));
        // Too small and low score: only the size failure is reported.
        Assert.Equal("Photo is too small: 5 bytes (minimum 10240)", _checker.Check(Photo(size: 5), 0));
    }

    [Fact]
    public void Missing_score_for_present_supported_photo_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => _checker.Check(Photo(), null));
    }

    [Fact]
    public void Thresholds_and_types_come_from_options()
    {
        var checker = new PhotoChecker(new EligibilityOptions
        {
            SupportedContentTypes = ["image/webp"],
            MinPhotoSizeBytes = 100,
            MinPhotoQualityScore = 90,
        });

        Assert.Equal("Unsupported photo content type: image/jpeg", checker.Check(Photo("image/jpeg"), 95));
        Assert.Equal("Photo is too small: 99 bytes (minimum 100)", checker.Check(Photo("image/webp", 99), 95));
        Assert.Equal("Photo quality score 89 is below minimum 90", checker.Check(Photo("image/webp", 100), 89));
        Assert.Null(checker.Check(Photo("image/webp", 100), 90));
    }
}
