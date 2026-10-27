# VPS deployment

The only approved deployment target is Alibaba Cloud ECS in `NewServer/server.json`. Use `NewServer/AliyunCloudAssistant.ps1` for Shell commands and file delivery. Do not use the retired Lightsail target or the credentials in `Server/`.

The current endpoint is `https://api.sunxiaochuan258.de5.net/remotecli/` (note `sun`, not the retired `shun` hostname). Cloudflare proxy remains enabled. The existing source certificate is valid through 2027-01-17. See [NewServer operations](../NewServer/README.md) for target details, verification, and memory measurements.

`longshell-relay.service` runs the Linux x64 framework-dependent Relay as the dedicated `longshell` user, using the ASP.NET Core 10 runtime in `/opt/longshell/runtime`. It binds only to loopback port 18084. The existing Docker Nginx reaches it through `longshell-proxy.socket` / `longshell-proxy.service` on private bridge address `172.18.0.1:18085`. Neither backend port is exposed on the public IP or added to the cloud security group.

Insert `nginx-remotecli.location.conf` into both API-host server blocks and validate with `docker exec mclist-proxy nginx -t` before reloading. Existing root website routes stay separate. Preserve the inode of the Docker bind-mounted `nginx.conf` when copying a validated candidate. The application accepts forwarded headers only from `127.0.0.1` and refuses non-loopback HTTP listeners in trusted-proxy mode.

The older `remotecli-vps.service` / Caddy snippet are historical templates, not the active deployment target.

Publish with:

```powershell
$TaskNugetRoot = Join-Path (Get-Location) '.nuget'
$env:NUGET_PACKAGES = Join-Path $TaskNugetRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $TaskNugetRoot 'http-cache'
dotnet restore src/RemoteCli.Vps/RemoteCli.Vps.csproj --runtime linux-x64 -m:1 --packages $env:NUGET_PACKAGES
dotnet publish src/RemoteCli.Vps/RemoteCli.Vps.csproj -c Release -r linux-x64 --self-contained false --no-restore -p:UseAppHost=false -p:DebugType=None -o artifacts/publish/vps-aliyun-linux-x64-<version>
```

The relay has no persistent application data directory; username authorization leases, pending invitations, and session rooms live in memory and are lost on process restart. Deploy the Relay build from the same source revision as the desktop client before enabling username authorization.
