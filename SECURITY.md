# ACM Local Judge 安全模型（SECURITY.md）

本文件是 Mimosa 分层威胁模型的项目级上下文，描述信任边界与"看似危险实则设计"的模式，供门禁与人工评审使用。

## 系统是什么

一台 C++23 在线评测机：选手提交代码 → 服务端派发独立 Worker → Worker 在**受限沙箱会话**内编译并运行选手程序（Windows Job Objects / Linux rlimit+cgroups v2），判题并返回结果。

## 信任边界

| 边界 | 说明 |
|---|---|
| 选手浏览器 → 服务端 API | 管理 API（题目/比赛/用户列表/设置/导出/重评）无条件要求管理员令牌；选手 API 需注册用户会话（HMAC 签名 Cookie），全部输入有长度/格式校验 |
| 服务端 → Worker | Worker 由服务端进程启动，输入是不可变快照 `request.json`（GUID 目录 + 固定文件名），带父进程看门狗与 cancel 哨兵 |
| Worker → 选手代码 | 选手代码**只在沙箱会话内**运行：进程数/内存/CPU/墙钟/输出五重限额，超限整树击杀；Windows 挂起创建进 Job，Linux rlimit+进程组/cgroup |

## 用户账号（2026-10 新增）

- 注册字段为真实姓名、学号、邮箱、密码；学号与邮箱大小写不敏感唯一（代码级检查 + `users.studentId` NOCASE UNIQUE 索引兜底）。
- 口令仅存 **PBKDF2-SHA256（10 万次迭代、16B 随机盐、32B 派生值）**，常数时间比较；登录对不存在学号做等时哈希以抹平时序差。
- 会话为无状态签名 Cookie `acm_user = userId.过期.HMAC-SHA256`，密钥 `sessionSecret` 与令牌同存 `data/settings.json`（仅 `data/` 写入根内）；Cookie HttpOnly + SameSite=Strict + 30 天。
- Codeforces 绑定走**挑战验证**：服务端签发验证码，用户把 CF 资料 Last name 临时改为验证码，服务端出网调用 `https://codeforces.com/api/user.info` 核对后才绑定；同一 handle 全站唯一。测试通过 `ACM_CF_API` 指向本地桩，不产生真实外呼。
- 排行榜匿名是展示层脱敏：保留名次，姓名/学号/CF handle 不下发；邮箱永远不下发榜单，口令哈希永不出服务端（`PublicViews.User`）。

## 设计固有的"敏感模式"（本项目的正常工作方式，非遗漏）

1. **`System.Diagnostics.Process.Start`（CWE-78 标记）** — 判题机的核心职责就是启动进程：
   - `JudgeQueue` 启动 Worker（`dotnet` + `ArgumentList`，固定文件名参数，从不经过 shell）；
   - `LinuxProcessSession` 启动 g++/选手程序（`ArgumentList` 传参 + rlimits + 会话隔离）；
   - 测试程序启动被测服务器。
   以上均不使用 shell 拼接，参数一律走 `ArgumentList`。策略文件 `command.allowedBinaries` 已列出全部合法可执行体（dotnet/g++/setsid/unshare/solution/checker/interactor/manager）。
2. **管理员配置的编译器路径** — `PUT /api/settings` 校验绝对路径与存在性；这是管理员专属配置（令牌保护），不是攻击者输入。
3. **`ACM_ROOT` / `ACM_DATA` 环境变量** — 服务器部署配置（systemd unit / 管理员设置），非 HTTP 可达输入；启动时经 `Path.GetFullPath` 规范化。
4. **提交目录** — 目录名由服务端生成并经 `Guid.TryParseExact` 严格校验后才参与路径拼接。

## 数据流不变量

- 选手产物（stdout/stderr/编译日志）回传前截断（输出限额），题库展示经 `PublicViews` 脱敏（测试答案与判题器源码不下发）。
- `data/` 与 `artifacts/` 是仅有的两个写入根；提交/题目/比赛/用户 id 在参与路径拼接前均经 GUID 或正则校验。

## 已知边界（如实声明）

沙箱以资源限制与进程树管理为目标，面向校队/班级等可信群体；不承诺对抗定向恶意代码（README「边界」一节同此声明）。账号体系覆盖身份区分：注册开放、无邮箱验证，不承诺抵御公网定向攻击。HTTPS 由前置反向代理终结。
