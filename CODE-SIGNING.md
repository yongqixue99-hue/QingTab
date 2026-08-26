# QingTab 正式代码签名说明

## 签名是什么

代码签名不是写进 QingTab 运行逻辑的一段代码。它是在 Release 构建完成后，使用发布者证书的私钥给 `QingTab.exe` 添加 Authenticode 数字签名：

- Windows 可以显示经过验证的发布者名称；
- 文件被改动后，签名会失效；
- RFC 3161 可信时间戳证明文件是在证书有效期内签署的，使签名在发布者证书以后正常到期后仍可验证。

签名不会增加 QingTab 的常驻内存，也不会增加后台进程。它只会让 EXE 增加少量证书和签名数据。

## 当前已实现的发布保护

`build-release.ps1 -Sign` 会在打包 ZIP 之前执行以下步骤：

1. Release 构建；
2. 精确执行 237 项行为检查和 23 项生命周期检查；
3. 使用 SHA-256 Authenticode 签署便携包中的 `QingTab.exe`；
4. 请求 DigiCert RFC 3161 / SHA-256 时间戳；
5. 使用 Windows 默认 Authenticode 策略验证签名、信任链和时间戳；
6. 核对 Code Signing 和 Time Stamping EKU；
7. 可同时固定预期发布者名称和证书指纹，避免误用另一张证书；
8. 只有验证成功才继续生成 ZIP、SHA-256、机器可读 Manifest 和 Release 报告。

未提供证书时，`-Sign` 会失败，不会把未签名文件标记成正式签名包。

## 还需要准备什么

真正显示“已验证的发布者”，必须先从受 Windows 信任的证书颁发机构申请代码签名证书，或者选择 Microsoft Store 让商店为 MSIX 签名。自签名测试证书不适合面向普通用户发布。

QingTab 保持完整 MIT 开源时，还可以申请 SignPath Foundation 的免费开源项目签名。该方案的证书发布者会显示为 `SignPath Foundation`，需要项目已经公开发布、使用公开源码仓库、保留完整开源许可、配置可验证的自动构建，并由维护者人工批准每次正式签名。它适合先建立可信分发；若希望 Windows 显示你本人或公司的名称，则应购买以相应个人/企业身份核验的公开信任证书。

公开信任代码签名证书的私钥通常位于硬件令牌、HSM 或云签名服务中。若证书供应商只提供硬件令牌而不允许导出 PFX，请在受控 Windows 发布机上安装供应商客户端，然后使用证书指纹模式，不要把硬件私钥上传到 GitHub。

## 本地发布机：证书存储区模式（推荐硬件令牌/HSM）

确认代码签名证书已经出现在当前用户或本机的 `个人/My` 证书存储区后：

```powershell
$env:QINGTAB_SIGNING_CERTIFICATE_THUMBPRINT = '证书的40位SHA-1指纹'
$env:QINGTAB_EXPECTED_SIGNER_THUMBPRINT = '同一张证书的40位SHA-1指纹'
$env:QINGTAB_EXPECTED_SIGNER_SUBJECT = '证书中显示的发布者名称'
.\build-release.ps1 -Version 0.2.7 -OutputRoot .\release-output -Sign
```

硬件令牌可能在签名时要求输入 PIN，这是正常现象。不要把 PIN 写进脚本或仓库。

## 本地发布机：PFX 模式（仅在证书允许安全导出时）

```powershell
$env:QINGTAB_SIGNING_CERTIFICATE_PATH = 'D:\secure\QingTab-Code-Signing.pfx'
$env:QINGTAB_SIGNING_CERTIFICATE_PASSWORD = 'PFX密码'
$env:QINGTAB_EXPECTED_SIGNER_THUMBPRINT = '证书的40位SHA-1指纹'
$env:QINGTAB_EXPECTED_SIGNER_SUBJECT = '证书中显示的发布者名称'
.\build-release.ps1 -Version 0.2.7 -OutputRoot .\release-output -Sign
```

PFX 和密码不能提交到 Git、源码 ZIP、聊天记录或普通网盘。本仓库已忽略 `*.pfx`、`*.p12` 等私钥文件，但仍应把证书保存在工作区之外。

## GitHub Actions：SignPath Foundation（推荐）

`.github/workflows/release.yml` 已改为 SignPath 官方 GitHub 集成，不再要求或接收 PFX。流程是：

1. 在同一个 GitHub Job 中构建未签名的 `QingTab.exe`，执行 237 + 23 项检查；
2. 使用固定到完整提交 SHA 的 `actions/upload-artifact` 上传这一份 EXE；
3. 使用固定到完整提交 SHA 的 `SignPath/github-action-submit-signing-request`，把该 GitHub Artifact ID 提交给 SignPath；
4. 等待 SignPath 审批、HSM 签名和可信时间戳；
5. 下载签名后的 EXE，再用 Windows SignTool 和 PowerShell 独立验签；
6. 重新执行全部门禁、源码 ZIP 独立重建、打包、Hash 和报告；
7. 最多只创建 GitHub 草稿 Release，不自动公开。

申请获批后，在 GitHub `code-signing` Environment 中按 SignPath 提供的信息配置：

- Secret：`SIGNPATH_API_TOKEN`；
- Variables：
  - `SIGNPATH_ORGANIZATION_ID`；
  - `SIGNPATH_PROJECT_SLUG`；
  - `SIGNPATH_SIGNING_POLICY_SLUG`；
  - `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`；
  - `QINGTAB_EXPECTED_SIGNER_SUBJECT`（通常为 `SignPath Foundation`，以获批信息为准）；
  - `QINGTAB_EXPECTED_SIGNER_THUMBPRINT`（可选；证书轮换时需要同步更新）；
  - `QINGTAB_TIMESTAMP_URL`（仅供本地验签脚本参数校验，通常可留空）。

建议给 `code-signing` Environment 配置 Required reviewers。不要在 GitHub 中创建 `QINGTAB_SIGNING_CERTIFICATE_BASE64`，SignPath Foundation 的私钥始终留在其 HSM 中。

工作流只有在上述 SignPath 参数获批并配置完成后才能真正签名；当前源码中的接入结构不等于文件已经获得签名。

## 独立验证

```powershell
.\scripts\Sign-QingTab.ps1 `
    -Path .\release-output\QingTab-v0.2.7-portable\QingTab.exe `
    -VerifyOnly `
    -ExpectedSignerSubject '发布者名称' `
    -ExpectedSignerThumbprint '40位证书指纹'
```

也可在资源管理器中右键 EXE，打开“属性 → 数字签名”查看发布者与时间戳。

## 重要限制

- 有效代码签名能证明发布者身份和文件完整性，但不能保证 SmartScreen 首次就完全不提醒；信誉通常还需要真实下载与使用积累。
- ZIP 文件本身不使用 Authenticode；应验证 ZIP 的 SHA-256，并验证 ZIP 内 EXE 的数字签名。
- 每次修改 EXE 都会破坏原签名，必须重新签名和重新生成 Hash。
