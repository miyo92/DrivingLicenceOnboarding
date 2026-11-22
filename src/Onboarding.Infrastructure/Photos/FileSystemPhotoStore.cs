using Microsoft.Extensions.Options;

namespace Onboarding.Infrastructure.Photos;

public interface IPhotoStore
{
    /// <returns>Server-generated storage key.</returns>
    Task<string> SaveAsync(Stream content, CancellationToken cancellationToken);

    Task<byte[]> ReadAllBytesAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes a stored photo; a missing file is not an error.</summary>
    void Delete(string storageKey);
}

/// <summary>Stores photos as files named by a generated GUID under <see cref="PhotoStorageOptions.RootPath"/>.</summary>
public sealed class FileSystemPhotoStore(IOptions<PhotoStorageOptions> options) : IPhotoStore
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    public async Task<string> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var key = Guid.NewGuid().ToString("N");
        try
        {
            await using var file = new FileStream(PathFor(key), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await content.CopyToAsync(file, cancellationToken);
        }
        catch
        {
            Delete(key); // no partial files left behind
            throw;
        }

        return key;
    }

    public Task<byte[]> ReadAllBytesAsync(string storageKey, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(PathFor(storageKey), cancellationToken);

    public void Delete(string storageKey) => File.Delete(PathFor(storageKey));

    // Keys are always server-generated GUIDs; anything else is refused so a key can never escape the root.
    private string PathFor(string storageKey) =>
        Guid.TryParseExact(storageKey, "N", out _)
            ? Path.Combine(_root, storageKey)
            : throw new ArgumentException("Invalid photo storage key", nameof(storageKey));
}
