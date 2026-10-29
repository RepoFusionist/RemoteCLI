# VPS deployment

The maintained deployment runs on Alibaba Cloud ECS. Operators keep the target configuration and Cloud Assistant credentials locally; these are excluded from the source repository. Use Alibaba Cloud Cloud Assistant for deployment and rollback.

The client default endpoint is `https://api.sunxiaochuan258.de5.net/remotecli/`. Validate HTTPS/WSS routing and certificate validity against the operator's local target configuration before deployment.

`longshell-relay.service` runs the Linux x64 framework-dependent Relay as the dedicated `longshell` user, using the ASP.NET Core 10 runtime in `/opt/longshell/runtime`. It binds only to loopback port 18084. The proxy socket and service templates can forward traffic from a local reverse proxy. Adapt the private bridge address to the host configuration and keep backend ports off the public interface.

Insert `nginx-remotecli.location.conf` into the intended API-host server blocks and validate the reverse proxy configuration before reloading. Preserve existing website routes and Docker bind mounts. The application accepts forwarded headers only from `127.0.0.1` and refuses non-loopback HTTP listeners in trusted-proxy mode.

The older `remotecli-vps.service` / Caddy snippet are historical templates, not the active deployment target.

Publish with:

```powershell
$TaskNugetRoot = Join-Path (Get-Location) '.nuget'
$env:NUGET_PACKAGES = Join-Path $TaskNugetRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $TaskNugetRoot 'http-cache'
dotnet restore src/RemoteCli.Vps/RemoteCli.Vps.csproj --runtime linux-x64 -m:1 --packages $env:NUGET_PACKAGES
dotnet publish src/RemoteCli.Vps/RemoteCli.Vps.csproj -c Release -r linux-x64 --self-contained false --no-restore -p:UseAppHost=false -p:DebugType=None -o artifacts/publish/vps-aliyun-linux-x64-<version>
```

Username authorization leases, pending invitations, and session rooms live in memory and are lost on process restart. The server administration state is persistent under `/var/lib/remotecli/admin` (created by `StateDirectory=remotecli`), with mode `0700` inherited from the service umask. On first start the server creates `bootstrap.key`; copy that key through the trusted Cloud Assistant channel, open `/remotecli/admin`, and initialize the `root` account. The key is deleted after successful initialization. The admin page controls official-client enforcement, password modes, authorization lifetime and rate limits. Deploy the Relay build from the same source revision as the desktop client before enabling username authorization.
