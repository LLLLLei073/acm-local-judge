# ACM Local Judge

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

## 服务器访问与令牌

服务启动时生成（或读取）**访问令牌**并打印在启动日志，同时持久化到 `data/settings.json`。**管理端 `/admin` 与全部管理 API 始终需要令牌（本机回环也一样）**；选手端 `/` 本机免认证，远程访问默认也要令牌。相关配置：`ACM_HOST`（监听地址）、`ACM_ALLOWED_HOSTS`（放行的 Host/Origin 域名白名单）、`ACM_TOKEN`（固定令牌）、`ACM_AUTH=required`（强制选手端也要令牌）、`ACM_RATE_LIMIT`（每 IP 每分钟提交上限，默认 30，0 关闭）、`ACM_OJ_OPEN=1`（开放选手端：远程访客可免令牌浏览和提交；管理操作不受影响，始终要令牌）。`GET /healthz` 提供存活探针（无认证）。

## 使用

页面分两端，完全独立：**`/` 是选手 OJ 端**（浏览题目、看题面、写代码、提交、查结果），**`/admin` 是管理控制台**（建题改题、测试数据、判题程序、ZIP 导入导出、运行环境设置）。选手页面上没有任何管理入口；`/admin` 页面本身由服务端把守——未登录只会看到令牌登录页（本机也一样），登录后 30 天内免重复输入。

1. 管理员首次访问 `/admin`，输入启动日志中的访问令牌登录。
2. 在管理端选择内置题目或建题，设定类型、题面、资源限制；在「测试数据」中上传同名 `.in/.ans` 或手动编辑。
3. 特判/交互/多进程题在「判题程序」中填写相应 C++ 源码；工具头文件已内置。
4. 选手在 `/` 选择题目，在「编写代码」编辑或载入 `.cpp`，点击提交评测。
5. 结果通过 SSE 实时更新。点击测试点查看各轮、各进程、stdout/stderr、退出码及通信时间线。
6. 「提交记录」可查看原源码及结果；按原快照重评是管理员操作。
7. 题目右上角可导出 ZIP；管理端左侧「导入题目包」先预览，确认后创建新题目。
8. 选手端看到的题目是脱敏视图：只保留第一个测试点作为样例，其余测试数据、判题程序源码、错误示例和通信流程期望值不会下发。

内置标准求和、浮点特判、即时加倍交互、双轮通信、双泊对照五道示例。双泊对照由两个角色通过调度器往返交互，双方最终答案都必须正确。每道题都带正确及错误程序。正确示例加载到编辑器后仍可自行修改，草稿按题目保存在浏览器本地。

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

自动测试使用真实 GCC 和系统沙箱（Windows Job Objects / Linux rlimit+cgroups）。日志、JSON 结果和临时数据库保存在 `artifacts/`，与 `data/` 分离。运行日志同时按天写入 `data/logs/judge-YYYYMMDD.log`（Microsoft 框架日志仅记录 Warning 以上）。可用 `ACM_ROOT`、`ACM_DATA`、`ACM_COMPILER`、`ACM_PARALLELISM`、`ACM_HOST`、`ACM_PORT`、`ACM_TOKEN`、`ACM_ALLOWED_HOSTS`、`ACM_AUTH`、`ACM_RATE_LIMIT` 配置后端。磁盘保留策略默认关闭：`ACM_RETAIN_PROBLEM_VERSIONS` / `ACM_RETAIN_SUBMISSIONS` 设为正整数后，后台每 6 小时清理超出的历史版本与已完结提交。同一数据目录只允许一个服务实例。

协议与格式详见 [协议文档](docs/protocols.md)，验收记录见 [验证报告](docs/verification.md)。

## 边界

面向可信代码训练（本机个人使用，或校队/班级经服务器部署使用）。沙箱限制资源并管理进程树：Windows 不隔离文件、注册表和网络；Linux 在 cgroup/namespace 可用时收紧进程与内存边界，并尽力用网络 namespace 断网。**请勿用于运行陌生来源的恶意程序**；远程部署务必经反向代理启用 HTTPS 并保管好访问令牌。没有账号、排行榜、比赛、部分分或外部 OJ 包格式兼容。

首次安装可能需要联网；启动失败会保留终端错误。若编译成功而程序提示缺失 MinGW DLL，请确认编译器同目录包含对应运行库。Monaco 编辑器按需加载，首次切换代码页会比题面稍慢。

第三方代码来源和版本在 `toolkit/VERSIONS.txt`，许可证保留在各头文件中： [testlib](https://github.com/MikeMirzayanov/testlib)、[nlohmann/json](https://github.com/nlohmann/json)。Windows 实现依据 [Job Objects 文档](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)。
