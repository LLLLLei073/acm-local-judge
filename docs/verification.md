# 本机验收记录

## 2026-10-21 · Codeforces 绑定延迟与记录同步修复

- CF 专项 40 项检查通过：`artifacts/codeforces-20261021-110454/report.json`。覆盖旧资料返回 pending、同一挑战复用与恢复、错误验证码拒绝、绑定自动同步、限流重试、1000 条分页、重复提交去重、已通过题目去重、跨账号隔离、中途失败保留完整快照、重判更新、重启持久化、重复同步合并、解绑竞态、共享请求间隔及缓存刷新提示。
- 后端原有 202 项回归通过：`artifacts/api-20261021-110116/report.json`；前端现有 22 项行为回归通过；Server Release 发布及前端类型/生产构建通过。沿用已有 Monaco 大包及依赖注释提示，原 API 测试有 6 处既存 nullable 警告。
- 浏览器隔离实例 `27130` 验证：旧 Last name 等待并自动重试；资料更新后自动绑定和同步 63 条提交 / 2 道题；第 2 页显示剩余 13 条；已通过题目仅显示 2 道；重启后仍可读取；模拟 CF 失败时保留旧统计、记录与更新时间，并显示错误。截图：`artifacts/cf-browser/success.png`、`failure-preserves-history.png`。
- 真实 CF `user.info` 请求返回 OK，确认刷新参数可用；真实账号的 Last name 修改由账号本人操作，本轮未更改任何真实 CF 资料。模拟验证不能证明 CF 上游会即时更新。官方接口只提供公开记录。
- `27128` 使用原 `artifacts/refactor-browser-data` 数据目录加载修复，`ACM_CF_API` 未设置，使用官方接口。测试数据与业务 `data/` 分开。此次未推送 GitHub。

## 2026-10-21 · 重构最终验收

327 项自动检查通过（49 原生、18 多进程、36 流程、202 接口、22 前端），Server/Worker 发布与最终前端类型/生产构建通过。新增出题工具按真实协议执行、整批数据验证、特判容差与错误归因；44 条页面断言及真实 500 条通信分页/日志导出、390px 固定宽度窗口已核对。逐功能源码与证据见 [最终验收](refactor-acceptance-2026-10-21.md)。本轮未改业务 data，后续修复尚未再次推送。

## 2026-10-20 · GitHub 推送前验证

推送后的继续重构：前端 22 项、接口 179 项通过；与未改动内核的 49 项原生、18 项多进程、36 项流程检查合计 304 项。新增密码修改的六条接口回归，以及长期练习个人计时/定时比赛统一计时的三条回归。实际页面发现并修复榜单搜索固定显示本人、比赛筛选混入未分组、排行榜侧栏选题无法切换、长期练习出现巨大罚时的问题。最新接口证据 `artifacts/api-20261020-161914/report.json`，浏览器证据与待验范围见重构进度。本轮后续修改尚未提交推送。

后端与 Worker Release 发布成功。原生判题 49、多进程 18、接口与题目包 170、可视化通信流程 36、前端行为 21，共 294 项通过；前端类型检查与生产构建通过，仍有大包及依赖注释提示。通信流程套件曾因旧 ZIP 断言未考虑“导入为新草稿”停止，更新断言并重跑后通过。测试使用独立 artifacts 数据目录，没有改写业务 data。详细证据与尚待浏览器验收的范围见 [重构进度](refactor-progress-2026-10-20.md)。

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

## OJ 端与管理员端拆分（2026-10-15）

- 页面按路径拆分：`/` 选手 OJ 端（看题/提交/SSE 结果/历史/取消），`/admin` 管理员端（题目 CRUD、测试数据、判题程序、流程编辑、ZIP 导入导出、运行环境）。`main.ts` 按 pathname 挂载，无新前端依赖；共享逻辑抽到 `client.ts`（api/令牌状态/登录）。
- 鉴权：管理 API（题目写操作、tests 上传、packages、settings、export、rejudge）一律要求令牌（回环本机仍免认证）；选手 API 受 `ACM_OJ_OPEN=1`（settings.json `ojOpen`）控制，默认关闭=远程全要令牌。
- 数据脱敏：非管理员视角的题目详情只保留第一个测试点作为样例，其余测试点内容、checker/interactor/manager 源码、wrongSource、通信流程（内嵌期望值）全部剥离；`/api/environment` 对非管理员隐藏编译器路径。
- API 回归 55 → 65：新增第三个实例（27126，`ACM_OJ_OPEN=1`，绑定 0.0.0.0 并经本机局域网 IP 模拟远程选手路径）——匿名列表/详情断言脱敏生效、匿名提交被接受、匿名 PUT/export/rejudge 均 401、管理员视角保留判题工具、匿名 environment 无编译器路径。
- 过程修复：IsAdminRequest 中 rejudge 分类误嵌 problems 分支导致匿名可重评；测试 BaseAddress 与路径拼接产生 `//` 落入 GET-only fallback 返回 405。

### 管理端强制登录（2026-10-15 追加）

- 双入口彻底分离：前端改为多页构建（`index.html` 选手端 / `admin.html` 管理端，各自独立 bundle）；选手页面移除管理入口，管理端不再回链选手端。
- `/admin` 由服务端把守：未持令牌（**含本机回环**）只返回内嵌令牌登录页，登录后才下发管理控制台；裸 `admin.html` 302 回 `/admin`。管理 API 同样一律要求令牌（原"回环免认证"仅保留给选手端）。`acmAdmin` 视角改为仅由有效令牌决定。
- 回归 65 → 70：回环管理 API 无令牌 401、未登录 `/admin` 是登录门、登录后下发控制台、裸 admin.html 重定向、令牌登录成功。

### 提交门禁情况记录（2026-10-16）

本批改动（题库/比赛 + Worker 重试修复）已全部验证（API 套件 79 项全绿、前端构建通过、本机服务运行新版），但版本提交被 Mimosa 插件 L3 门禁拦截：12 高危 + 4 中危，逐项核对均为误报或设计固有（`Process.Start`+`ArgumentList` 启动编译器/选手程序是判题机本体；Token/AllowedHosts/OjOpen/RateLimit 与路径穿越无关；Compiler 路径为管理员专属配置；提交目录 id 经 GUID 严格校验）。12 项全部位于已提交的历史代码中，意味着门禁会拦截后续所有版本提交。Mimosa 1.0.4 无白名单/误报申诉机制（规则包加密签名，文档化配置仅 failure-mode 与输出级别）。处置待项目所有者在客户端插件设置中决定（停用插件 / 在 ZCode 之外终端提交 / 等待插件提供白名单）。官方深扫封印：scan-2026-10-16T00-52-23.527Z-d3686dd4b961，seal sha256:00be9899…d3ce。

## 用户系统、Codeforces 绑定与校内排行榜（2026-10-16）

- **账号体系**：`users` 表（JSON body + `studentId` NOCASE UNIQUE 兜底索引）；注册字段为真实姓名/学号/邮箱/密码（学号、邮箱大小写不敏感唯一，密码 ≥6 位）；口令仅存 PBKDF2-SHA256（10 万次迭代），登录对不存在学号做等时哈希抹平时序差；会话为 HMAC 签名 Cookie `acm_user`（密钥 `sessionSecret` 首启生成入 `settings.json`，HttpOnly/Strict/30 天）。**选手端全站强制登录**；管理令牌与 `/admin` 流程不变（令牌亦可访问选手 API，提交不归属个人）。
- **破坏性变更**：移除 `ACM_OJ_OPEN` / `ACM_AUTH` 匿名/强制令牌语义；`submissions` 表新增 `userId` 列（ctor 内 PRAGMA 检查后 `ALTER TABLE` 幂等迁移，旧提交保持 NULL 显示「—」）；`Submission` 记录与 API 响应增加归属字段，rejudge 沿用原作者。
- **Codeforces 绑定**：挑战验证（CF 资料 Last name 临时改为 6 位验证码）→ 服务端调 `user.info` 核对后绑定并拉取 rating/rank；同 handle 全站唯一；挑战码 15 分钟过期、内存暂存。榜单打开时批量刷新超过 1 小时的绑定数据，CF 不可达时沿用缓存。`ACM_CF_API` 仅测试打桩用。
- **校内排行榜**：双榜 —— CF rating 榜（仅绑定用户）+ 校内解题榜（去重 AC 题数、提交数、最近 AC 时间，并列按提交数/时间）；匿名是**占位匿名**（保留名次，隐藏姓名/学号/CF handle，邮箱永不下发）。
- **前端**：选手端整页登录/注册卡片（替换原令牌弹窗，令牌弹窗仅保留在管理端）；顶栏用户菜单（个人设置/退出）；「排行榜」页双 tab（CF 段位配色）；个人设置弹窗（资料/匿名开关/改密码/CF 绑定向导）；提交记录加「提交者」列；管理端新增「用户管理」（列表/删除）。
- **过程中发现并修复**：榜单条目匿名属性名 `Solved/Submissions/LastAc` 经 camelCase 序列化成 `solved/submissions/lastAc`，与前端 `solvedCount/submissionCount/lastAcAt` 不一致（本地起服实测 JSON 后改为显式命名）；ApiTests 中 `reveal` 局部变量与外层 WA 用例重名。
- **回归**：API 套件 79 → **107 项全绿**（新增 28 项：匿名 me 探针、弱密码/学号重复/邮箱重复 400、错误密码/未知学号 401、会话解析、匿名开关、挑战码签发/错误码拒绝/验证绑定存 rating、CF 榜匿名占位与揭示、同 handle 二绑拒绝、解题榜全员列出、用户列表鉴权、令牌列出/删除用户、删除后登录拒绝、登出清会话、OJ 匿名 401、远程注册、登录后浏览/脱敏详情、归属提交、普通用户 rejudge/PUT/export 401、主实例注册用户提交归属与解题榜计数；原匿名 open-OJ 用例改写为登录流程，`ACM_AUTH` 实例语义更新）。Windows 全量 verify 通过（Smoke + Multiprocess + 107 API + 36 Flow + 前端构建），证据 `artifacts/{smoke,multiprocess,api,flow}-20261016-1151*`。CF 桩为本地 TcpListener 极简 HTTP 服务，全程无真实外呼。

## 2026-10-19 · CF/AtCoder 化与校内 rating

- **回归**：API 套件 124 → **151 项全绿**（新增 27 项：非数字/过短学号 400、未开赛比赛隐藏题目/详情/提交/名单、未开赛排行榜 400、开赛公布题目与开放排行榜、排行榜参与者/罚时排名/尝试计数、提前收卷后自动结算、两人 Elo +200/-200、rating 落账号、site 榜排序、重复访问结算幂等、源码仅作者/令牌可见、mine 筛选、status+problem 组合筛选）。Windows 全量 verify 通过（Smoke 49 + Multiprocess 18 + 151 API + Flow 36 + 前端构建），证据 `artifacts/{smoke,multiprocess,api,flow}-20261019-*`。
- **新语义**：比赛时间窗（未开赛题目 404）、ICPC 排行榜（罚时 = 用时 + 20 分钟/错次，同分并列名次）、rated 比赛结束后首次访问排行榜触发一次性多人 Elo 结算（初始 1500、±350 封顶、锁保护幂等）、提交源码作者可见制、题库状态列改为仅统计本人提交（前端 myHistory）。
- **浏览器端到端**（IAB 实测）：注册（学号提示/浙科大品牌）→ 题库（难度区间筛选、通过人数列）→ UI 提交 AC（SSE 实时）→ 提前收卷 → 比赛卡片（RATED/已结束/时间窗）→ 排行榜（+14/+2、Δ+200/-200）→ 校内 rating 榜。视觉评审 8/10：布局清晰、主题统一、无结构性缺陷；按建议为排行榜格子列加了浅底与分隔线。
