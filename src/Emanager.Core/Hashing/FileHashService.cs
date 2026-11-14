using System.Security.Cryptography;

namespace Emanager.Core.Hashing;

public enum HashAlgorithmKind { Sha256, Sha512 }
public sealed record HashResult(HashAlgorithmKind Algorithm, string Hex, long BytesRead);

public interface IFileHashService
{
    Task<HashResult> ComputeAsync(Stream input, HashAlgorithmKind algorithm, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> CompareAsync(Stream left, Stream right, HashAlgorithmKind algorithm, CancellationToken cancellationToken = default);
}

public sealed class FileHashService : IFileHashService
{
    public async Task<HashResult> ComputeAsync(Stream input, HashAlgorithmKind algorithm, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var hash = algorithm == HashAlgorithmKind.Sha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[128 * 1024]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count); total += count; progress?.Report(total);
        }
        return new(algorithm, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
    }

    public async Task<bool> CompareAsync(Stream left, Stream right, HashAlgorithmKind algorithm, CancellationToken cancellationToken = default)
    {
        var a = await ComputeAsync(left, algorithm, cancellationToken: cancellationToken);
        var b = await ComputeAsync(right, algorithm, cancellationToken: cancellationToken);
        return a.BytesRead == b.BytesRead && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a.Hex), Convert.FromHexString(b.Hex));
    }
}
