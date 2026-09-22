using BililiveAutoUploader.Domain;

namespace BililiveAutoUploader.Tests.Unit;

public sealed class PathSafetyTests
{
    [Fact]
    public void ResolveUnderRoot_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "recordings");
        Assert.Throws<ArgumentException>(() => PathSafety.ResolveUnderRoot(root, "../secret.mp4"));
    }

    [Fact]
    public void ResolveUnderRoot_ReturnsPathUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "recordings");
        var path = PathSafety.ResolveUnderRoot(root, "room/file.mp4");
        Assert.StartsWith(Path.GetFullPath(root), path, StringComparison.OrdinalIgnoreCase);
    }
}
