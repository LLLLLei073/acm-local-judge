# 题目包与判题协议 v1

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

manager 是可信 C++23 判题程序，使用 `#include "acm_manager.hpp"`。manager 的 stdin/stdout 是 **UTF-8 JSON Lines 控制通道**，stderr 用作诊断；不要向 stdout 打印调试文字。SDK 提供 JSON 解析、Base64 和显式 flush。每行最多 64KiB，数据分块发送，单块建议不超过 8KiB。

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

不能假定一次 output 是完整的一行或一个 token。对每个 id/round 拼接片段，使用 exit 作为该实例输出结束标记；多进程事件可以交错。发送最终判定后 manager 应正常退出。引擎仍检查选手正常退出与资源限制，不接受忽略选手超限的 AC。

```cpp
#include "acm_manager.hpp"
#include <fstream>
int main() {
    auto init = acm::receive();
    std::ifstream in(init["inputPath"].get<std::string>());
    int x;
    if (!(in >> x)) { acm::verdict("Judge Error", "input unavailable"); return 0; }
    acm::send(0, std::to_string(x) + "\n");
    acm::close(0);
    std::string answer;
    for (;;) {
        auto e = acm::receive();
        if (e["type"] == "eof") return 3;
        if (e["type"] == "output" && e["id"] == 0) answer += acm::decode(e["data"]);
        if (e["type"] == "exit" && e["id"] == 0) break;
    }
    // 对实际题目应处理所有实例并校验答案；这里只展示 SDK 用法。
    acm::verdict(answer.empty() ? "WA" : "AC", "example verdict");
}
```

完整可运行的多进程 manager 在内置「并行求和」题目的判题程序页；管理器双轮通信范例也由原生测试覆盖。

## HTTP API

同源 JSON 接口，默认 `http://127.0.0.1:27123`；错误返回 `{ "error": "说明" }`。浏览器跨站 Origin 被拒绝。

| 方法 | 路径 | 行为 |
|---|---|---|
| GET / PUT | `/api/environment` / `/api/settings` | 查询环境 / 设置 `{compiler:绝对路径}` |
| GET / POST | `/api/problems` | 列表 / 新建完整题目对象 |
| GET / PUT / DELETE | `/api/problems/{id}` | 查询 / 更新 / 删除题目，历史保留 |
| POST | `/api/problems/{id}/tests` | multipart 文件集合，按同名配对合并 |
| GET | `/api/problems/{id}/export` | ZIP 下载 |
| POST | `/api/packages/preview` | multipart `file`，返回验证后的题目对象；POST 到 problems 完成导入 |
| GET / POST | `/api/submissions` | 最近 200 条 / `{problemId,source}` 创建提交 |
| GET | `/api/submissions/{id}` | 提交、结果、原始源码 |
| GET | `/api/submissions/{id}/events` | SSE JSON 帧，最终状态后关闭 |
| GET | `/api/submissions/{id}/logs` | 完整 JSON 结果 |
| POST | `/api/submissions/{id}/cancel` | 取消排队或运行任务 |
| POST | `/api/submissions/{id}/rejudge` | 用原题目、源码、工具快照创建新提交 |

源码快照与结果文件位于 submission id 对应目录。程序 stdout 以可读 UTF-8（替换非法字节）供 UI 展示，同时 `stdoutBase64` 保存原始字节；判题和双轮转发不使用有损展示文本。trace 的 dataBase64 保留方向、时间戳及原始字节。
