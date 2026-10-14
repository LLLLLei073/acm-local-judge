# 五类示例题目包

在网页左下角「导入题目包」选择 ZIP，预览后导入。导入创建新题目，不覆盖内置题库。

| 文件 | 示例 |
|---|---|
| Standard.zip | A + B，两个测试点 |
| Special.zip | 浮点平方根，testlib checker |
| Interactive.zip | 三轮加倍，testlib interactor |
| Communication.zip | first / second 双轮转发 |
| Multiprocess.zip | 双泊对照，角色 0 排序 → 角色 1 比较 → 角色 0 确认，调度器实时转发 |
| VisualFlow.zip | 双向接力，无需 manager 源码，在「成员与通信」编辑五步流程 |

每个包包含题面、数据、必要判题源码、正确和错误示例。入口在 `src/Judge.Core/Examples.cs`，双泊对照定义在 `src/Judge.Core/MultisetExample.cs`，ZIP 往返导入导出由 `tests/Judge.ApiTests` 验证。可视化流程示例由 `src/Judge.Core/FlowExample.cs` 定义，完整协议见 [communication-flow.md](../docs/communication-flow.md)。

双泊对照测试文件为三行：n、a、b，仅供调度器读取。角色 0 最初得到 n+a，角色 1 最初得到 n+b；后续通过调度器原样转发。双方最终 0/1 必须一致且正确，重复次数不同必须判为不同多重集。
