# 本机验收记录

验收日期：2026-10-12（Asia/Shanghai）。环境：Windows x64、GCC 15.2.0、.NET SDK 10.0.400、Node.js 22.22.2、pnpm 11.15.0。

## 自动测试

- 原生引擎：49 项检查，使用真实 C++23 编译与运行，不是模拟结果。
- 双泊对照专项：18 项检查，验证两个角色的往返交互、原始字节转发和双方最终答案。
- API / 题目包：29 项检查，独立数据目录、独立 27124 端口，真实 Worker。
- 后端 Release 构建：通过；SQLite 已升级到 Microsoft.Data.Sqlite 10.0.12 / SQLitePCLRaw 3.0.5，当前 restore/build 无已知漏洞告警。
- 前端 vue-tsc 类型检查及 Vite 生产构建：通过。Monaco 独立按需加载；编辑器包仍有体积提示，首页 JS 约 177KB（gzip 64KB）。

原生检查覆盖：

- ASCII 空白、前导零、小数、大小写、token 数量、非 ASCII 空白、非法 UTF-8。
- 五类题正确程序 AC、错误程序 WA/TLE，以及资源最大值统计。
- 特判 PE、checker 崩溃与超时。
- 交互正常问答、未 flush、选手提前异常退出、交互器崩溃、大量 stderr、后置 checker。
- 通信工作目录独立、第二轮超时、manager 双轮通信、禁止同时启动两轮。
- 多进程单实例 RE/TLE、manager 非法协议。
- CE、RE、CPU TLE、墙钟 TLE、MLE、OLE、取消，以及退出码 259 不误当成仍在运行。

API / 包检查覆盖：五类 ZIP 往返、字段验证、目录越界、Windows 路径、解压上限、建题、配对数据上传及缺配拒绝、真实提交、SSE 终态、快照重评、导入新题、运行中取消、Worker 确认取消、服务突然退出无残留选手进程、重启 Interrupted、历史持久化、跨站 Origin 拒绝、删除题目保留提交。

自动测试证据位于 `artifacts/smoke-*/report.json`、`artifacts/multiprocess-*/report.json`、`artifacts/api-*/report.json`，逐程序结果在对应目录。本次回归分别为 smoke-20261012-202209、multiprocess-20261012-202000、api-20261012-202434，共 96 项通过。

双泊对照专项覆盖相同多重集、重复次数不同、10¹⁸ 数值、双方答案不一致、双方一致但错误、未排序、额外输出、提前退出、单角色 RE/TLE、未 flush、逐字节输出和 CRLF/Tab 原样转发。最大规模 n=200000、19 位整数的长行测试 AC；该次两个角色 CPU 分别约 78.13ms / 46.88ms，提交内存峰值汇总约 5.12MiB。时间为本机观测，不是性能保证。

## 可视化成员与通信流程验收

原生判题回归 49 项，双泊多进程 manager 回归 18 项，可视化流程专项 36 项，HTTP/API 回归 33 项。专项测试使用真实 C++23 进程验证五步往返、原样 CRLF 转发、行分片、10 万字节长行、拼接数据、EOF、输出校验、非法成员/步骤引用、额外输出、提前退出、未 flush、CPU/输出限制和取消；题目包、SQLite、不可变快照及重评均通过。

浏览器操作：导入 `examples/VisualFlow.zip`，编辑成员名称与步骤顺序，验证仍被引用的成员/消息受保护，编辑数据来源与片段并保存。Worker 真实评测两个测试点均 AC。把步骤 4 期望值改为 999 后返回 `WA：步骤 4`，恢复参考答案后重新 AC。

## 并行评测验收（2026-10-15）

调度层由严格串行改为并行派发：提交仍按顺序开始，可同时运行多个独立 Worker。并行评测数 1–8（默认 2），在「运行环境」设置或 `ACM_PARALLELISM` 配置，保存后对后续派发即时生效；每个提交的评测语义、取消、Judge Error、崩溃隔离与 SSE 推送不变。并发评测各自独立限额，会争抢 CPU，墙钟读数可能波动（已写入 README）。

HTTP/API 回归 41 项（原 33 项 + 新增 8 项），独立数据目录、独立 27124 端口、真实 Worker：并行数超范围拒绝、environment 上报 parallelism、两份 1.5s 提交同时进入 Compiling/Running（真并发）、并行两份均 AC、并行数 1 时后提交保持 Queued 不重叠、串行两份依次 AC，以及原有取消、父进程死亡清理、重启 Interrupted、Origin 拒绝等回归。

过程中发现并修复：`ChannelReader<T>.Count` 在 unbounded channel 上不受支持（会抛 NotSupportedException，导致 environment 接口 500），改为独立计数器统计排队数。

本轮变更留在工作区，未提交或推送 GitHub。

## 浏览器验收

通过 Codex 内置浏览器实际操作完成：

1. 新建「浏览器验收 · 求和」，编辑 Markdown 题面。
2. 批量上传 `01.in / 01.ans`，看到配对预览并保存。
3. 打开 Monaco，载入 C++ 源码并提交；通过 SSE 显示 AC。
4. 下载该题 ZIP，从页面重新上传并确认导入；题库新增独立题目。
5. 网页提交多进程示例，显示 AC；详情显示进程 0/1 输出 9/16 和四段消息时间线。
6. 检查 390px 窄屏布局，文档宽度未超过视口；随后恢复默认视口。

截图：`artifacts/screenshots/multiprocess-result.jpg`。浏览器验收使用 `artifacts/browser-data`，正式启动使用 `data`，不混入验收题目。

2026-10-12 双进程修正后，在当前本地题库新增「双泊对照 · 双进程交互」并从网页提交正确示例，三个测试点全部 AC。首个测试点时间线验证：分别分发 a/b → 角色 0 发出排序序列 → 原样转发到角色 1 → 角色 1 发出 1 → 转发到角色 0 → 角色 0 发出 1。截图保存在 `artifacts/screenshots/multiset-relay.jpg`。原有题目与历史提交未被覆盖。

## 验收边界

本次覆盖本机 Windows 环境与内置/构造协议程序；没有宣称 Linux 兼容、真实赛事平台协议兼容、公网恶意代码沙箱隔离或大规模并发能力。多进程上限可配置到 16，实际协作正反例以两个实例验证。CPU 时间具有 Windows 计时粒度；内存采用提交内存，不能直接与 Linux RSS 排名比较。

## 跨平台内核移植（2026-10-15）

- `NativeRunner` 改为平台分发门面：Windows Job Objects 会话原样保留，新增 Linux 会话（rlimit 硬底 + 10ms 轮询 + /proc 测量 + cgroup v2 委托增强 + unshare/setsid 包装探测降级）。
- 跨平台修正：PATH 分隔符、二进制扩展名、编译器默认路径与 `.exe` 校验、Windows 保留设备名检查仅 Windows 生效。
- WA 默认不再回显期望 token，题目级 `revealExpected` 开启回显；API 回归增至 43 项（默认隐藏 + 显式开启）。
- 测试 fixtures 全部去 Windows 化（`std::this_thread::sleep_for` 替代 `Sleep`，字节级输出片段保留 `_WIN32` 分支）。
- 验收：Windows 全量 verify 通过（Smoke + Multiprocess + 43 项 API + 36 项 Flow + 前端构建）。Linux 会话在 Windows 上仅编译验证（本机无 WSL/Docker）；Linux 真机行为由 `scripts/verify.sh` 在服务器上验收（LinuxRunner 的 TLE/MLE/OLE、netns 探测、cgroup 委托）。

## 网络层与运维（2026-10-15）

- 可配置绑定 `ACM_HOST`（默认 127.0.0.1）+ Host/Origin 域名白名单（`ACM_ALLOWED_HOSTS` / `settings.json allowedHosts`），替换原先"非回环一律 403"的墙。
- Bearer/Cookie 访问令牌：首启自动生成并打印，`ACM_TOKEN` 可固定；回环免认证保持本机体验，`ACM_AUTH=required` 强制全认证；`POST /api/auth` 换取 HttpOnly Cookie（SameSite=Strict，30 天）；SSE 经 EventSource 自动携带 Cookie。前端新增令牌登录弹窗，localStorage 记忆。
- 提交限流：按 IP 固定窗口，`ACM_RATE_LIMIT`（默认 30/分钟，0 关闭），超限 429。
- `GET /healthz` 存活探针（免认证，返回 judging/queued 计数）。
- 文件日志按天写入 `data/logs/`（框架日志仅 Warning+）；磁盘保留策略 `ACM_RETAIN_PROBLEM_VERSIONS` / `ACM_RETAIN_SUBMISSIONS`，后台每 6 小时清理。
- `ForwardedHeaders`（仅信任回环代理）保证限流与远程判定在反代后取真实客户端 IP；.NET 10 中 `ForwardedHeadersOptions` 位于 `Microsoft.AspNetCore.Builder`。
- 部署链路：`scripts/start.sh`、`scripts/verify.sh`、`deploy/judge.service`（Delegate=yes 委托 cgroup）、`docs/deploy.md`（Caddy/nginx 样例、环境变量表、令牌轮换、内存口径标定说明）。
- 过程中修复：测试脚手架 `Start()` 重构时丢失 `server` 赋值导致误报 NRE 与孤儿进程；无 Authorization 头时 `Headers.Authorization` 隐式转换为 null 导致 500（改用 `ToString()`）；ready 探针改用免认证 `/healthz`；cancel 观察断言由固定 600ms 改为轮询（消除竞态）。
- 回归：API 套件 41 → 55 项（WA 回显开关 ×2、认证/令牌/Cookie/Bearer、Host 白名单放行/拒绝、Origin 放行/拒绝、限流三连）。Windows 全量 verify 通过；Linux 真机行为由 `scripts/verify.sh` 在服务器验收（LinuxRunner 超限判定、netns 探测、cgroup 委托）。
