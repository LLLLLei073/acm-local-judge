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
- Northflank：用户已完成登录，当前为 Favorys's Team / Free Team。官方价格页虽然显示 No card needed，实际 Default 项目的服务创建表单却明确要求 Payment method / Add a card，并说明 Developer Sandbox 也需银行卡验证。没有创建服务、连接 GitHub 或输入应用密钥；不满足用户免绑卡要求，停止该平台部署。页面证据保存在本机 artifacts/cloud-deploy/northflank-card-required.png。

## 已准备的通用容器参数（尚未上线）

- 仅使用经实际控制台确认免绑卡的免费资源，不创建付费磁盘、GPU 或额外数据库。
- 项目、服务名 acm-local-judge；来源为公开仓库 LLLLLei073/acm-local-judge 的 codex/free-cloud-deploy 分支。
- Combined service，Dockerfile=/Dockerfile，上下文仓库根目录，单实例免费资源规格。
- 公共 HTTP 端口 8080，健康检查 /healthz。
- 运行环境需要 ACM_POSTGRES、ACM_TOKEN、ACM_SESSION_SECRET、ACM_WORKER_TOKEN、ACM_REGISTRATION_CODE；在确定平台公开域名后设置 ACM_ALLOWED_HOSTS。
- 数据库继续使用已创建的 Supabase，SSL VerifyFull 与 /app/deploy/supabase-ca.crt。
- 网站可用后启动本机 Windows Agent，并执行公开 HTTPS 注册登录、提交、结果、权限与重启持久化验收。

## 后续选择

已向用户说明并等待选择：Railway 免费额度，或本机运行网站加免费隧道；未擅自创建新的账号或资源。

- [Railway 官方试用说明](https://docs.railway.com/pricing/free-trial)：首次 $5 / 最长 30 天，之后 Free 每月 $1，不结转，不能保证此应用整月在线。账号若只能获得 Limited Trial，会限制出站网络，必须先核对能否连接 Supabase，不能把注册成功视为可部署。
- [Hugging Face 当前官方说明](https://huggingface.co/docs/hub/spaces-overview)：创建 Docker Space 已要求付费套餐，且默认出站端口不包含 PostgreSQL 5432，因此没有让用户继续注册该平台。
- 本机隧道方案需要电脑持续开机；随机临时 URL、实时推送与长期域名能力需在选择具体隧道后验证，不能直接当作独立云端托管。

部署仍未完成，没有可交付的长期公网地址。本机数据保留，未迁移到云端。
