# 云端发布验收记录

日期：2026-10-22。

## 当前发布状态

尚未获得公网服务地址。Render 在明确指定 plan=free 后返回 HTTP 402：账号必须先添加付款信息，服务没有创建。未切换付费套餐，未创建磁盘。

用户已选择更换无需绑卡的平台。备选改为 Back4app Containers Free，官方说明无需信用卡并支持 Docker/.NET；注册页已打开，等待用户完成登录与必要的账户授权。Supabase 数据库继续复用。

用户随后完成 Back4app 登录。当前控制台停留在 Upload files 来源选择页；准备从 GitHub 提交 4849d1a 上传干净源码。GitHub 集成请求读写代码、提交状态和 PR 权限，未批准该授权。自动上传因扩展没有本地文件访问权限失败，需用户手动选择准备好的源码文件夹，随后才能配置并创建免费容器。

Supabase Free 项目 qsscjqrtzkfmyodoicco 已创建且健康；私有 acm schema、受限数据库角色和 RLS 已就绪。安全 Advisor 没有返回警告。数据库连接使用 VerifyFull 和官方根证书。

代码已推送 GitHub 分支 codex/free-cloud-deploy，提交 e0fdb3f9c62a5e2f1ac8921580c6f1cb5e670144；推送后远程哈希核对一致。未提交本机数据、部署秘密、.zcode 或生成的 VisualFlow.zip 变动。

## 已验证

- 前端测试 30 项通过，生产构建通过。保留已有的大 chunk 提示。
- SQLite API / 题包回归 202 项通过。
- Codeforces 绑定、分页同步与账户隔离回归 40 项通过（模拟 CF API，不代表外网 CF 当前可达）。
- 真实 Supabase + 本机 HTTP 后端 + Windows Agent 首轮 40 项通过：独立权限、持久化、快照、租约与取消状态、五类编译运行、逐实例最大值资源口径、远程验题和答案生成。
- 后续复测五类再次通过；答案生成时遇到数据库连接超时，领取响应已取消的任务最终按租约中断，返回明确错误，没有被误判为 WA。原始证据保存在 artifacts/cloud-deploy/e2e-network-interruption.log。
- 针对网络中断后的出题/取消流程再次执行，29 项全部通过：远程验题、答案生成、CE、真实取消、原生进程退出和取消后下一任务 AC；证据 artifacts/cloud-deploy/e2e-authoring.log。
- 免绑卡部署适配已推送提交 4849d1a3ab159f711d2cb52e4fe7cb7db6bb9ece，远程哈希核对一致；Docker 默认监听并 EXPOSE 8080。

## 剩余边界

Back4app 登录完成后，仍需验证 Docker 云端构建、容器可用、公开 HTTPS 网页、注册登录和真实公网评测。当前本机数据没有迁移到云端。Cloudflare 可选代理尚未上线。

评测需要 Windows Agent 持续运行；当前验收启动的临时服务会在测试结束时停止，不代表正式常驻评测端已经上线。
