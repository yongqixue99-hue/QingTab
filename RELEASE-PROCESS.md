# QingTab 正式发布流程

## 已自动化的发布门禁

`build-release.ps1` 现在按固定顺序执行：

1. Release 构建，任何警告或错误由构建日志保留。
2. 精确核对 `PASS: 206 QingTab behavior checks`。
3. 核对退出、会话隔离、重复释放和 Explorer 重连的 23 项生命周期检查。
4. 将 `QingTab.exe` 进行 SHA-256 Authenticode 正式签名。
5. 使用 RFC 3161 服务写入 SHA-256 可信时间戳。
6. 用 SignTool 和 PowerShell 双重验签，缺签名、签名无效或缺时间戳都会停止发布。
7. 生成便携包和源码包以及各自的 SHA-256。
8. 解压源码 ZIP，在独立目录重新构建并再次执行 206 + 23 项检查。
9. 生成 Markdown Release 报告和机器可读 JSON manifest。

GitHub 工作流位于：

- `.github/workflows/ci.yml`：每次提交和拉取请求执行构建、206 项检查、23 项生命周期检查和只读桌面审计。
- `.github/workflows/release.yml`：`v*` 标签触发正式签名 Release 流程，全部通过后只创建草稿 Release，不直接公开。

## 正式证书前置条件

正式签名不能用临时自签名证书冒充。需要把真实代码签名证书配置为 GitHub 仓库 Secrets：

- `QINGTAB_SIGNING_CERTIFICATE_BASE64`：PFX 文件的 Base64 内容。
- `QINGTAB_SIGNING_CERTIFICATE_PASSWORD`：PFX 密码。

可选仓库 Variables：

- `QINGTAB_EXPECTED_SIGNER_SUBJECT`：预期签名者名称；配置后会防止误用其他证书。
- `QINGTAB_TIMESTAMP_URL`：RFC 3161 时间戳地址；未配置时使用 `http://timestamp.digicert.com`。

本地也可以使用证书库的指纹：

```powershell
$env:QINGTAB_SIGNING_CERTIFICATE_THUMBPRINT = '<正式证书指纹>'
$env:QINGTAB_EXPECTED_SIGNER_SUBJECT = '<预期发布者名称>'
.\build-release.ps1 -Version 0.2.6 -OutputRoot .\release-output -Sign
```

没有正式证书时，签名流程会明确失败，不会生成看似正式、实际不可信的发布包。

## 生命周期测试分层

默认 CI 运行的 23 项检查不会关闭 Explorer、不会注销，也不会修改用户注册表。它覆盖：

- 重复和并发退出只恢复一次接管项。
- 恢复失败或异常时禁止退出。
- 新登录会话使用不同的互斥体、退出事件、就绪事件和 IPC 名称。
- Explorer 旧代际请求在重启时立即失效。
- 最后一个旧请求只允许一次共享资源释放。
- 重复完成、外来 ticket、重复 Dispose 都不能二次释放。
- 256 次重启/重新连接状态循环。

真实 Explorer 重启和真实注销需要在专用测试账户、保存全部工作后手工加开关运行：

```powershell
.\tests\Invoke-QingTabDesktopLifecycle.ps1 `
  -Scenario ExplorerRestart `
  -CandidateExe .\QingTab\bin\Release\net481\QingTab.exe `
  -AllowDesktopDisruption

.\tests\Invoke-QingTabDesktopLifecycle.ps1 `
  -Scenario Logoff `
  -Phase Prepare `
  -CandidateExe .\QingTab\bin\Release\net481\QingTab.exe `
  -AllowDesktopDisruption
```

脚本有以下安全闸门：

- 只接受 FileVersion `0.2.6.0` 的候选程序。
- 检测到任何正在运行的 QingTab 时拒绝继续，不会停止或替换用户当前驻留版本。
- 不写 Folder 接管项，不修改开机自启。
- 没有显式 `-AllowDesktopDisruption` 时拒绝重启 Explorer 或注销。
- 注销验证使用一次性 RunOnce 检查点，确认旧会话进程没有跨会话残留。

## GitHub Release 使用方法

推荐流程：

1. 先让 `QingTab CI` 全绿。
2. 确认正式证书 Secrets 已配置。
3. 创建并推送与项目版本一致的标签，例如 `v0.2.6`。
4. `Signed QingTab Release` 自动生成签名包、Hash 和报告。
5. 工作流创建草稿 Release；人工检查签名者、时间戳、Hash 和文案后再公开。

工作流不会覆盖已存在的同名 Release，也不会绕过缺证书或验签失败。
