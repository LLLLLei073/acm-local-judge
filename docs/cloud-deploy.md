# 免费云端部署

更新时间：2026-10-22。Supabase Free 已就绪，网页托管仍未完成：Render 要求绑卡；Back4app Free 实测只提供 60 分钟临时公开 URL，不适合长期托管。以下保留配置参考，不代表已上线。实际发布状态见 [验收记录](cloud-verification-2026-10-22.md)。

## 架构与数据

一个 Docker Web Service 同时提供 Vue 网页与 ASP.NET Core API，保持同源 Cookie。Supabase PostgreSQL 保存账号、题目、比赛、CF 历史、提交和结果。Windows Agent 通过 HTTPS 主动领取任务，在本机使用 GCC 与 Job Objects 执行五类评测，网页容器不执行提交代码。

本次使用全新云端数据库，不自动迁移本机账号、私有题目或提交记录。本机 SQLite 与现有服务保留；需要的题目可通过题目包导入。

提交的题目、源码、测试数据、工具包快照和结果日志，以 gzip 压缩后存入 PostgreSQL 的 acm.remote_jobs。本版本没有创建 Supabase Storage bucket；压缩数据仍占数据库容量。免费套餐容量有限，应按需导出、删除历史提交。

## 资源归属

- Supabase：xlei74647@gmail.com's Org / plbalfgigiqrfpwcuwro，项目 acm-local-judge / qsscjqrtzkfmyodoicco，新加坡，Free。
- Render：Lei's workspace / tea-dbca0lon74is73fs2sqg，服务 acm-local-judge，新加坡，Free。
- GitHub：LLLLLei073/acm-local-judge。部署分支、提交和公开 URL 见发布验收记录。

不启用付费实例、磁盘或数据库升级。免费服务休眠、构建和容量限制以平台当前控制台为准。

## 数据库初始化

1. 在专用 Supabase 项目执行 deploy/postgres-schema.sql。acm 是私有 schema，不加入 Data API exposed schemas。
2. 创建独立登录角色 acm_backend，单独生成随机密码；执行 deploy/postgres-role.sql，只授予应用 schema 的权限。
3. 使用控制台给出的 Session pooler（5432），用户名 acm_backend.<project-ref>。本次主机 aws-0-ap-southeast-1.pooler.supabase.com；新项目需重新查询。
4. 必须使用 SSL Mode=VerifyFull；deploy/supabase-ca.crt 是 Supabase 官方公开根证书，不含秘密。

## Back4app Containers 设置

**限制：控制台显示 Free URL 只有效 60 分钟，永久地址需要升级。用户要求只免费，因此不在这里继续正式上线。**

官方免费方案无需信用卡，0.25 CPU、256 MB 内存、美国区域；实际限额以控制台为准。先完成账号登录，再将 GitHub 授权限定为本仓库，选择分支 codex/free-cloud-deploy、根目录 /、根目录 Dockerfile、Free 套餐。

本次改用控制台的 Upload files 入口：GitHub 集成会请求代码写权限，而源码上传可以直接部署公开提交的快照。已从提交 4849d1a 导出仅含构建依赖的文件夹 artifacts/cloud-deploy/back4app-source-4849d1a（90 个文件、约 1.75 MB），排除了凭据、本机数据库、构建产物和内部目录。上传方式不自动跟随 GitHub 更新，更新时需重新上传。浏览器扩展未允许本地文件 URL 时，请在网页手动选择这个文件夹，不需要因此扩大扩展权限。

Dockerfile 声明端口 8080，默认 PORT=8080，健康检查 /healthz。配置下表的五个秘密变量；获得容器公开域名后，还必须设置 ACM_ALLOWED_HOSTS 为该域名（不含 https:// 或路径）。否则应用会拒绝该 Host。若平台自定义端口，同时修改 PORT 和容器公开端口。

先保持自动部署开启；只有构建成功、容器可用、公开 HTTPS 网页和真实评测通过，才算发布完成。

## 可选 Render 设置

运行时 Docker，根目录 Dockerfile，套餐 free。容器非 root 运行，监听 0.0.0.0:$PORT，健康检查 /healthz。公开主机自动加入 Host 白名单。

以下变量只放服务端，禁止放入 VITE_* 或仓库：

| 变量 | 用途 |
|---|---|
| ACM_POSTGRES | Npgsql 连接串，包含 SSL Mode=VerifyFull;Root Certificate=/app/deploy/supabase-ca.crt |
| ACM_TOKEN | 管理端令牌 |
| ACM_SESSION_SECRET | 稳定会话密钥，重建时不可重置 |
| ACM_WORKER_TOKEN | Windows Agent 独立令牌 |
| ACM_REGISTRATION_CODE | 可信成员注册邀请码 |

四个密钥至少 16 字符，本次使用随机 64 位十六进制字符串。本机凭据保存在被 Git 忽略的 artifacts/cloud-deploy/secrets.json，已限制当前 Windows 用户访问。adminToken 用于 /admin，inviteCode 用于注册；不要上传或分享整个文件。

## Windows Agent

需要 .NET 10 SDK 和 C++23 GCC。网页和数据库独立运行，但实际评测需要电脑开机且 Agent 正在运行。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/start-agent.ps1 -Url https://实际服务.onrender.com
```

默认编译器 D:\mingw64\bin\g++.exe，可用 -Compiler 修改。脚本读取本机凭据文件。Ctrl+C 正常停止；不要同时启动多个 Agent，默认一次评一个提交。

- 无任务每 10 秒拉取，执行中每 5 秒心跳；租约 90 秒。
- 取消、网络中断或租约拒绝会停止所属进程；过期任务标记 Interrupted，可手动重评。
- 未领取任务保留排队状态；容器重建不会丢失提交快照。
- 每个任务独立临时目录，结束后清理。一键验题和答案生成也由 Agent 执行，完成后删除临时出题任务。

这是可信代码训练工具。Job Objects 不提供完整文件和网络隔离，邀请码只发给可信成员。

## 可选 Cloudflare Pages

初次上线不依赖 Cloudflare。已有 web/functions/[[path]].js 与 web/deployment/proxy.mjs 可将 /api、/admin、/admin.html、/healthz 流式代理到 Render。Pages 根目录 web，构建 pnpm install --frozen-lockfile && pnpm run build，输出 dist，Node 22.12+，服务端 ACM_BACKEND_ORIGIN 为 Render HTTPS origin。需要支持 Functions 的 Git 集成或 Wrangler，不能只上传 dist。此可选路径已有本地测试，尚未部署 Cloudflare。

## 验证

tests/Judge.CloudTests 需独立验收数据库，不可对有其他用户和 Agent 的生产队列执行。设置 ACM_TEST_POSTGRES；真实五类评测另设 ACM_CLOUD_E2E=1，先发布 Server 到 artifacts/cloud-server，Agent 到 artifacts/agent。

覆盖数据库读写、压缩快照、临时目录丢失后恢复、独立权限、租约和中断、五类真实评测及资源最大值统计、远程验题和答案生成、编译错误、取消后继续评测。SQLite API、Codeforces 回归和前端测试单独运行。最新结果见发布验收记录。
