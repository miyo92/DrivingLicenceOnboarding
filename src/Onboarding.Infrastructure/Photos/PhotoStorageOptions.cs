namespace Onboarding.Infrastructure.Photos;

public sealed class PhotoStorageOptions
{
    public const string SectionName = "PhotoStorage";

    public string RootPath { get; set; } = "/data/photos";
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
}
