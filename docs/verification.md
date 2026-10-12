# 本机验收记录

验收日期：2026-10-12（Asia/Shanghai）。环境：Windows x64、GCC 15.2.0、.NET SDK 10.0.400、Node.js 22.22.2、pnpm 11.15.0。

## 自动测试

- 原生引擎：47 项检查，使用真实 C++23 编译与运行，不是模拟结果。
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

自动测试证据位于 `artifacts/smoke-*/report.json`、`artifacts/api-*/report.json`，逐程序结果在对应目录；最近一次原生控制台输出保存在 `artifacts/native-verification-final.log`。

## 浏览器验收

通过 Codex 内置浏览器实际操作完成：

1. 新建「浏览器验收 · 求和」，编辑 Markdown 题面。
2. 批量上传 `01.in / 01.ans`，看到配对预览并保存。
3. 打开 Monaco，载入 C++ 源码并提交；通过 SSE 显示 AC。
4. 下载该题 ZIP，从页面重新上传并确认导入；题库新增独立题目。
5. 网页提交多进程示例，显示 AC；详情显示进程 0/1 输出 9/16 和四段消息时间线。
6. 检查 390px 窄屏布局，文档宽度未超过视口；随后恢复默认视口。

截图：`artifacts/screenshots/multiprocess-result.jpg`。浏览器验收使用 `artifacts/browser-data`，正式启动使用 `data`，不混入验收题目。

## 验收边界

本次覆盖本机 Windows 环境与内置/构造协议程序；没有宣称 Linux 兼容、真实赛事平台协议兼容、公网恶意代码沙箱隔离或大规模并发能力。多进程上限可配置到 16，实际协作正反例以两个实例验证。CPU 时间具有 Windows 计时粒度；内存采用提交内存，不能直接与 Linux RSS 排名比较。
