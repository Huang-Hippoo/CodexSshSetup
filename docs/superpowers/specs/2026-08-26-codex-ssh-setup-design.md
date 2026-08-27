# Codex SSH Setup 设计规格

## 1. 目标与范围

Codex SSH Setup 是一个面向 Windows 10/11 x64 的 WPF 桌面工具，用于把用户可登录的 Linux SSH 主机准备为 Codex Desktop 的远端主机，并通过 Windows 本地 iKuuuVPN 为远端 Codex 单独提供网络链路。

MVP 交付两个主流程：首次配置向导与日常连接主页。MVP 必须完成 SSH 配置、用户级 Node/Codex 安装、VPN 代理探测、反向隧道、Codex 登录辅助、状态检查和日志；托盘图标和自动重连包含在首版，但界面动画、自动更新、多主机并发等不进入 MVP。

## 2. 已知事实、假设与非目标

### 已知事实

- Windows 内置 OpenSSH 提供 `ssh.exe`、`scp.exe` 和 `ssh-keygen.exe`。
- SSH 反向转发 `-R 127.0.0.1:17890:127.0.0.1:<port>` 只让 Linux 回环地址监听该端口，前提是服务端允许 TCP forwarding。
- `HTTP_PROXY`/`HTTPS_PROXY` 只注入单条远端命令，不写入 shell profile、系统环境、conda 配置或 Slurm 环境。

### 明确假设

- 用户拥有服务器账号，并能在首次连接时通过系统 SSH 交互输入密码或处理主机指纹确认。
- 远端是常见 Linux shell 环境，存在 `/bin/sh`，并允许写入用户主目录。
- Windows 本地已安装并启动 iKuuuVPN；工具不安装、不配置 VPN，也不修改 Windows 系统代理。
- 远端 OpenSSH 服务允许公钥认证和回环反向端口转发。若管理员禁用，工具只能报告原因，不能绕过策略。

### 非目标

- 不保存或自动填写 SSH 密码、ChatGPT 密码、设备码。
- 不修改服务器系统 Node、系统 npm、`/etc`、防火墙、sshd 配置或其他用户文件。
- 不保证所有 conda 变体或所有 Linux 发行版的包管理器自动安装；用户级 Node 安装必须有确定且可回滚的路径。
- 不把代理设为系统级、用户 shell 全局级或服务器全局级。

## 3. 技术方案选择

采用 C#、WPF、.NET 8，目标 RID 为 `win-x64`。开发项目使用 MVVM 风格但不引入第三方 MVVM 框架，以减少供应链和部署体积。SSH 全部通过 Windows OpenSSH 子进程完成，不使用会接触密码的 SSH SDK。

发布提供两个版本：

1. 推荐的 framework-dependent portable 文件夹，体积较小，适合已有 .NET 8 Desktop Runtime 的机器。
2. self-contained portable 文件夹，目标机器不需要预装 .NET。若单文件发布验证通过，再额外交付单文件 EXE；不以牺牲稳定性强制单文件。

不采用 WinUI 3，因为部署和打包链更复杂；不采用 PowerShell GUI，因为后台隧道生命周期、托盘行为和自动化测试较弱。

## 4. 架构与边界

应用分为五个可独立测试的核心单元和一个 WPF 外壳：

- `Configuration`：保存非敏感主机资料、代理端口、UI 偏好和工具创建的 key 路径。配置位于 `%LOCALAPPDATA%\CodexSshSetup\config.json`，日志位于同目录 `logs`。配置不含密码、设备码或私钥内容。
- `ProcessRunner`：以隐藏窗口启动外部命令，异步读取 stdout/stderr，支持超时、取消和交互式终端模式。所有参数通过 `ProcessStartInfo.ArgumentList` 传递，避免命令注入。
- `SshProvisioning`：测试端口、生成 key、安装公钥、管理专属 SSH config 区块、测试免密、探测和安装远端 Node/Codex。
- `ProxyDetection`：确认 `iKuuuVPNCore.exe` 进程，关联其回环监听端口，优先 7890，并通过 HTTP CONNECT 到 `auth.openai.com:443` 验证代理链。
- `TunnelSupervisor`：只管理自己创建且持有的 SSH 进程；用 `ExitOnForwardFailure=yes`、keepalive 与受控退避自动重连；关闭时只终止记录的进程树。
- `CodexRemote`：查询版本和登录状态，运行仅对本条命令注入代理变量的 `codex login --device-auth`，实时将网址/验证码输出到 UI，但不持久化这些输出。
- `WPF Shell`：首次配置与日常主页、状态卡、按钮、日志窗口和托盘生命周期。UI 不直接拼接命令。

所有 SSH/SCP 调用均使用别名或经过字段验证的主机值。Host/IP、用户名、端口和别名分别校验；SSH 别名只允许字母、数字、点、下划线和连字符。

## 5. 首次配置数据流

1. 校验四个输入字段，保存非敏感草稿。
2. 用 TCP 连接测试 Host/IP 与端口，超时显示可读原因。
3. 在 `%USERPROFILE%\.ssh\codex_ssh_setup_<alias>_ed25519` 生成无口令 ed25519 key；已有同名 key 时校验公钥存在后复用。无口令 key 是日常无人值守重连的必要条件，风险通过文件 ACL、专用路径和不捆绑私钥控制。
4. 首次公钥安装使用系统 `ssh` 的交互窗口，使密码和主机指纹确认直接发生在 OpenSSH 中。远端脚本创建 `~/.ssh`、追加去重公钥，并执行目录 700、文件 600 权限。
5. 更新 `%USERPROFILE%\.ssh\config` 中由标记包围的专属区块。写入前保留 `.codex-ssh-setup.bak`，原子替换文件，只改本工具管理的别名区块。
6. 使用 `BatchMode=yes` 测试免密登录，避免测试阶段再次等待密码。
7. 探测远端 `node`、`npm`、`codex`。Node 主版本低于 20 时，不覆盖系统 Node；优先复用符合要求的 conda 环境，其次安装到 `~/.local/codex-ssh-setup/node`。安装包在 Windows 下载/准备后上传，避免远端直接访问受限网络。
8. Windows 本地运行 `npm pack @openai/codex` 到临时目录并上传 tgz。远端 npm 使用 `--prefix ~/.local/codex-ssh-setup/npm` 安装，避免 `sudo` 和真正的系统级 `npm -g`。
9. 写入工具专用 wrapper：`~/.local/bin/codex-codex-ssh-setup`。wrapper 设置本工具的 Node/npm bin PATH，并只为 Codex 进程注入 `HTTP_PROXY`、`HTTPS_PROXY` 和对应小写变量，然后 `exec codex "$@"`。不覆盖用户已有的 `codex`。
10. 验证 Node、npm、Codex 路径和版本，保留逐步状态与原始日志。

注意：原需求中的“远端 `npm install -g`”若直接执行通常会写系统 prefix 或要求 sudo，与“不影响系统级环境”冲突。MVP 将其解释为 `npm install --global --prefix <用户目录>`；这是安全边界修正，不改变安装结果的用户可用性。

## 6. iKuuuVPN 代理检测

1. 使用进程列表查找 `iKuuuVPNCore`，记录 PID，但不操作其进程。
2. 读取 Windows TCP 表，将 PID 与 `127.0.0.1`/`0.0.0.0` 监听端口关联；候选顺序为 7890、其他 PID 关联端口、常见端口 7891/1080。
3. 对候选端口发送严格的 HTTP CONNECT 请求：`CONNECT auth.openai.com:443 HTTP/1.1`。任何 2xx 表示 CONNECT 建立；407 明确报告需要代理认证；403 或 Cloudflare 页面只可能出现在 CONNECT 后的上层 HTTP 流量，不应被误判为 CONNECT 失败。
4. 若 PID 关联不可用，允许用户手动指定端口并执行同一测试。

这里需要纠正一个前提：纯 HTTP CONNECT 阶段不会正常返回 Cloudflare 403 challenge；成功依据应是代理对 CONNECT 的 2xx 响应。403 只能用于后续 HTTPS 请求已穿过隧道的旁证。

## 7. 反向隧道与恢复

启动命令等价于：

```text
ssh -N -T -R 127.0.0.1:17890:127.0.0.1:<proxyPort> \
  -o ExitOnForwardFailure=yes \
  -o ServerAliveInterval=15 \
  -o ServerAliveCountMax=3 \
  <alias>
```

进程创建时隐藏窗口并重定向日志。应用内每个主机至多一个 supervisor；重复点击连接返回现有状态。进程异常退出后按 1、2、5、10、30 秒退避重连，直到用户主动断开或应用选择“停止隧道并退出”。VPN 暂停但本地端口仍在时 SSH 隧道可以保持；VPN 进程重启导致本地监听短暂消失时，supervisor 不必重建 SSH 配置，并在端口恢复后重新验证链路。若 SSH 进程本身退出，则重连。

工具只保存自己启动的进程对象和启动令牌，不通过模糊命令行匹配杀死外部 SSH。应用崩溃后遗留进程不被下次启动盲目终止；新连接先检查远端端口占用并给出可操作提示。

## 8. Codex 登录与 Desktop 兼容

登录状态通过 wrapper 执行 `codex login status`。未登录时，用户点击“设备登录”，应用以交互输出模式执行 `codex login --device-auth`。输出只存在于内存日志视图；检测到 URL 与短码后突出显示并提供复制按钮，登录结束后清除设备码展示，磁盘日志过滤疑似设备码。

Codex Desktop 的远端探测兼容策略分两层：

- 不覆盖 `codex` 时，应用在最终页展示可复制的远端可执行路径 `~/.local/bin/codex-codex-ssh-setup`，供支持自定义启动命令的 Desktop 版本使用。
- 若 Desktop 只探测 `codex`，提供显式“安装用户级 codex wrapper”选项：仅当 `~/.local/bin/codex` 不存在，或确认它由本工具管理时创建；已有非本工具文件绝不覆盖。SSH config 可选写入 `RemoteCommand` 不适合 Codex Desktop 探测，因此不采用。

Desktop 具体版本的远端 CLI 启动接口可能变化，MVP 不假设未验证的私有协议。应用会验证 SSH shell 中 `command -v codex` 的可见性，并在 README 中说明若 Desktop 不读取 `~/.local/bin`，需要在 Desktop 的远端设置中选择 wrapper 路径或由用户显式启用用户级 shim。

## 9. GUI 与状态模型

主窗口包含：

- 首次配置页：Host/IP、用户名、端口、别名；VPN 自动检测结果；“开始配置”和“取消”。
- 日常主页：VPN、SSH、Tunnel、Codex CLI、Login 五张状态卡；“连接”“断开”“重新检测”“设备登录”“查看日志”。
- 日志窗口：人类可读摘要与可展开原始输出，敏感信息过滤后才写盘。
- 托盘菜单：显示、连接、断开、退出。关闭按钮首次弹出“隐藏并保持隧道”或“停止隧道并退出”，以后可记住非敏感偏好。

每项状态为 Unknown、Checking、Ready、Warning 或 Failed，并携带短说明。长任务支持取消；UI 线程不得被网络或进程等待阻塞。

## 10. 错误处理与可观测性

错误分为输入错误、网络错误、认证错误、远端能力错误、代理错误、策略限制和内部错误。UI 显示翻译后的结论，同时保存退出码、stderr 和步骤上下文。常见映射至少覆盖：

- `Connection refused`：端口不可达或 sshd 未监听。
- `Permission denied`：账号、密码/密钥或服务端认证策略错误。
- `remote port forwarding failed`：17890 被占用或服务端禁用转发。
- `codex: command not found`：安装失败或 PATH/探测路径不包含 wrapper。
- 代理 CONNECT 超时/拒绝/407：VPN 未启动、端口错误或需认证。

日志轮转保留最近 7 个文件，每个最大 2 MiB。写盘前过滤设备码、Bearer token、Authorization header 和可能的 URL 查询凭证。

## 11. 安全模型

- 外部命令参数不经 shell 字符串拼接；必要的远端 shell 脚本通过标准输入传递，并对数据采用固定格式/编码。
- SSH config 更新使用专属标记、备份和原子替换。
- 私钥永不读取进应用内存、不复制到交付物、不写日志。
- 配置只含非敏感字段，设备登录输出不持久化。
- 所有服务器写入限定在当前用户的 `~/.ssh`、`~/.local/codex-ssh-setup` 和经确认的 `~/.local/bin` 管理文件。
- 不自动接受变化的主机密钥；首次确认交给 OpenSSH，后续遵守 `known_hosts`。

## 12. 测试与验收

自动化测试覆盖：输入验证、SSH config 区块的新增/更新/保留其他内容、远端脚本转义、版本解析、代理 CONNECT 响应、日志脱敏、状态聚合、隧道重连退避与只终止自有进程。进程和网络边界通过接口注入，核心测试不要求真实服务器。

构建验收包括：

- `dotnet test` 全部通过。
- Debug 与 Release 构建通过，无编译错误。
- `win-x64` portable 发布产生可启动 EXE。
- 在 Windows 上进行无服务器烟雾测试：应用启动、字段校验、VPN 未运行提示、日志窗口、关闭行为。
- 提供可选人工集成清单：真实 SSH 首次密码、公钥去重、Node/Codex 用户级安装、隧道、设备登录和 Desktop 刷新。没有用户提供的真实服务器凭证时，不宣称真实端到端链路已验证。

## 13. MVP 之后

后续版本可增加多主机并发、Windows 启动项、自动更新、可选 Pageant/硬件 key、多语言和更细的远端发行版适配。它们不应推迟 MVP 的安全核心与可运行交付。
