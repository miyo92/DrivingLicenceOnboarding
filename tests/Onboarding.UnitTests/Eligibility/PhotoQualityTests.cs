using Onboarding.Core.Eligibility;

namespace Onboarding.UnitTests.Eligibility;

public class PhotoQualityTests
{
    [Fact]
    public void Same_bytes_always_give_same_score()
    {
        var bytes = new byte[20_000];
        new Random(42).NextBytes(bytes);

        Assert.Equal(PhotoQuality.Score(bytes), PhotoQuality.Score(bytes.ToArray()));
    }

    [Fact]
    public void Score_follows_sha256_prefix_mod_101()
    {
        // SHA-256("") starts with e3b0c442 = 3820012610; 3820012610 % 101 = 3.
        Assert.Equal(3, PhotoQuality.Score([]));
    }

    [Fact]
    public void Score_is_within_0_to_100_and_varies_with_content()
    {
        var random = new Random(7);
        var scores = new HashSet<int>();
        for (var i = 0; i < 2_000; i++)
        {
            var bytes = new byte[64];
            random.NextBytes(bytes);
            var score = PhotoQuality.Score(bytes);
            Assert.InRange(score, 0, 100);
            scores.Add(score);
        }

        Assert.True(scores.Count > 50, $"Expected a spread of scores, got {scores.Count} distinct values");
    }
}
