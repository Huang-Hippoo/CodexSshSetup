# Codex SSH Setup

一个 Windows 10/11 x64 图形工具，用来把 Linux SSH 主机准备为 Codex Desktop 远端主机，并让远端 Codex 通过 Windows 本地 iKuuuVPN 访问登录与 API 服务。

## 它会做什么

- 测试 SSH Host/IP 与端口。
- 创建或复用专用 ed25519 key，通过系统 OpenSSH 窗口安装公钥。
- 只更新 Windows `~/.ssh/config` 中带有 `CODEX SSH SETUP` 标记的区块，并备份原配置。
- 检测远端 Node、npm 与 Codex；Node 低于 20 时优先使用 conda，否则由 Windows 下载并校验官方 Node 20 Linux 包后上传到服务器。
- 在 Windows 执行 `npm pack @openai/codex`，上传 tgz，并安装到远端用户目录。
- 检测 `iKuuuVPNCore.exe`、PID 对应监听端口与 HTTPS CONNECT。
- 维护 `127.0.0.1:17890` 反向 SSH 隧道，断线自动重连。
- 检查 Codex 登录状态并运行设备登录。
- 提供首次配置页、日常连接页、状态卡、日志与托盘菜单。

## 安全边界

- 不保存 SSH 密码、ChatGPT 密码、设备码或私钥内容。
- 首次 SSH 密码只输入到 Windows OpenSSH 自己的窗口。
- 不修改 Windows 系统代理。
- 不写 Linux `/etc`、系统 Node/npm、shell profile、conda 全局配置或 Slurm 环境。
- Node 与 Codex 安装在 `~/.local/codex-ssh-setup`。
- Linux 代理端口只绑定 `127.0.0.1:17890`。
- 退出时只关闭本工具持有的 SSH 进程，不按名称扫描或终止其他 SSH 会话。

专用 SSH key 默认不设口令，以支持日常无人值守重连。它只存放在当前 Windows 用户的 `.ssh` 目录，不包含在程序或源码包中。若你的安全策略要求带口令 key，请使用 ssh-agent 预加载密钥；当前 MVP 不管理 ssh-agent。

## 使用方法

### 首次配置

1. 先启动 iKuuuVPN，并确认所选节点可用。
2. 运行 `CodexSshSetup.exe`。
3. 填写服务器 Host/IP、SSH 用户名、端口和别名。代理端口通常是 `7890`。
4. 点击“开始一键配置”。
5. OpenSSH 窗口出现时，核对主机指纹并输入服务器密码。工具本身不会看到密码。
6. 若 Login 状态显示未登录，点击“设备登录”，从内存日志区域复制网址和验证码，在浏览器完成授权。
7. 状态就绪后，回到 Codex Desktop 的“设置 → 连接 → SSH”刷新主机。

当前 EXE 未做商业代码签名，Windows SmartScreen 首次运行可能显示未知发布者；请核对本次交付的 SHA-256 后再选择运行。

### 日常连接

1. 启动 iKuuuVPN。
2. 启动本工具并点击“连接”。它不会重复安装。
3. 需要暂时关闭窗口时选择“是”，工具会隐藏到托盘并保持隧道。
4. 从托盘选择“停止隧道并退出”，或在关闭对话框选择“否”，才会终止本工具创建的隧道。

## 远端文件

- Node：`~/.local/codex-ssh-setup/node`
- npm global prefix：`~/.local/codex-ssh-setup/npm`
- Codex 专用 wrapper：`~/.local/bin/codex-codex-ssh-setup`
- 若 `~/.local/bin/codex` 原先不存在，工具会创建指向专用 wrapper 的符号链接；已有文件绝不覆盖。

wrapper 只给 Codex 进程设置以下变量，不全局 export：

```text
HTTP_PROXY=http://127.0.0.1:17890
HTTPS_PROXY=http://127.0.0.1:17890
http_proxy=http://127.0.0.1:17890
https_proxy=http://127.0.0.1:17890
```

如果某个 Codex Desktop 版本允许指定远端 CLI 路径，优先选择 `~/.local/bin/codex-codex-ssh-setup`。如果它只执行 `codex`，确保远端非交互 shell 的 PATH 包含 `~/.local/bin`。MVP 不修改 shell profile，因此某些服务器需要管理员或用户自行让 SSH 非交互 shell看到该目录。

## 常见错误

- `Connection refused`：Host/端口错误、网络不通或 sshd 未监听。
- `Permission denied`：用户名、密码/密钥或服务器认证策略有误。
- `remote port forwarding failed`：17890 被占用，或服务器禁止 TCP forwarding。
- VPN 已运行但 CONNECT 失败：代理端口不对、节点不可用或代理要求认证。
- Node 安装失败：远端没有 conda，且架构不是 x86_64/aarch64，或远端缺少支持 xz 的 `tar`。
- Codex tgz 安装仍访问 npm：Codex 的 Linux 原生二进制是平台可选依赖；上传主 tgz 能减少下载，但当前 MVP 不是完全离线安装器。

## 配置和日志

- 配置：`%LOCALAPPDATA%\CodexSshSetup\config.json`
- 日志：`%LOCALAPPDATA%\CodexSshSetup\logs`
- SSH config 备份：`%USERPROFILE%\.ssh\config.codex-ssh-setup.bak`

磁盘日志会过滤 Authorization、Bearer token、URL 查询凭证和设备码；登录网址与设备码只显示在本次运行的内存日志区域。

## 构建

需要 .NET 8 SDK：

```powershell
.\scripts\publish.ps1
```

脚本会运行测试并生成 framework-dependent、self-contained 与 single-file 三种 `win-x64` 发布结果。推荐普通用户使用 self-contained portable ZIP 或 single-file EXE。

## 验证范围

自动测试覆盖输入校验、SSH config 保留/替换、命令参数边界、用户级安装脚本、代理 CONNECT 判定、Node 清单与 SHA-256 选择、PID 端口解析、日志脱敏、配置原子写入、真实子进程输出和隧道归属/重连状态机。

没有真实服务器账号、主机指纹和 iKuuuVPN 会话时，构建过程只能完成 Windows 启动烟雾测试。真实端到端验收应依次确认：首次密码窗口、公钥去重、免密 SSH、Node/Codex 版本、Linux `127.0.0.1:17890`、设备登录以及 Codex Desktop SSH 刷新。

## 许可证

本项目采用 [MIT License](LICENSE) 开源。你可以使用、修改、分发及用于商业用途，但必须保留原始版权和许可证声明。本软件按“原样”提供，不附带任何担保。
