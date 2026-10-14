# 可视化成员与通信流程

入口：多进程题 → 编辑题目 → **成员与通信**。导入 `examples/VisualFlow.zip` 可体验完整五步往返示例。

## 编辑与执行

- 成员可添加、改名、删除，共 2–16 个，编号对应选手 `argv[1]`，`argv[2]` 为成员总数。调度器固定存在。被步骤引用的成员不能直接删除；其他成员删除后编号与步骤引用同步更新。
- 拖动步骤标题或使用上下箭头排序，点击标题展开设置。删除或移动步骤不能使其他步骤引用尚未收到或已删除的消息。
- **发送数据**：向指定成员写入测试输入、参考答案、固定文本或前面收到的消息。输入/答案/消息支持按行截取，从 1 开始，行数 0 表示直到末尾。最多追加 16 个片段，按顺序原样拼接，例如先发输入第 1 行的 n，再发第 3 行的 b。
- **接收 / 转发**：等待指定成员输出指定数量的完整行（每行以 LF 结束，支持 CRLF）。可与参考数据按 ASCII token 精确比较；通过后原样转发给另一成员，或仅保留在调度器。前面的接收内容也可作为后续期望答案。
- **关闭输入**：向成员提供 EOF，之后不能再向它发送或转发。流程结束时会自动关闭所有输入、等待正常退出，并拒绝未被步骤消费的额外输出。

勾选 **用此流程执行评测** 后，流程替代本题 manager；原 manager 源码仍保存。未勾选时步骤只展示样例，评测继续使用原 C++ manager。启用时至少需要一处输出校验，否则保存失败。

“题面样例内容”只生成题面通信表，绝不会作为实际转发数据或隐藏测试答案。发送步骤样例留空时，使用第一个测试点和数据来源生成；接收步骤样例手动填写。旧 Markdown 表格不会自动导入、覆盖或改写 manager。

## 判题语义与边界

首版用于多进程题的有限、顺序、按行协议。成员同时启动，调度器按步骤消费输出；其他成员提前输出的内容暂存，OS 管道事件到达顺序不是协议顺序。接收包完成前不转发其部分内容，完成后保留原始字节，不补换行、不替选手 flush。

无换行的半条消息不算完整行，正常提前退出为 WA；等待依然受每个成员的 CPU、墙钟、内存和输出限制。异常退出是 RE，超限为 TLE/MLE/OLE，配置或基础设施错误为 Judge Error，取消为 Cancelled。时间、内存仍取各成员最大值。

不提供条件分支、循环、动态行数、数值计算、浮点或自定义算法校验，这些协议继续使用 C++ manager。交互题 interactor、通信题两轮启动和其他评测模式保持原行为。

## 数据格式

`problem.json` 增加可选 `flow`，题目包外层格式仍为 v1。旧题没有该字段时保持原行为；旧版应用不能执行可视化流程题。

```json
{
  "flow": {
    "version": 1,
    "enabled": true,
    "members": ["发起方", "应答方"],
    "steps": [
      {
        "id": "give-a", "kind": "send", "to": 0,
        "data": { "source": "input", "startLine": 1, "lineCount": 1 },
        "additionalData": [], "sample": "3\n"
      },
      {
        "id": "relay-a", "kind": "receive", "from": 0, "to": 1,
        "lines": 1, "check": true,
        "expected": { "source": "input", "startLine": 1, "lineCount": 1 },
        "sample": "3\n"
      }
    ]
  }
}
```

这只是字段示例，完整往返协议见 `FlowExample.Create()`。kind 可选 send/receive/close，receive 的 to=-1 表示仅调度器。source 可选 literal/input/answer/message，literal 读取 text，message 通过 stepId 引用前面接收步骤的稳定 ID；重排不会改变 ID。

最多 256 步，每份样例或固定文本最多 65536 字符。测试数据行范围在保存时校验，动态消息范围在运行时校验。CRUD、ZIP、SQLite、提交快照保存完整 flow，重评使用原流程。

## 验证

`dotnet run --project tests/Judge.FlowTests -c Release` 使用真实 C++23/Windows Job，覆盖分片与长行、CRLF、多个测试点、错误答案、提前退出、额外输出、非法 UTF-8、无 flush、CE/RE/TLE/OLE、取消、EOF、拼接数据、旧 manager、ZIP 和快照。HTTP/Worker 验证在 `tests/Judge.ApiTests`。
