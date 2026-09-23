using BililiveAutoUploader.Application;
using BililiveAutoUploader.Infrastructure;

namespace BililiveAutoUploader.Tests.Unit;

public sealed class LocalFilesTests
{
    [Fact]
    public async Task ListAsync_EmptyRelativePathListsRootDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "bililive-local-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "recording.mp4"), "test");
            var service = new LocalFileService(new StorageOptions { LocalRoot = root });

            var entries = await service.ListAsync(string.Empty, CancellationToken.None);

            Assert.Contains(entries, entry => entry.Name == "recording.mp4");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
