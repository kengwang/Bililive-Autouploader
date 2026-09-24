namespace BililiveAutoUploader.Domain;

public static class UploadFilePolicy
{
    public static bool KeepLocalAfterUpload(string relativePath)
        => relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldDeleteAfterUpload(string relativePath, bool requested)
        => requested && !KeepLocalAfterUpload(relativePath);
}
