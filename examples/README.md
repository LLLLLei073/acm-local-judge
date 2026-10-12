# 五类示例题目包

在网页左下角「导入题目包」选择 ZIP，预览后导入。导入创建新题目，不覆盖内置题库。

| 文件 | 示例 |
|---|---|
| Standard.zip | A + B，两个测试点 |
| Special.zip | 浮点平方根，testlib checker |
| Interactive.zip | 三轮加倍，testlib interactor |
| Communication.zip | first / second 双轮转发 |
| Multiprocess.zip | 两个进程平方和，C++ manager |

每个包包含题面、数据、必要判题源码、正确和错误示例。源定义统一维护在 `src/Judge.Core/Examples.cs`，ZIP 往返导入导出由 `tests/Judge.ApiTests` 验证。
