using BililiveAutoUploader.Application;
using BililiveAutoUploader.Domain;
using System.Security.Cryptography;

namespace BililiveAutoUploader.Infrastructure;

public sealed class LocalFileService(StorageOptions options) : ILocalFileService
{
    public Task<IReadOnlyList<FileEntryDto>> ListAsync(string? relativePath, CancellationToken cancellationToken)
    {
        var root = PathSafety.ResolveUnderRoot(options.LocalRoot, relativePath ?? ".");
        if (!Directory.Exists(root)) return Task.FromResult<IReadOnlyList<FileEntryDto>>([]);
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var isDirectory = Directory.Exists(path);
                return new FileEntryDto(PathSafety.NormalizeRelative(options.LocalRoot, path), info.Name, isDirectory, isDirectory ? null : info.Length, info.LastWriteTimeUtc);
            }).ToList();
        return Task.FromResult<IReadOnlyList<FileEntryDto>>(entries);
    }

    public async Task<bool> WaitForStableAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        long? lastLength = null;
        DateTime lastWrite = default;
        var stable = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                await Task.Delay(options.StableCheckInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var info = new FileInfo(path);
            if (lastLength == info.Length && lastWrite == info.LastWriteTimeUtc) stable++; else stable = 0;
            if (stable >= options.StableChecks) return true;
            lastLength = info.Length;
            lastWrite = info.LastWriteTimeUtc;
            await Task.Delay(options.StableCheckInterval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

public sealed class FileChecksumService : IFileChecksumService
{
    private const int SliceSize = 256 * 1024;
    private const int BufferSize = 1024 * 1024;
    private const int BlockSize = 16 * 1024 * 1024;

    public async Task<FileChecksum> CalculateAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        using var md5 = System.Security.Cryptography.MD5.Create();
        var crc = new System.IO.Hashing.Crc32();
        var buffer = new byte[BufferSize];
        var first = new byte[Math.Min(SliceSize, length)];
        if (first.Length > 0) _ = await stream.ReadAsync(first, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var blocks = new List<string>();
        while (stream.Position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Math.Min(BlockSize, length - stream.Position);
            using var block = IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
            long read = 0;
            while (read < remaining)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining - read)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                md5.TransformBlock(buffer, 0, count, null, 0);
                crc.Append(buffer.AsSpan(0, count));
                block.AppendData(buffer, 0, count);
                read += count;
            }
            blocks.Add(Convert.ToHexString(block.GetHashAndReset()).ToLowerInvariant());
        }
        md5.TransformFinalBlock([], 0, 0);
        return new FileChecksum(length, Convert.ToHexString(md5.Hash!).ToLowerInvariant(), Convert.ToHexString(System.Security.Cryptography.MD5.HashData(first)).ToLowerInvariant(), Convert.ToHexString(crc.GetCurrentHash()).ToLowerInvariant(), blocks);
    }
}
