using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Onboarding.Core.Eligibility;

public static class PhotoQuality
{
    /// <summary>Simulated 0-100 score derived from the photo bytes: same bytes, same score.</summary>
    public static int Score(ReadOnlySpan<byte> photoBytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(photoBytes, hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash[..4]) % 101);
    }
}
