# Bililive AutoUploader

ASP.NET Core 10 站点：接收录播姬 Webhook V2 的 `FileClosed` 事件，将录播文件和配置的附属文件上传到百度网盘，并按策略清理本地文件。

## 本地运行

```powershell
$env:Panel__InitialPassword = Read-Host "首次启动密码"
dotnet run --project src/BililiveAutoUploader.Web
```

首次启动需要通过 `Panel__InitialPassword` 提供至少 12 个字符的临时密码。密码会使用 ASP.NET Core PasswordHasher 加盐哈希后写入 SQLite；初始化完成后应移除该环境变量。生产环境还需要配置 `Baidu__Cookie`、`Baidu__AccessToken`，并将 SQLite 数据库和 Data Protection 密钥放在持久化卷中。

## Docker

```bash
PANEL_INITIAL_PASSWORD='replace-with-a-long-password' docker compose up --build -d
# 首次初始化成功后清除环境变量并重建容器，避免明文留在容器环境中
unset PANEL_INITIAL_PASSWORD
docker compose up -d --force-recreate
```

也可以直接拉取 GitHub Container Registry 镜像：

```bash
docker pull ghcr.io/kengwang/bililive-autouploader:latest
```

录播机 Webhook URL：`http://<host>:8080/api/webhooks/bililive`。Webhook 无需登录；面板和管理 API 使用站点自身的单用户 Cookie 登录验证。

## 百度协议来源说明

`Infrastructure/BaiduPanClient.cs` 按 `E:\Clones\BaiduPCS-Go` 中的百度网盘 HTTP、秒传和分片上传行为进行 C# 重写，不启动 Go CLI。原项目采用 Apache License 2.0；本项目保留其来源说明，并对翻译后的实现做了修改。
