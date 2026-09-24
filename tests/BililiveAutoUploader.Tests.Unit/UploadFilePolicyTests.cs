using BililiveAutoUploader.Domain;

namespace BililiveAutoUploader.Tests.Unit;

public sealed class UploadFilePolicyTests
{
    [Theory]
    [InlineData("room/recording.xml")]
    [InlineData("room/RECORDING.XML")]
    public void XmlIsAlwaysKept(string path)
        => Assert.False(UploadFilePolicy.ShouldDeleteAfterUpload(path, true));

    [Fact]
    public void NonXmlFollowsRequestedDeletePolicy()
    {
        Assert.True(UploadFilePolicy.ShouldDeleteAfterUpload("room/recording.flv", true));
        Assert.False(UploadFilePolicy.ShouldDeleteAfterUpload("room/recording.flv", false));
    }
}
