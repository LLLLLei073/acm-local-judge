# 云端部署续办记录

日期：2026-10-23。

## Docker 修复已验证

构建失败根因：安装依赖前未复制 web/pnpm-workspace.yaml，导致 pnpm 无法读取仓库既有的 esbuild 构建许可。

修复提交：f3807efecad000f00a74acb3d141a68ad16dea54，分支 codex/free-cloud-deploy。远程分支与本机 HEAD 已核对一致。

[GitHub Actions 39877641700](https://github.com/LLLLLei073/acm-local-judge/actions/runs/39877641700) 已完成，结论 success：

- 标准 Ubuntu runner 上构建完整 Docker 镜像成功。
- 前端依赖安装、生产构建、.NET 后端发布均成功。
- 容器内检查 index.html、admin.html、Judge.Server.dll 和 Supabase CA 证书存在。
- 检查容器中 .NET 运行时可用。

该检查没有连接生产数据库或运行网站，不能代替公网部署验收。构建状态与步骤结论保存在 artifacts/cloud-deploy/linux-container-build-status.json；完整日志下载曾遇 TLS 握手超时，可直接打开上述 Actions 页面查看。

## 托管选择

- Render：创建 Free 服务返回 402，要求付款信息；未创建服务。
- Back4app：用户已创建 Free 应用，但控制台明确显示临时 URL 仅有效 60 分钟。未升级，未向该平台配置数据库凭据。
- Northflank：2026-10-23 官方 https://northflank.com/pricing 页面显示 Sandbox Free / No card needed / Always on，提供 2 个服务、2 个任务和 1 个数据库额度。已打开账号入口，等待用户完成注册或登录；实际创建时仍须核对免费套餐，不能选择 Pay as you go。

## Northflank 待执行参数

- 仅使用 Northflank Cloud 免费 Sandbox，不创建 BYOC、磁盘、GPU、额外数据库或付费资源。
- 项目、服务名 acm-local-judge；来源为公开仓库 LLLLLei073/acm-local-judge 的 codex/free-cloud-deploy 分支。
- Combined service，Dockerfile=/Dockerfile，上下文仓库根目录，单实例免费资源规格。
- 公共 HTTP 端口 8080，健康检查 /healthz。
- 运行环境需要 ACM_POSTGRES、ACM_TOKEN、ACM_SESSION_SECRET、ACM_WORKER_TOKEN、ACM_REGISTRATION_CODE；在确定平台公开域名后设置 ACM_ALLOWED_HOSTS。
- 数据库继续使用已创建的 Supabase，SSL VerifyFull 与 /app/deploy/supabase-ca.crt。
- 网站可用后启动本机 Windows Agent，并执行公开 HTTPS 注册登录、提交、结果、权限与重启持久化验收。

部署仍未完成，没有可交付的长期公网地址。本机数据保留，未迁移到云端。
