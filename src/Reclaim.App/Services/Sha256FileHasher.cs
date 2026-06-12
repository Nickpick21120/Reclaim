using System.IO;
using System.Security.Cryptography;
using Reclaim.Core.Duplicates;

namespace Reclaim.App.Services;

/// <summary>Hashes file contents with SHA-256, streaming so large files don't
/// load fully into memory. Used by the duplicate finder.</summary>
public sealed class Sha256FileHasher : IFileHasher
{
    public string Hash(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }
}
