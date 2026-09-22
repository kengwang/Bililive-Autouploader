# Bililive AutoUploader

ASP.NET Core 10 站点：接收录播姬 Webhook V2 的 `FileClosed` 事件，将录播文件和配置的附属文件上传到百度网盘，并按策略清理本地文件。

## 本地运行

```powershell
dotnet run --project src/BililiveAutoUploader.Web
```

生产环境请通过环境变量覆盖 `Webhook__Secret`、`Baidu__Cookie`、`Baidu__AccessToken`，并将 SQLite 数据库放在持久化卷中。

## Docker

```bash
docker compose up --build
```

也可以直接拉取 GitHub Container Registry 镜像：

```bash
docker pull ghcr.io/kengwang/bililive-autouploader:latest
```

录播机 Webhook URL：`http://<host>:8080/api/webhooks/bililive`，请求 Header 使用 `X-Webhook-Secret`。

## 百度协议来源说明

`Infrastructure/BaiduPanClient.cs` 按 `E:\Clones\BaiduPCS-Go` 中的百度网盘 HTTP、秒传和分片上传行为进行 C# 重写，不启动 Go CLI。原项目采用 Apache License 2.0；本项目保留其来源说明，并对翻译后的实现做了修改。
