# 服务器部署指南（Linux）

从「Windows 本机工作台」到「校队/班级使用的服务器测评机」。目标形态：Linux 服务器 + systemd 常驻 + 反向代理 HTTPS + 访问令牌。使用对象是校队/班级等**可信群体**——这套部署不追求对抗公网恶意攻击（那需要账号体系与强沙箱），目标是防误操作、防资源滥用、防令牌外泄后的随手破坏。

## 前置要求

- Ubuntu 22.04 / Debian 12 或更新的主流发行版（x64）
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（构建）与 ASP.NET Core 运行时
- `g++` 13+（完整 C++23 标准库支持），`unshare`/`setsid` 来自 util-linux（发行版默认自带）
- Node.js 22+ 与 pnpm 11（仅构建前端时需要）
- systemd（用于 `Delegate=yes` 的 cgroup 委托）

## 1. 创建专用用户与目录

```bash
sudo useradd -r -m -s /usr/sbin/nologin judge
sudo mkdir -p /opt/acm-judge && sudo chown judge:judge /opt/acm-judge
```

测评机以低权限 `judge` 用户运行；它对系统的写权限越少，沙箱边界外的风险越小。

## 2. 构建

在服务器上构建（需 SDK + Node/pnpm）：

```bash
sudo -u judge git clone <repo> /opt/acm-judge && cd /opt/acm-judge
sudo -u judge scripts/start.sh          # 构建并前台运行，Ctrl+C 停止
```

或在 Windows/Linux 开发机上构建后上传产物（服务器只需运行时）：

```bash
# 开发机
dotnet publish src/Judge.Worker/Judge.Worker.csproj -c Release -o artifacts/worker
dotnet publish src/Judge.Server/Judge.Server.csproj -c Release -o artifacts/server
(cd web && pnpm install --frozen-lockfile && pnpm build)
# 打包 artifacts/server、artifacts/worker、web/dist、toolkit、deploy 上传到 /opt/acm-judge
```

启动脚本会自动完成 Worker/Server 发布和前端构建；后续更新代码后重跑 `scripts/start.sh` 或 `systemctl restart judge`。

## 3. systemd 常驻

```bash
sudo cp deploy/judge.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now judge
```

`deploy/judge.service` 已声明 `Delegate=yes`：评测会话能在服务自己的 cgroup 子树内创建独立组，获得树级内存峰值统计、pids 上限和 `cgroup.kill` 整树击杀。没有委托权限时测评机自动退化为 rlimit + /proc 测量，功能可用但内存口径改为 RSS 峰值采样。

环境变量可写入 `deploy/judge.env`：

```ini
ACM_ALLOWED_HOSTS=oj.example.edu
ACM_PARALLELISM=4
# 可选：固定访问令牌（默认每台机器首次启动自动生成并打印在日志）
# ACM_TOKEN=please-change-me
```

### 运行时配置（环境变量一览）

| 变量 | 默认 | 说明 |
|---|---|---|
| `ACM_HOST` | `127.0.0.1` | 监听地址；反代部署保持回环，仅本机调试时可改 |
| `ACM_PORT` | `27123` | 监听端口 |
| `ACM_ALLOWED_HOSTS` | 空 | 额外放行的 Host/Origin 域名，逗号分隔 |
| `ACM_AUTH` | 自动 | `required` 强制所有请求（含回环）持令牌；默认仅远程要求 |
| `ACM_TOKEN` | 自动生成 | 固定访问令牌；留空则首次启动生成并打印 |
| `ACM_OJ_OPEN` | 关 | `1` 开放选手 OJ 端：远程访客免令牌浏览/提交，管理操作仍需令牌 |
| `ACM_RATE_LIMIT` | `30` | 每 IP 每分钟可提交次数，`0` 关闭限流 |
| `ACM_PARALLELISM` | `2` | 同时评测的提交数（1–8） |
| `ACM_COMPILER` | 平台默认 | g++ 路径，默认 `/usr/bin/g++`，可被设置页覆盖 |
| `ACM_CGROUP` | 开 | `0` 禁用 cgroup 会话（仅 rlimit） |
| `ACM_CGROUP_DIR` | 自动探测 | 指定可写的 cgroup v2 委托目录 |
| `ACM_NETWORK_ISOLATION` | 开 | `0` 关闭无特权网络 namespace 探测 |

## OJ 端与管理员端

页面按路径分两端，**完全独立**：`/` 是选手 OJ 端（看题、提交、查结果），`/admin` 是管理控制台（建题、测试数据、判题程序、导入导出、运行环境）。选手页面没有管理入口；`/admin` 由服务端把守，未持令牌只会看到登录页——**本机回环也一样**。

- **管理员**：首次访问 `/admin` 输入令牌（启动日志打印或 `data/settings.json` 查看），Cookie 保持 30 天；管理 API 无论本机远程、OJ 是否开放，一律要求令牌。
- **选手**：本机免认证；远程默认要令牌，`ACM_OJ_OPEN=1` 后免令牌练习。开放时服务端自动脱敏——只保留第一个测试点作为样例，其余测试数据、checker/interactor/manager 源码、错误示例和通信流程期望值不会下发给选手；`/api/environment` 也不再暴露编译器路径。
- 提交记录全员可见（训练场景约定）；如需按人隔离，另起多套 `ACM_DATA` 目录多实例部署。

访问令牌在首次启动时生成并打印到 stdout（`journalctl -u judge` 可查），同时持久化到 `data/settings.json`。**本机回环访问免认证**（方便管理员本机调试）；远程浏览器第一次打开页面会要求输入令牌，登录后以 HttpOnly Cookie 保持 30 天。

## 4. 反向代理 HTTPS

Kestrel 只监听回环，TLS 由反代终结。推荐 Caddy（自动证书）：

```caddyfile
oj.example.edu {
    reverse_proxy 127.0.0.1:27123
}
```

或 nginx：

```nginx
server {
    listen 443 ssl http2;
    server_name oj.example.edu;
    # ssl_certificate     /etc/letsencrypt/live/oj.example.edu/fullchain.pem;
    # ssl_certificate_key /etc/letsencrypt/live/oj.example.edu/privkey.pem;
    client_max_body_size 90m;
    location / {
        proxy_pass http://127.0.0.1:27123;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_buffering off;          # SSE 实时结果需要关闭缓冲
        proxy_read_timeout 3600s;
    }
}
```

服务器把 `judge.example.edu` 加入 `ACM_ALLOWED_HOSTS`（或 `data/settings.json` 的 `allowedHosts`），否则中间件的 Host 白名单会拒绝该域名。防火墙只放行 80/443 与 SSH，**不要**对外暴露 27123 端口。

## 5. 部署后验收

```bash
scripts/verify.sh        # 全量回归：四个测试项目 + API 套件（含认证/限流/Host 白名单）
```

再从浏览器走一遍远程链路：打开域名 → 输入令牌 → 建题 → 提交 → 看 SSE 实时结果。

## 平台语义差异（跨平台复用题目须知）

- **内存口径**：Windows 报提交内存峰值（commit），Linux 报 RSS 峰值。同一题目两侧判定强度不同，迁移题目建议内存限制留 1.2–1.5 倍余量。
- **编译器版本**：Linux g++ 与 MinGW 行为差异（头文件、内建宏、链接行为）可能影响个别代码，题面示例建议在目标环境重验一遍。
- **联网**：启用网络 namespace 时选手程序在新网络栈中运行（回环不可达外部）；探测失败自动退化，可用 `ACM_NETWORK_ISOLATION=0` 显式关闭。

## 已知边界

- 无账号体系：一个共享令牌即全体成员身份，提交记录全员可见。需要按人区分时另建多套 data 目录 + 多实例。
- 令牌即权限：泄露令牌等于交出题库和全部提交源码。定期轮换（改 `ACM_TOKEN` 或删掉 `settings.json` 里的 `token` 重启）。
- 测评机允许选手代码运行几分钟量级的编译与执行，可抵御课程规模的滥用；不抵御定向资源攻击。
