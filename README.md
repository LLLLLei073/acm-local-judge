# ACM Local Judge

训练台与出题工作台已完成本轮整体重构：训练概览、账号隔离草稿、共享运行证据、自动断线恢复、数据配对预览、通信时序图和可审核的 AI 建议。原计划 22 个功能组的调整与验证见 [逐功能验收](docs/refactor-acceptance-2026-10-21.md)，操作说明见 [工作台指南](docs/workbench-guide.md)。

跨平台 C++23 测评工作台：Windows 本机开箱即用，可部署到 Linux 服务器（见 [部署指南](docs/deploy.md)）。Vue 3 + Monaco 前端，ASP.NET Core 10 后端，独立 Worker，SQLite 持久化。

## 启动

双击项目根目录的 **`start.cmd`**。首次启动会安装前端依赖、构建服务并打开 **http://127.0.0.1:27123**。安装完成后网页和评测可以离线运行，不依赖 CDN。

需要 Windows 10/11 x64、.NET 10 SDK、Node.js 22.12+、pnpm 11 和支持 C++23 的 MinGW-w64 GCC。当前默认编译器为 `D:\mingw64\bin\g++.exe`；可在网页左下角「运行环境」修改。并行评测数（同时评测的提交数）也在此设置，范围 1–8、默认 2，保存后立即生效。

```powershell
# 初次启动 / 源码更新后重新构建
powershell -ExecutionPolicy Bypass -File scripts/start.ps1

# 已构建时快速启动
powershell -ExecutionPolicy Bypass -File scripts/start.ps1 -SkipBuild
```

保留服务终端，按 Ctrl+C 结束服务。若服务已启动，再次运行启动脚本会打开现有网页。端口默认只监听回环地址；Linux 服务器部署（systemd 常驻 + 反向代理 HTTPS + 访问令牌）见 [部署指南](docs/deploy.md)。

## 用户系统与访问控制

本站面向**浙江科技大学**成员：注册必须绑定学号（4–12 位数字，全站唯一），一个学号只能注册一个账号。选手端为**全站强制登录**：打开 OJ 需要用真实姓名、学号、邮箱注册（学号与邮箱唯一），之后以学号 + 密码登录，会话以 HttpOnly 签名 Cookie 保持 30 天（密钥 `sessionSecret` 首次启动生成并持久化到 `data/settings.json`，轮换即全员下线）。密码仅存 PBKDF2-SHA256 哈希。**管理端 `/admin` 与全部管理 API 始终需要访问令牌（本机回环也一样）**，令牌同时也可直接访问选手 API（提交不归属个人）。相关配置：`ACM_HOST`（监听地址）、`ACM_ALLOWED_HOSTS`（放行的 Host/Origin 域名白名单）、`ACM_TOKEN`（固定令牌）、`ACM_RATE_LIMIT`（每 IP 每分钟提交上限，默认 30，0 关闭）、`ACM_CF_API`（Codeforces API 地址，默认 `https://codeforces.com/api`，仅测试打桩时覆盖）。`GET /healthz` 提供存活探针（无认证）。

绑定 Codeforces 需验证账号归属：申请验证码后把 CF 个人资料的 **Last name** 临时改为验证码，系统调用 CF 公开 API 核对通过才完成绑定。资料尚未更新时保留原验证码，每 10 秒自动重试，可暂停；绑定成功后自动同步 rating、公开提交历史与去重后的已通过题目。个人设置 → Codeforces 可分页查看并手动同步。榜单打开时对超过 1 小时的数据安排后台刷新；网络或分页失败保留上次成功数据。CF 记录独立于本站解题榜，不需要提供 CF 密码或 API 密钥。**校内排行榜**提供三榜：**校内 rating 榜**（由 rated 比赛结算的站内 Elo，见下）、Codeforces rating 榜（仅绑定用户）与校内解题榜（按 AC 题数），用户可在个人设置中选择**匿名**——保留名次但隐藏姓名、学号与 CF 账号。

## 比赛系统（Codeforces / AtCoder 式）

- **时间窗**：管理员建比赛时设置开始/结束时间（不设开始时间 = 长期开放的练习赛）。**未开赛的题目对选手完全隐形**（题库、题目详情、提交入口全部 404），开赛后自动公布；比赛卡片显示「未开始 / 进行中 / 已结束」徽章与**实时倒计时**。
- **ICPC 赛制排行榜**：进行中实时更新（无封榜）。排名按通过题数降序、罚时升序；罚时 = 通过用时（分钟）+ 每次错误提交 20 分钟。每题格子显示 `+`/`+2`/`-3` 与评测中的 `…`。
- **长期练习计时**：未设置开始时间的练习赛，从每位选手首次提交起计时；定时比赛从统一开赛时间起计时。
- **校内 rating**：勾选 **Rated** 的比赛结束后，任何人首次访问其排行榜即自动按最终名次做多人 Elo 结算（初始 1500，单场变化 ±350 封顶），结果写入个人资料与校内 rating 榜，且**只结算一次**（幂等）。排行榜会展示每人的本场变化 Δ。

## 使用

页面分两端，完全独立：**`/` 是选手 OJ 端**（注册登录、浏览题目、看题面、写代码、提交、查结果、排行榜），**`/admin` 是管理控制台**（建题改题、测试数据、判题程序、验题与发布、ZIP 导入导出、用户管理、运行环境设置）。选手页面上没有任何管理入口；`/admin` 页面本身由服务端把守——未登录只会看到令牌登录页（本机也一样），登录后 30 天内免重复输入。

1. 管理员首次访问 `/admin`，输入启动日志中的访问令牌登录；可在「运行环境」中配置 AI 助手（任意 OpenAI 兼容服务：DeepSeek / Kimi / 通义 / Ollama 等，仅保存在本浏览器）：填好 Base URL 与 Key 后点「获取模型」自动拉取可选模型列表，再「测试连接」验证。
2. 在管理端选择内置题目或建题，设定类型、题面、资源限制；在「测试数据」中上传同名 `.in/.ans` 或手动编辑。新建题目默认为**草稿**，选手不可见。
3. 特判/交互/多进程题在「判题程序」中填写相应 C++ 源码；工具头文件已内置。
4. **AI 助手**可建议难度 rating、起草题面和生成强化输入，均需人工检查。输入先预览再应用：标准/特判题由标准答案计算答案；交互、通信、多进程题按需要填写参考判定（`.ans`）。所有新增点均由标准答案按本题真实协议验证，整批通过才保存；输入约束仍由出题人核对。
5. 题目需标注**难度 rating（100–3500）**才能发布或加入比赛。用标准答案源码验证全部数据；「暴力对拍」按本题 checker/interactor/双轮/manager/可视化流程评估候选程序，遇首个非 AC 停止并显示原因，不能把任意失败视作复杂度证明。「整题重测」按当前数据重新评测历史提交。确认后发布，工作台展示评级、测试点、近期验证与发布状态。
6. 选手在 `/` 用真实姓名、学号、邮箱注册并登录；提交记录与排行榜按人归属。
7. 选手在**题库**（参考 Codeforces 题库的紧凑表格）中选题：列有编号、题目、比赛、难度（CF 配色）、类型、测试点、**通过人数**和做题状态（按本人完整历史统计，已有 AC 不被后续失败覆盖），支持按关键词、**难度区间**、类型、比赛与状态（已解决/尝试过/未解决）筛选；**比赛**页按比赛浏览（含时间窗状态与倒计时），点击进入对应题库视图，或打开该比赛的 ICPC 排行榜。
8. 每道题归属于一场比赛（未分组单独一类）；管理员在「比赛管理」中新建比赛（可设时间窗与 Rated 开关），或在题目工作台的发布栏直接把题目加入/移出比赛，选手端立即生效。未评级的题目会被拒绝加入比赛。
9. **排行榜**页提供校内 rating 榜、Codeforces rating 榜与校内解题榜；在「个人设置」中查看校内 rating、完成 CF 绑定挑战、修改资料密码、切换排行榜匿名。管理员在 `/admin` 的「用户管理」中查看与删除注册用户。
10. 结果通过 SSE 实时更新。点击测试点查看各轮、各进程、stdout/stderr、退出码及通信时间线。编辑器支持 **Ctrl+Enter** 提交；题面样例带**一键复制**。
11. 「提交记录」支持按**范围（全部/我的）×判定×题目**筛选；**源码仅提交者本人与管理员可见**（他人只看到结果行）。按原快照重评是管理员操作。
12. 题目右上角可导出 ZIP；管理端左侧「导入题目包」先预览，确认后创建新题目（导入为草稿，评级并验证后发布）。
13. 选手端看到的题目是脱敏视图：只保留第一个测试点作为样例，其余测试数据、判题程序源码和通信流程期望值不会下发。

内置标准求和、浮点特判、即时加倍交互、双轮通信、双泊对照五道示例。双泊对照由两个角色通过调度器往返交互，双方最终答案都必须正确。每道题均带标准答案源码，供验题与新数据验证使用；标准/特判题还可自动计算答案。

## 评测语义

多进程题可在「编辑题目 → 成员与通信」管理成员、拖动步骤、设置数据来源和输出校验，自动生成题面通信样例。勾选「用此流程执行评测」即可替代本题 manager；复杂分支和算法判定仍使用 C++ manager。导入 `examples/VisualFlow.zip` 可体验五步往返协议，详见 [可视化通信流程](docs/communication-flow.md)。

| 类型 | 协议 | 资源汇总 |
|---|---|---|
| 标准 | 输入 UTF-8 文本；按 ASCII 空格、Tab、CR、LF、VT、FF 分割，精确比较 token；无数值转换 | 单个选手实例 |
| 特判 | `checker input.txt output.txt answer.txt`，支持 testlib | 仅计选手 |
| 交互 | `interactor input.txt transcript.txt answer.txt`；stdin/stdout 双向管道，可选后置 checker | 仅计选手 |
| 通信 | 两次新进程、新工作目录；默认第一轮输出原始字节转发到第二轮；支持 manager 模式 | 两轮 CPU/内存分别取最大值 |
| 多进程 | 同一程序 N 个实例；`argv[1]` 为 0 起始编号，`argv[2]` 为 N；manager 路由消息 | 每实例分别限额，CPU/内存分别取最大值 |

标准题 `02` 不等于 `2`，`2.0` 不等于 `2`，`Yes` 不等于 `yes`。无效 UTF-8 输出返回 WA，不会以替换字符参与比较。特判读取选手输出原始字节。WA 默认只提示第 N 个 token 不同，**不回显期望答案**；出题调试可在题目 JSON 中设 `revealExpected: true` 打开回显。

编译参数固定 `-std=c++23 -O2 -pipe`，判题工具额外包含 `toolkit` 目录。每次记录实际 GCC 版本。C++23 的具体标准库功能支持由安装的 GCC 决定。

CPU 为实例进程树的内核统计 CPU 时间；内存峰值为 **Windows 提交内存峰值（commit）**或 **Linux RSS 峰值**——两者口径不同，同一限制值在两侧判定强度有差异，跨平台复用题目时建议预留余量。CPU 通过约 10ms 周期检查，并在退出后再核算；极短程序的读数存在系统计时粒度。墙钟独立限制读写等待；不提供自动 flush。

多进程题的同一份程序同时运行多个角色，由调度器分发各自输入、双向转发消息、按协议判定。选手只需读取角色编号、使用普通 stdin/stdout；JSON/Base64 只用于调度器与引擎之间。「双泊对照」的完整题面、选手程序和调度器见 [MultisetExample.cs](src/Judge.Core/MultisetExample.cs)。已有本地题库不会因源码更新自动覆盖；可导入新版 [Multiprocess.zip](examples/Multiprocess.zip)。

默认：选手 2000ms CPU / 10000ms 墙钟 / 256MiB / stdout+stderr 16MiB；编译 60s / 1GiB / 日志 1MiB；判题工具 10000ms CPU / 512MiB。交互与 manager 的墙钟至少覆盖整个选手会话。多进程数 2–16。

Windows 上选手进程挂起创建、加入 Job 后恢复运行，关闭 Job 自动清理进程。Linux 上以 rlimits（CPU/地址空间/文件大小/进程数）为硬底、10ms 轮询精确判定，可用时加入 cgroup v2 组获得树级内存/进程限制与 `cgroup.kill`，并按发行版能力探测启用无特权网络 namespace 隔离（`ACM_NETWORK_ISOLATION=0` 关闭，`ACM_CGROUP=0` 禁用 cgroup，`ACM_CGROUP_DIR` 指定委托目录）。选手每实例只允许一个 OS 进程，内部可用线程；多个协作实例由评测器启动。编译器允许必要的子进程。提交按顺序开始，可同时运行多个独立 Worker：并行评测数 1–8（默认 2），在「运行环境」设置，保存后立即生效，并发评测各自独立限额、会争抢 CPU，墙钟读数可能波动。单个提交内普通错误后仍运行剩余测试点；最终取测试点顺序中首个非 AC，基础设施错误或取消单独处理。

状态：AC、WA、PE、CE、RE、TLE、MLE、OLE、Judge Error、Cancelled、Interrupted，以及 Queued/Compiling/Running。checker/interactor 的 0/1/2 退出码映射 AC/WA/PE，其他退出码或工具超限为 Judge Error；不支持部分分。manager 使用显式判定消息，并正常退出。

## 数据与开发

`data/judge.db` 保存题目和提交索引，`data/problems/` 保存题目版本及数据文件，`data/submissions/` 保存每次源码、快照、编译产物和日志。删除题目不删除历史快照。服务重启将未完成提交标记 Interrupted，不自动重复执行。备份时先停止服务，再复制整个 `data` 目录。

源码结构：`src/Judge.Core` 是沙箱会话（Windows Job Objects / Linux rlimit+cgroups）和判题引擎；`src/Judge.Worker` 是独立执行入口；`src/Judge.Server` 是队列、SQLite、题目包和 HTTP API；`web` 是 Vue 工作台；`toolkit` 内置固定版本的 testlib 和 C++ manager SDK。

```powershell
# 前端开发（另一个终端运行后端）
cd web
pnpm dev

# 全部自动验收；先关闭正在运行的服务，避免锁定发布文件
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1

# Linux 服务器上的等价验收
bash scripts/verify.sh
```

自动测试使用真实 GCC 和系统沙箱（Windows Job Objects / Linux rlimit+cgroups）。日志、JSON 结果和临时数据库保存在 `artifacts/`，与 `data/` 分离。运行日志同时按天写入 `data/logs/judge-YYYYMMDD.log`（Microsoft 框架日志仅记录 Warning 以上）。可用 `ACM_ROOT`、`ACM_DATA`、`ACM_COMPILER`、`ACM_PARALLELISM`、`ACM_HOST`、`ACM_PORT`、`ACM_TOKEN`、`ACM_ALLOWED_HOSTS`、`ACM_RATE_LIMIT`、`ACM_CF_API` 配置后端。磁盘保留策略默认关闭：`ACM_RETAIN_PROBLEM_VERSIONS` / `ACM_RETAIN_SUBMISSIONS` 设为正整数后，后台每 6 小时清理超出的历史版本与已完结提交。同一数据目录只允许一个服务实例。

协议与格式详见 [协议文档](docs/protocols.md)，验收记录见 [验证报告](docs/verification.md)。

## 边界

面向可信代码训练（本机个人使用，或校队/班级经服务器部署使用）。沙箱限制资源并管理进程树：Windows 不隔离文件、注册表和网络；Linux 在 cgroup/namespace 可用时收紧进程与内存边界，并尽力用网络 namespace 断网。**请勿用于运行陌生来源的恶意程序**；远程部署务必经反向代理启用 HTTPS。账号体系覆盖「区分谁提交了什么」：注册开放（任何能访问站点的人可注册），不做邮箱验证，不承诺抵御公网定向攻击；管理端仍以单一令牌把守，泄露令牌等于交出题库与全部提交源码。没有部分分或外部 OJ 包格式兼容；比赛支持时间窗、倒计时与 ICPC 罚时，但没有封榜。Codeforces 集成只读公开的 user.info 接口。

首次安装可能需要联网；启动失败会保留终端错误。若编译成功而程序提示缺失 MinGW DLL，请确认编译器同目录包含对应运行库。Monaco 编辑器按需加载，首次切换代码页会比题面稍慢。

第三方代码来源和版本在 `toolkit/VERSIONS.txt`，许可证保留在各头文件中： [testlib](https://github.com/MikeMirzayanov/testlib)、[nlohmann/json](https://github.com/nlohmann/json)。Windows 实现依据 [Job Objects 文档](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)。
