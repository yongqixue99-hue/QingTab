# 轻页 QingTab

Windows 11 的文件资源管理器已经有标签页了，但文件夹选项仍然只有两种：要么在当前标签里跳走，要么再开一扇窗口。

我想保留当前标签，又不想让窗口越开越多，于是做了轻页。它常驻在托盘里。普通打开文件夹时，目标文件夹会进入现有资源管理器的新标签。

轻页只改普通打开。Win + E、任务栏上的资源管理器、右键“在新窗口中打开”和“在新标签页中打开”都照旧。

当前候选版本：`0.2.7 Lite`

## 实际效果

下面三段都是 Windows 11 实机录屏。画面只做了裁切和侧栏遮挡，窗口、标签和加载过程都是真的。

### 没有开启轻页

普通打开文件夹后，Windows 又弹出一扇资源管理器窗口。连续打开几个文件夹，任务栏和桌面很快就会堆满窗口。

![未使用轻页时弹出新的资源管理器窗口](https://raw.githubusercontent.com/yongqixue99-hue/QingTab/main/media/demos/without-qingtab-new-window.gif)

### 开启轻页

同样的操作会在当前资源管理器里增加一个标签。

![普通打开文件夹进入新标签](https://raw.githubusercontent.com/yongqixue99-hue/QingTab/main/media/demos/open-folder-new-tab.gif)

连续打开三个文件夹，标签从一个增加到四个，期间没有先创建一扇用于中转的新窗口。

![从一个标签连续增加到四个标签](https://raw.githubusercontent.com/yongqixue99-hue/QingTab/main/media/demos/one-to-four-tabs.gif)

## 当前功能和轻量表现

轻页的功能不多，所有功能都围绕一件事：让 Windows 11 的文件夹尽量留在同一个资源管理器窗口里。

- 普通打开文件夹时，在现有窗口中增加一个新标签；
- 连续打开多个文件夹时，按顺序增加标签，不会同时堆出多扇窗口；
- Win + E、任务栏资源管理器和右键“在新窗口中打开”保持原样；
- 托盘里可以随时开关接管，并可选择开机自动启动；
- 关闭功能或退出程序时，先恢复 Windows 原来的文件夹打开方式；
- Explorer 暂时繁忙或标签创建失败时，自动回到 Windows 正常开窗；
- 回收站、控制面板、库等特殊系统位置保持 Windows 原生打开；
- 不需要管理员权限，解压后直接运行。

### 程序有多小

| 项目 | 实测数据 | 说明 |
|---|---:|---|
| `QingTab.exe` | **125440 字节，约 0.12 MB** | 0.2.7 主程序本体 |
| 当前便携 ZIP | **约 0.09 MB** | 下载文件大小 |
| 当前便携包解压后 | **约 0.15 MB** | 包含程序、说明、许可、校验值和卸载脚本 |
| 程序独占的常驻物理内存 | **中位数约 9.70 MB** | 这部分内存只归轻页使用 |
| 总工作集 | **中位数约 44.39 MB** | 包含 Windows、.NET 和 WinForms 的共享页面，不全是轻页独占 |
| 启动到可服务状态 | **中位数 609 ms** | 5 轮独立启动，范围 485–3209 ms，第一轮包含冷缓存 |
| 空闲驻留稳定性 | **未发现持续上升** | 3 分钟观察中，内存、句柄、线程和界面对象均未持续增长 |
| 行为检查 | **237 项全部通过** | 包含特殊位置分流、WSL、休眠重连、IPC 和退出检查 |

主程序约 `0.12 MB`，既有基准中程序独占的常驻物理内存约 `9.7 MB`。轻页没有服务、驱动、Explorer 注入、全局键鼠钩子、遥测和自动更新，常驻时只保留托盘与打开新标签需要的部分。

Explorer 创建标签后，还要加载文件列表、图标和 Shell 扩展。标签很多或连续打开文件夹时，可能短暂出现“此电脑”。之前试过用遮罩盖住这段过渡，实机等待更明显，所以当前版本保留了 Explorer 自己的加载过程。

完整测试口径见 [`MEMORY-BENCHMARK-0.2.6-B-2026-08-13.md`](MEMORY-BENCHMARK-0.2.6-B-2026-08-13.md)。内存和启动数据来自 0.2.6 基准；0.2.7 没有加入新的常驻服务、UI 自动化或遮罩模块。不同电脑的实际数据会有波动。

## 哪些操作会进入新标签

- 在资源管理器内容区普通双击文件夹；
- 从桌面或其他程序普通打开文件夹；
- 打开 `E:\` 这类磁盘根目录；
- 已经有资源管理器窗口时，目标会进入其中的新标签。

下面这些操作保持 Windows 原样：

- Win + E、任务栏资源管理器和直接运行 `explorer.exe`；
- 右键“在新窗口中打开”；
- 右键“在新标签页中打开”；
- 左侧导航栏的左键和中键；
- 回收站、控制面板、库等特殊系统位置；
- 当前没有任何资源管理器窗口时，先正常打开第一扇窗口。

## 文件夹选项

如果希望在资源管理器内容区双击文件夹时进入新标签，需要使用这项 Windows 设置：

`文件夹选项 → 常规 → 浏览文件夹 → 在不同窗口中打开不同的文件夹`

选“同一窗口”时，Explorer 会直接让当前标签跳到另一个文件夹，轻页收不到新的打开请求。选“不同窗口”后，轻页可以在 Windows 创建窗口之前接住请求，再把它送进新标签。

这项设置不会影响右键“在新窗口中打开”。用户明确选择新窗口时，轻页不会接管。

## 安装

轻页是便携程序，不需要管理员权限。

1. 解压完整 ZIP 到一个不会随意移动的目录；
2. 旧版正在运行时，先从托盘退出旧版；
3. 运行 `QingTab.exe`；
4. 首次运行时选择是否开启“普通打开文件夹 → 新标签”；
5. 需要开机启动时，再勾选托盘里的“开机自动启动”。

升级时建议把新版解压到新的稳定目录。旧版退出后再运行新版，不要直接覆盖仍在运行的程序文件。

## 托盘菜单

托盘右键菜单里可以开关普通文件夹接管和开机启动，也可以复制诊断信息、查看说明与许可，或者退出轻页。

关闭“普通打开文件夹 → 新标签”后，轻页会恢复 Windows 原来的打开方式，并释放与 Explorer 的连接。退出程序时也会先做同样的恢复。开机启动是单独的选项，退出程序不会自动取消下次登录启动。

复制诊断信息时，只会记录请求耗时、结果、版本、内存和句柄数。文件夹只记录“本地文件夹、磁盘根目录、网络文件夹、Shell 位置”等类别，不复制完整路径。

## 打不开时会怎样

Explorer 暂时繁忙、标签创建失败或导航失败时，轻页会调用 Windows 自己的资源管理器打开目标文件夹。回到原生开窗后可能出现一扇新窗口，但文件夹仍然能打开。

关闭功能、退出或卸载时，轻页只会清理由自己写入的文件夹打开命令。如果文件夹打开命令后来被其他程序修改，轻页会停止清理并提示，不会覆盖其他程序的设置。

## 轻量与隐私

- 不注入 Explorer；
- 不安装服务、驱动或浏览器扩展；
- 不使用全局鼠标钩子或键盘钩子；
- 不联网，没有遥测和自动更新；
- 只在当前用户注册表中写入文件夹打开命令和开机启动项；
- 最近 20 次诊断只保存在内存，默认不记录完整文件夹路径；
- 错误日志不写文件夹路径、异常消息和堆栈，每个文件最多 `256 KiB`，只保留当前文件和两个归档。

完整说明见 [`PRIVACY.md`](PRIVACY.md)。

## 卸载

1. 在托盘中取消“普通打开文件夹 → 新标签”；
2. 取消“开机自动启动”；
3. 退出轻页；
4. 运行随包附带的 `卸载轻页.cmd`；
5. 删除程序目录。

卸载脚本发现文件夹打开方式已被其他程序修改时会停止清理，并保留现场。

## 构建

构建机需要 Windows 和 .NET SDK 6 或更高版本。程序目标框架为 `.NET Framework 4.8.1`，支持 Windows 11 22H2（Build 22621）或更高版本。

```powershell
.\build-release.ps1 -Version 0.2.7
```

正式发行流程支持 SHA-256 Authenticode、RFC 3161 可信时间戳、签名后验证和证书指纹固定。

```powershell
.\build-release.ps1 -Version 0.2.7 -OutputRoot .\release-output -Sign
```

证书私钥不会写入源码。证书存储区/PFX 配置、GitHub `code-signing` Environment 和验证方法见 [`CODE-SIGNING.md`](CODE-SIGNING.md)。没有受信任代码签名证书时，普通构建只能用于开发验证，不能宣传为“已验证发布者”。

## Code signing policy

QingTab 已申请加入 SignPath Foundation 的开源代码签名计划。在申请获批并完成可验证构建接入前，QingTab 发布文件**不应被视为已由 SignPath Foundation 签名**。

Upon approval: Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

- Authors and committers: [@yongqixue99-hue](https://github.com/yongqixue99-hue)
- Reviewers: [@yongqixue99-hue](https://github.com/yongqixue99-hue)
- Approvers: [@yongqixue99-hue](https://github.com/yongqixue99-hue)

每次正式签名请求都需要 Approver 人工批准。构建、检查、签名后验证、打包、Hash 和报告流程见 [`RELEASE-PROCESS.md`](RELEASE-PROCESS.md) 与 [`CODE-SIGNING.md`](CODE-SIGNING.md)。

Privacy: This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. See [`PRIVACY.md`](PRIVACY.md).
