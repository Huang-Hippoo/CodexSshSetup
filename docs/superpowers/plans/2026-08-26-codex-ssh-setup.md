# Codex SSH Setup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a usable Windows WPF tool that provisions a Linux SSH host for Codex Desktop, detects iKuuuVPN, maintains a Codex-only reverse proxy tunnel, and assists device login without storing credentials.

**Architecture:** A dependency-free .NET 8 core library owns validation, SSH configuration, command construction, proxy probing, sanitization, and tunnel policy. A WPF app supplies process/network adapters, orchestration, persistence, status presentation, and tray lifecycle. Every external boundary is represented by a focused service so destructive or credential-sensitive behavior can be reviewed independently.

**Tech Stack:** C# 12, .NET 8, WPF, Windows OpenSSH, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-26-codex-ssh-setup-design.md`

## Global Constraints

- Target Windows 10/11 x64 and publish a self-contained portable build.
- Use Windows built-in `ssh.exe`, `scp.exe`, and `ssh-keygen.exe`; never bundle or read private key contents.
- Never save SSH passwords, ChatGPT passwords, device codes, authorization headers, or bearer tokens.
- Never modify Windows system proxy, Linux system Node/npm, `/etc`, shell profiles, conda configuration, Slurm, or another user's files.
- Bind the remote proxy only to `127.0.0.1:17890`.
- Install Node and Codex below `~/.local/codex-ssh-setup`; do not invoke sudo.
- Pass local process arguments through `ProcessStartInfo.ArgumentList`; validate all user-controlled identifiers.

---

### Task 1: Solution skeleton and validation core

**Files:**
- Create: `CodexSshSetup.sln`
- Create: `src/CodexSshSetup.Core/CodexSshSetup.Core.csproj`
- Create: `src/CodexSshSetup.Core/Models/ServerProfile.cs`
- Create: `src/CodexSshSetup.Core/Validation/ProfileValidator.cs`
- Create: `tests/CodexSshSetup.Core.Tests/CodexSshSetup.Core.Tests.csproj`
- Create: `tests/CodexSshSetup.Core.Tests/ProfileValidatorTests.cs`

**Interfaces:**
- Produces: `ServerProfile(string Host, string UserName, int Port, string Alias, int RemoteProxyPort = 17890)`.
- Produces: `ProfileValidator.Validate(ServerProfile profile) -> IReadOnlyList<string>`.

- [ ] Write tests proving valid hostnames/IPs are accepted and shell metacharacters, whitespace aliases, empty usernames, invalid ports, and unsafe remote ports are rejected.
- [ ] Run the focused tests and confirm they fail because the types are missing.
- [ ] Implement immutable models and validation using allowlists.
- [ ] Run the focused tests and full suite.

### Task 2: SSH config ownership and atomic updates

**Files:**
- Create: `src/CodexSshSetup.Core/Ssh/SshConfigManager.cs`
- Create: `tests/CodexSshSetup.Core.Tests/SshConfigManagerTests.cs`

**Interfaces:**
- Produces: `SshConfigManager.Upsert(string existing, ServerProfile profile, string identityFile) -> string`.
- Produces: `SshConfigManager.ManagedBlockId(string alias) -> string`.

- [ ] Write tests for creating a managed block, replacing the same alias, preserving unrelated hosts/comments, using forward-slash identity paths, and rejecting malformed aliases.
- [ ] Run tests and verify the missing implementation failure.
- [ ] Implement deterministic begin/end markers and newline preservation.
- [ ] Run all tests.

### Task 3: Version parsing, command plans, and secret filtering

**Files:**
- Create: `src/CodexSshSetup.Core/Commands/CommandSpec.cs`
- Create: `src/CodexSshSetup.Core/Commands/SshCommandFactory.cs`
- Create: `src/CodexSshSetup.Core/Parsing/ToolVersion.cs`
- Create: `src/CodexSshSetup.Core/Logging/SecretRedactor.cs`
- Create: `tests/CodexSshSetup.Core.Tests/CommandAndParsingTests.cs`
- Create: `tests/CodexSshSetup.Core.Tests/SecretRedactorTests.cs`

**Interfaces:**
- Produces: `CommandSpec(string FileName, IReadOnlyList<string> Arguments, string? StandardInput = null, bool Interactive = false)`.
- Produces factory methods for key generation, interactive key install, batch SSH test, SCP upload, remote inspection, tunnel, login status, and device login.
- Produces: `ToolVersion.TryParseNode(string, out Version)` and `IsNode20OrNewer`.
- Produces: `SecretRedactor.Redact(string) -> string`.

- [ ] Write tests proving exact argument boundaries, reverse bind address, keepalive options, user-prefix npm install, wrapper-only proxy variables, version parsing, and redaction of device codes/tokens while retaining useful errors.
- [ ] Verify red tests.
- [ ] Implement the minimal immutable command model and factory without local shell concatenation.
- [ ] Implement anchored redaction patterns for authorization headers, bearer tokens, URL query secrets, and device-code lines.
- [ ] Run all tests.

### Task 4: Proxy CONNECT probe and tunnel retry policy

**Files:**
- Create: `src/CodexSshSetup.Core/Proxy/HttpConnectProbe.cs`
- Create: `src/CodexSshSetup.Core/Tunnel/RetryPolicy.cs`
- Create: `tests/CodexSshSetup.Core.Tests/HttpConnectProbeTests.cs`
- Create: `tests/CodexSshSetup.Core.Tests/RetryPolicyTests.cs`

**Interfaces:**
- Produces: `HttpConnectProbe.ParseResponse(string) -> ProxyProbeResult` with `Success`, `StatusCode`, and message.
- Produces: `RetryPolicy.DelayForAttempt(int) -> TimeSpan`, sequence 1, 2, 5, 10, 30 seconds capped at 30.

- [ ] Write tests for CONNECT 200/204 success, 407 authentication failure, 403 failure, malformed response, and retry cap/reset behavior.
- [ ] Verify red tests.
- [ ] Implement parser and retry policy.
- [ ] Run all tests.

### Task 5: Windows runtime adapters and configuration storage

**Files:**
- Create: `src/CodexSshSetup.App/CodexSshSetup.App.csproj`
- Create: `src/CodexSshSetup.App/Infrastructure/ProcessRunner.cs`
- Create: `src/CodexSshSetup.App/Infrastructure/AppConfigStore.cs`
- Create: `src/CodexSshSetup.App/Infrastructure/FileLog.cs`
- Create: `src/CodexSshSetup.App/Models/AppConfig.cs`
- Create: `tests/CodexSshSetup.Core.Tests/AppConfigSerializationTests.cs`

**Interfaces:**
- Produces: `IProcessRunner.RunAsync(CommandSpec, IProgress<string>?, CancellationToken) -> ProcessResult`.
- Produces: JSON persistence below `%LOCALAPPDATA%\CodexSshSetup` containing only profiles, proxy port, and close preference.
- Produces: rolling sanitized logs, 2 MiB per file and seven files.

- [ ] Write serialization tests proving secret-shaped properties are absent and profiles round-trip.
- [ ] Verify red tests.
- [ ] Implement hidden-window process execution, timeout/cancellation, output capture, and optional visible interactive mode for the first password prompt.
- [ ] Implement atomic JSON persistence and sanitized rolling logs.
- [ ] Run all tests and build the app project.

### Task 6: Provisioning, proxy detection, and remote Codex orchestration

**Files:**
- Create: `src/CodexSshSetup.App/Services/SshProvisioningService.cs`
- Create: `src/CodexSshSetup.App/Services/ProxyDetectionService.cs`
- Create: `src/CodexSshSetup.App/Services/CodexRemoteService.cs`
- Create: `src/CodexSshSetup.App/Services/SetupOrchestrator.cs`
- Create: `src/CodexSshSetup.App/Models/ComponentStatus.cs`

**Interfaces:**
- Produces async operations for port test, key provisioning, SSH config update, passwordless test, local npm pack, upload, user-prefix install, wrapper install, version verification, PID/port discovery, live CONNECT probe, login status, and device auth.
- Emits `ComponentStatus(Name, State, Summary, Detail)` and progress log events.

- [ ] Add contract tests around command plans and state transitions using a scripted fake process runner.
- [ ] Verify red tests.
- [ ] Implement orchestration with per-step cancellation and readable mappings for refused, permission denied, missing CLI, forwarding failure, timeout, and proxy 407.
- [ ] Ensure temp npm package directories are deleted in `finally` and no credential text is persisted.
- [ ] Run all tests.

### Task 7: Owned tunnel supervisor with automatic reconnect

**Files:**
- Create: `src/CodexSshSetup.App/Services/TunnelSupervisor.cs`
- Create: `tests/CodexSshSetup.Core.Tests/TunnelStateMachineTests.cs`

**Interfaces:**
- Produces: idempotent `ConnectAsync`, `DisconnectAsync`, current state, and status events.
- Consumes only the process instance it created; never scans and kills arbitrary SSH processes.

- [ ] Write state-machine tests for duplicate connect, user disconnect suppressing retry, unexpected exit scheduling retry, delay reset after a stable connection, and disposal terminating only the owned handle.
- [ ] Verify red tests.
- [ ] Implement the supervisor around an injected process-session factory and `RetryPolicy`.
- [ ] Run all tests.

### Task 8: WPF first-run wizard and daily dashboard

**Files:**
- Create: `src/CodexSshSetup.App/App.xaml`
- Create: `src/CodexSshSetup.App/App.xaml.cs`
- Create: `src/CodexSshSetup.App/MainWindow.xaml`
- Create: `src/CodexSshSetup.App/MainWindow.xaml.cs`
- Create: `src/CodexSshSetup.App/ViewModels/MainViewModel.cs`
- Create: `src/CodexSshSetup.App/ViewModels/AsyncCommand.cs`
- Create: `src/CodexSshSetup.App/Views/LogWindow.xaml`
- Create: `src/CodexSshSetup.App/Views/LogWindow.xaml.cs`

**Interfaces:**
- Presents setup fields; VPN, SSH, Tunnel, Codex CLI, and Login statuses; configure/connect/disconnect/redetect/login/log actions.
- Keeps long work off the UI thread and supports cancellation.

- [ ] Add view-model tests for validation, busy-state command disabling, status updates, and cancellation.
- [ ] Verify red tests.
- [ ] Implement a two-tab Chinese UI with accessible labels, compact status cards, progress output, and copyable login URL/code.
- [ ] Add close behavior that asks whether to hide/keep the tunnel or stop/exit.
- [ ] Run tests and launch a smoke instance.

### Task 9: Tray lifecycle, packaging, README, and final verification

**Files:**
- Create: `src/CodexSshSetup.App/Services/TrayIconService.cs`
- Create: `README.md`
- Create: `scripts/publish.ps1`
- Create: `.gitignore`
- Create: `outputs/CodexSshSetup-portable-win-x64.zip`
- Create: `outputs/CodexSshSetup-source.zip`

**Interfaces:**
- Tray actions: show, connect, disconnect, exit.
- Publish script builds Release, runs tests, creates framework-dependent and self-contained portable folders, then zips user-facing deliverables.

- [ ] Add a lifecycle test proving tray hide does not stop the tunnel and explicit exit does.
- [ ] Verify red test, implement tray behavior, and run all tests.
- [ ] Write README covering prerequisites, first-run flow, daily flow, security boundaries, known server-policy failures, Desktop wrapper compatibility, and log paths.
- [ ] Run `dotnet test`, Release build, framework-dependent publish, and self-contained publish; inspect exit codes and warnings.
- [ ] Launch the published EXE for a no-server smoke test and capture whether it remains running without a console window.
- [ ] Zip binaries and source into `outputs`, excluding keys, configs, logs, `bin`, `obj`, `.git`, and temporary npm packages.
- [ ] Compare the final implementation to every design requirement and report any real-server tests that remain manual.
