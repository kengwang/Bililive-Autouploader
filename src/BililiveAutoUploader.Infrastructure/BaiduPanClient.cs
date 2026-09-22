using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BililiveAutoUploader.Application;

namespace BililiveAutoUploader.Infrastructure;

/// <summary>
/// 百度网盘 HTTP 协议客户端。行为参考 BaiduPCS-Go 的上传协议实现，但不调用其 CLI。
/// </summary>
public sealed class BaiduPanClient(HttpClient httpClient, BaiduOptions options) : IBaiduPanClient
{
    private const int DefaultPageSize = 1000;

    public async Task<IReadOnlyList<CloudFileEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var all = new List<CloudFileEntry>();
        for (var page = 1; ; page++)
        {
            var uri = $"{options.BaseAddress.TrimEnd('/')}/api/list?dir={Uri.EscapeDataString(path)}&order=name&desc=0&clienttype=0&num={DefaultPageSize}&page={page}";
            using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
            var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(body, "目录列表");
            var list = body.TryGetProperty("list", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToList() : [];
            foreach (var item in list)
            {
                var itemPath = item.GetProperty("path").GetString() ?? string.Empty;
                all.Add(new CloudFileEntry(itemPath, item.GetProperty("server_filename").GetString() ?? Path.GetFileName(itemPath), item.GetProperty("isdir").GetInt32() == 1, item.GetProperty("size").GetInt64(), item.TryGetProperty("md5", out var md5) ? md5.GetString() : null));
            }
            if (list.Count < DefaultPageSize) break;
        }
        return all;
    }

    public async Task<CloudFileEntry?> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var uri = $"{options.BaseAddress.TrimEnd('/')}/api/filemetas?dlink=0&target={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { path }))}";
        using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!body.TryGetProperty("info", out var info) || info.GetArrayLength() == 0) return null;
        var item = info[0];
        return new CloudFileEntry(item.GetProperty("path").GetString() ?? path, item.GetProperty("filename").GetString() ?? Path.GetFileName(path), item.GetProperty("isdir").GetInt32() == 1, item.GetProperty("size").GetInt64(), item.TryGetProperty("md5", out var md5) ? md5.GetString() : null);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string> { ["path"] = path, ["isdir"] = "1", ["block_list"] = "[]", ["rtype"] = "0" };
        using var response = await SendAsync(HttpMethod.Post, $"{options.BaseAddress.TrimEnd('/')}/api/create?a=mkdir", new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(body, "创建目录", allowExists: true);
    }

    public async Task<bool> TryRapidUploadAsync(string localPath, string cloudPath, FileChecksum checksum, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["path"] = cloudPath, ["size"] = checksum.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["isdir"] = "0", ["rtype"] = "3", ["autoinit"] = "1", ["block_list"] = JsonSerializer.Serialize(checksum.BlockMd5),
            ["content-md5"] = checksum.Md5, ["slice-md5"] = checksum.SliceMd5, ["content-length"] = checksum.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        using var response = await SendAsync(HttpMethod.Post, $"{options.BaseAddress.TrimEnd('/')}/api/precreate", new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (body.TryGetProperty("return_type", out var type) && type.GetInt32() == 2) return true;
        return false;
    }

    public async Task UploadAsync(string localPath, string cloudPath, FileChecksum checksum, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cloudPath)?.Replace('\\', '/') ?? "/";
        if (directory != "/") await CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        var precreateForm = new Dictionary<string, string>
        {
            ["path"] = cloudPath, ["target_path"] = directory + "/", ["size"] = checksum.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["isdir"] = "0", ["rtype"] = "3", ["autoinit"] = "1", ["block_list"] = JsonSerializer.Serialize(checksum.BlockMd5),
            ["local_mtime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        using var precreateResponse = await SendAsync(HttpMethod.Post, $"{options.BaseAddress.TrimEnd('/')}/api/precreate", new FormUrlEncodedContent(precreateForm), cancellationToken).ConfigureAwait(false);
        var precreate = await ReadJsonAsync(precreateResponse, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(precreate, "预创建");
        var uploadId = precreate.GetProperty("uploadid").GetString() ?? throw new InvalidOperationException("百度未返回 uploadid。");
        var blockList = precreate.TryGetProperty("block_list", out var blockArray) && blockArray.ValueKind == JsonValueKind.Array
            ? blockArray.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Enumerable.Range(0, checksum.BlockMd5.Count).ToArray();
        var chunkSize = Math.Clamp(options.ChunkSizeMiB, 4, 64) * 1024 * 1024;
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[chunkSize];
        var uploaded = 0L;
        for (var part = 0; part < blockList.Length; part++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(buffer, 0, read), "uploadedfile", Path.GetFileName(localPath));
            var uploadUri = $"{options.PcsAddress.TrimEnd('/')}/rest/2.0/pcs/file?method=upload&type=tmpfile&uploadid={Uri.EscapeDataString(uploadId)}&partseq={part}&app_id={options.AppId}";
            using var uploadResponse = await SendAsync(HttpMethod.Post, uploadUri, content, cancellationToken).ConfigureAwait(false);
            var uploadBody = await ReadJsonAsync(uploadResponse, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(uploadBody, "分片上传");
            uploaded += read;
            progress?.Report(new UploadProgress(uploaded, checksum.Length, part, blockList.Length));
        }
        var createForm = new Dictionary<string, string> { ["path"] = cloudPath, ["uploadid"] = uploadId, ["block_list"] = JsonSerializer.Serialize(checksum.BlockMd5), ["size"] = checksum.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["isdir"] = "0", ["rtype"] = "3" };
        using var createResponse = await SendAsync(HttpMethod.Post, $"{options.BaseAddress.TrimEnd('/')}/api/create", new FormUrlEncodedContent(createForm), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(await ReadJsonAsync(createResponse, cancellationToken).ConfigureAwait(false), "合并分片");
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["param"] = JsonSerializer.Serialize(new[] { new { path } }) });
        using var response = await SendAsync(HttpMethod.Post, $"{options.PcsAddress.TrimEnd('/')}/rest/2.0/pcs/file?method=delete&app_id={options.AppId}", content, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false), "删除云端文件");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.UserAgent.ParseAdd("BililiveAutoUploader/1.0");
        if (!string.IsNullOrWhiteSpace(options.Cookie)) request.Headers.TryAddWithoutValidation("Cookie", options.Cookie);
        if (!string.IsNullOrWhiteSpace(options.AccessToken)) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.AccessToken}");
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"百度网盘 HTTP {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static void EnsureSuccess(JsonElement body, string operation, bool allowExists = false)
    {
        if (!body.TryGetProperty("errno", out var errno) || errno.GetInt32() == 0) return;
        var code = errno.GetInt32();
        if (allowExists && code is -8 or 31061) return;
        throw new InvalidOperationException($"百度网盘{operation}失败，错误码 {code}。");
    }
}
