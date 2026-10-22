# 题目包与判题协议 v1

多进程题支持可选的 `flow.version=1` 成员与步骤配置。`flow.enabled=true` 时不要求 manager.cpp，执行可视化流程；未启用时原协议不变。字段、行边界和校验规则见 [可视化通信流程](communication-flow.md)。

## ZIP 题目包

```text
problem.json
statement.md
checker.cpp       # 可选，特判必需
interactor.cpp    # 交互必需
manager.cpp       # 多进程/管理器通信必需
tests/01.in
tests/01.ans
```

所有文本 UTF-8。`problem.json` 最小示例（省略字段使用默认值）：

```json
{
  "formatVersion": 1,
  "title": "A + B",
  "mode": "Standard",
  "limits": { "cpuMs": 2000, "wallMs": 10000, "memoryMiB": 256, "outputKiB": 16384 },
  "judgeLimits": { "cpuMs": 10000, "wallMs": 60000, "memoryMiB": 512, "outputKiB": 16384 },
  "firstMarker": "first",
  "secondMarker": "second",
  "processCount": 2,
  "managedCommunication": false,
  "protocol": "从标准输入读两个整数，输出它们的和。",
  "exampleSource": "#include <iostream>\nint main(){int a,b;std::cin>>a>>b;std::cout<<a+b;}",
  "wrongSource": "int main(){}"
}
```

`mode` 只能是 `Standard / Special / Interactive / Communication / Multiprocess`。题面、判题源码和 tests 从包内文件读取，不从 manifest 的同名内嵌字段读取。导入忽略旧 id，始终创建新题。

压缩包及解压总量上限 64MiB，最多 2100 项；测试数据合计最多 60MiB、最多 1000 点。拒绝绝对路径、`..`、符号链接、重名文件、Windows 设备名及缺失答案。交互、多进程题允许省略 `.ans`，管理器自行定义参考数据需求。测试点名称允许文字、数字、下划线、点和横线，长度 1–80。

## checker / interactor

checker 命令参数顺序是输入、选手输出、标准答案，stdin 关闭。退出 0=AC，1=WA，2=PE，其他=Judge Error，stderr 为判题说明。工具超限也是 Judge Error。

interactor 参数为输入、交互记录文件、标准答案。stdin 接收选手 stdout，stdout 写给选手 stdin；两边的 stderr 独立捕获。使用 `registerInteraction(argc, argv)` 后，`inf` 是输入文件，`ouf` 是选手回答流，`tout` 写交互记录。若配置 checker，则成功交互后将 `transcript.txt` 作为 checker 的选手输出参数。

通信是原始字节流，不按行缓存、不替选手 flush。使用 `std::endl` 或 `std::flush`。某一端结束后，另一端会收到 EOF；未响应或互相等待由墙钟限时收尾。正常的非零选手退出属于 RE；若交互器先明确给出 WA/PE/系统错误，该判定保留。

## 简单双轮通信

第一轮 stdin = `firstMarker + "\n" + 测试输入`。第二轮 stdin = `secondMarker + "\n" + 第一轮 stdout 原始字节`。评测器不会自动给中间输出补换行。两轮 stdin 都在输入发完后关闭。

第二轮最终输出按标准 token 比较，或使用可选 checker。任一轮超限或非正常退出失败。两轮使用不同工作目录，不复制临时文件；最终 CPU 和内存分别取两轮最大值。

## manager 通道

manager 是可信 C++23 判题程序，使用 `#include "acm_manager.hpp"`。manager 的 stdin/stdout 是 **UTF-8 JSON Lines 控制通道**，stderr 用作诊断；不要向 stdout 打印调试文字。SDK 提供 JSON 解析、Base64 和显式 flush。每条内部 JSON 消息最多 64KiB；acm::send 自动按 8KiB 原始字节分块。选手的一行可以远超 64KiB，分块不会添加换行或修改内容。

manager 从工作目录运行，命令参数为输入和答案文件。首个 stdin 事件：

```json
{"type":"init","version":1,"mode":"Multiprocess","processCount":2,"inputPath":"input.txt","answerPath":"answer.txt"}
```

路径相对于 manager 当前工作目录，以避免 Windows C++ 窄字符路径对中文目录的编码差异。请检查读取成功，不要依赖未初始化数值。

引擎 → manager：

| 事件 | 字段 | 含义 |
|---|---|---|
| init | version, mode, processCount, inputPath, answerPath | 协议和测试数据 |
| started | id, round | 已启动通信轮次 |
| output | id, round, data | 某实例 stdout 的 Base64 字节片段 |
| exit | id, round, verdict, exitCode | 该实例退出且 stdout 已读完 |

manager → 引擎：

| 命令 | 字段 | 含义 |
|---|---|---|
| start | 无 | 仅通信模式，启动下一轮 |
| send | id, data | Base64 解码后写入实例 stdin |
| close | id | 关闭实例 stdin，后续不再发送 |
| verdict | verdict, message | AC / WA / PE / Judge Error |

多进程模式在 init 后由引擎自动启动 N 个实例，round 恒为 1。选手 `argv[1]` 为编号（0…N-1），`argv[2]` 为总数；stdin/stdout 保持普通题目协议，不要求选手使用 JSON。

通信 manager 的 processCount 为 1、id 恒为 0；先 `start` 第一轮，再收集输出和 exit，然后 `start` 第二轮。不允许同时启动两轮或第三次启动。引擎自动向每轮 stdin 写入其轮次标记与换行，其他输入由 manager 的 send 提供。只有完成两轮才允许 AC。无需第二轮读取第一轮目录；manager 应显式转发其需要的字节。

不能假定一次 output 是完整的一行或一个 token。对每个 id/round 拼接片段，**实时解析题目协议所需的行或字段并转发**；exit 只表示该实例全部输出已结束。不要等进程退出才转发：进程可能正在等另一角色的回复。多进程事件可以交错。发送最终判定后 manager 应正常退出。引擎仍检查选手正常退出与资源限制，不接受忽略选手超限的 AC。



完整的角色间往返通信见内置「双泊对照」题目的判题程序页，以及 [MultisetExample.cs](../src/Judge.Core/MultisetExample.cs)：

1. manager 读取测试文件，分别向角色 0/1 发送 n+a / n+b，保持两边输入打开。
2. 收到角色 0 的两行输出后，校验 n 与排序后的 a，并原样转发给角色 1。
3. 收到角色 1 的最终 0/1 后校验正确性，原样转发给角色 0。
4. 收到角色 0 的最终 0/1 后，确认它与角色 1 一致且正确。
5. 检查两边完整输出、正常退出及资源限制。缺失结果、额外输出、排序错误或答案错误均不能通过。

这是同时运行的两个进程，通过调度器双向通信；与通信题依次启动两轮不同。无需选手包含 manager SDK，也不会向选手 stdin 注入 JSON。Windows 文本模式可能输出 CRLF，转发保留实际字节。该示例支持 n=200000、64 位数值和跨多个 output 事件的长行，测试位于 tests/Judge.MultiprocessTests。

## HTTP API

同源 JSON 接口，默认 `http://127.0.0.1:27123`；错误返回 `{ "error": "说明" }`。浏览器跨站 Origin 被拒绝。

| 方法 | 路径 | 行为 |
|---|---|---|
| GET / PUT | `/api/environment` / `/api/settings` | 查询环境 / 设置 `{compiler:绝对路径}` |
| GET / POST | `/api/problems` | 列表（选手仅见已发布题目，管理员额外带 `published` 字段）/ 新建完整题目对象 |
| GET / PUT / DELETE | `/api/problems/{id}` | 查询 / 更新 / 删除题目，历史保留；未发布题目对非管理员返回 404 |
| POST | `/api/problems/{id}/harden` | 新数据：`{tests:[{input,answer?}]}`，1–20 条，单个输入/参考判定 ≤1 MiB。标准/特判自动计算答案；交互、通信、多进程使用提供的 `answer`（默认空）。按本题真实协议运行标准答案验证，整批 AC 才以 `h01…` 追加；失败不保存任何建议 |
| POST | `/api/problems/{id}/brute` | 候选程序：`{source}`（≤1 MiB），使用正式引擎的 checker/interactor/双轮/manager/可视化流程，首个非 AC 即停，返回 `{results:[{name,verdict,cpuMs,message}],stopped}`。编译阶段失败为 400，消息保留 CE 或 Judge Error；不得按普通 token 比较替代复杂协议 |
| POST | `/api/problems/{id}/rejudge-all` | 整题重测：该题全部历史提交按**当前**题目快照（含强化数据）重新入队，返回 `{requeued}` |
| GET | `/api/problems/{id}/export` | ZIP 下载 |
| POST | `/api/packages/preview` | multipart `file`，返回验证后的题目对象；POST 到 problems 完成导入 |
| GET / POST | `/api/submissions` | 最近 200 条 / `{problemId,source}` 创建提交（未发布题目仅管理员可提交） |
| GET | `/api/submissions/{id}` | 提交、结果、原始源码 |
| GET | `/api/submissions/{id}/events` | SSE JSON 帧，最终状态后关闭 |
| POST | `/api/submissions/{id}/cancel` | 取消排队或运行任务 |
| POST | `/api/submissions/{id}/rejudge` | 用原题目、源码、工具快照创建新提交 |

题目对象带 `published` 与 `rating` 标志：`rating` 为 CF 风格 100–3500（0=未评级），**发布与加入比赛都要求已评级**（服务端在保存题目与保存比赛时分别校验）；题目新建默认为草稿，由管理员评级、验证后显式发布。`exampleSource` 用于验题及新数据的真实协议验证；仅标准/特判模式直接运行它计算 `.ans`。管理端 LangChain 助手由浏览器直连配置的 OpenAI 兼容服务，用于评级、题面及输入提议，不能代替协议验证和人工约束检查。

源码快照与结果文件位于 submission id 对应目录。程序 stdout 以可读 UTF-8（替换非法字节）供 UI 展示，同时 `stdoutBase64` 保存原始字节；判题和双轮转发不使用有损展示文本。trace 的 dataBase64 保留方向、时间戳及原始字节。
# Codeforces 账号与同步

- `POST /api/users/me/codeforces {handle}`：申请或复用未过期的验证码（15 分钟）；`GET /api/users/me/codeforces/challenge` 返回当前账号的 `challenge {handle,code,expiresAt,minutes}` 或 null。
- `POST /api/users/me/codeforces/verify {code}`：核对 Last name，成功返回 `200 {status:"verified",user}` 并排队同步；官方资料不匹配返回 `202 {status:"pending",retryAfterSeconds:10,message,challenge}`。不匹配绝不完成绑定。错误验证码、过期、上游错误使用明确的 400 错误信息。
- `POST /api/users/me/codeforces/sync`：要求已绑定，返回 202 和后台任务状态；同一绑定的重复请求复用正在运行的任务。关闭页面不取消后台任务。
- `GET /api/users/me/codeforces/history?page=1&view=submissions`：只返回当前登录用户的数据；`view=solved` 为通过题目列表。返回 `handle,updatedAt,submissionCount,solvedCount,page,pageCount,items,sync,user`，每页 50 条；`sync` 含 `status,fetched,message`。状态为 idle / queued / running / complete / failed。
- `DELETE /api/users/me/codeforces`：解绑并清除本地 CF 记录；绑定版本防止旧任务重新写入。

官方 `user.status` 每次读取 1000 条，按提交 ID 去重；全量扫描会刷新重判结果。只有所有页和账号资料都成功时才在 SQLite 事务中替换快照。一次最多 20 万条、20 分钟，超限明确失败并保留旧快照。多个调用者共享至少 2.1 秒请求间隔，限流和临时错误最多重试两次。打开排行榜会为超过一小时的账号安排后台刷新；自动失败重试至少间隔五分钟。中断时保留最近成功快照，重新打开面板或手动同步可继续发起新任务。

接口只读取公开资料，不需要 CF 密码或 API 密钥，也不把 CF 成绩并入本站评测结果。官方参考：[API 使用约束](https://codeforces.com/apiHelp)、[user.info / user.status](https://codeforces.com/apiHelp/methods)。
