# 轻页 QingTab 0.2.6 Lite 发布候选验证记录

状态：后台轻量化、隔离封包和用户日常操作验收均已完成；当前机器默认仍运行 0.2.5 Lite RC2，0.2.6 尚未替换正式注册。

## 目标

0.2.6 不再尝试用遮罩或额外标签切换隐藏 Explorer 自己的默认页。它保持 0.2.5 Lite 的用户行为和 `ExplorerWatcher` 外部接口不变，只缩小正式实现与依赖面：响应优先路径属于产品；已撤回的遮罩、UIA、双身份和后台恢复属于隔离研究。

## 第一阶段改动

- 产品版本元数据更新为 `0.2.6 / 0.2.6.0`。
- `ExplorerWatcher` 的生产入口固定调用 `OpenPathInResponsiveNewTabAsync`，不再运行视觉模式选择或预热。
- 正式项目排除 `ExplorerVisualMask*`、`ExplorerTabSelectionSnapshot`、`ExplorerTabActivationLease`、`ExplorerTabDualIdentity*`、`ExplorerNativeTabSnapshotCapture`、`ExplorerTabNativeOwnershipLease` 与 `ExplorerOpenExperiencePolicy`。
- 跨进程导航的 `ExplorerNavigationDisposition` 与回退策略移到独立生产模块，继续保护“结果未知时不重复开窗、不误关可能已接受的标签”。
- 研究文件由 `QingTab.Tests` 链接编译，历史策略检查仍可运行，但不再增加驻留 EXE 的程序集、类型或资源。
- 请求队列删除视觉流程专用的 `HasKnownDuplicateRequest` 信号、outstanding 字典和完成回调；正式接口只保留 `300 ms` 去抖、有界 FIFO 与串行出队。超过去抖窗口的明确再次点击仍会被保留。

## 后台验证

- QingTab Release：`0` 警告、`0` 错误。
- 行为检查：单次 `PASS: 206`；随后连续 5 轮均为 `PASS: 206`。
- 第一次全量运行曾有一项 IPC 毫秒上限检查在并行构建负载下失败；未修改代码原样复跑即通过，之后连续 5 轮无复现。它被记录为测试环境计时波动，不冒充产品缺陷已修复。
- 0.2.5 RC2 EXE：`204800` 字节。
- 0.2.6 第一阶段 EXE：`116736` 字节。
- 减少：`88064` 字节，约 `43%`。
- 0.2.6 第一阶段 EXE SHA-256：`29D738C45FE50B2E4F6EF60C574D4C060572D46582BDC223739BAC65F2D43F61`（后续源码改动与重建会改变，不能作为发布哈希）。
- 最终程序集引用：`Microsoft.CSharp, mscorlib, System, System.Core, System.Drawing, System.Windows.Forms`。
- `UIAutomationClient / UIAutomationTypes / WindowsBase`：均不存在。
- 内部便携包与源码包烟雾封包成功；源码包包含此前遗漏的 `Models` 目录以及 0.2.6 专属测试报告。
- 从源码包的独立目录显式构建产品与 `QingTab.Tests` 均为 `0` 警告、`0` 错误，测试为 `PASS: 206`。不同绝对目录下重建的 EXE 大小与版本一致，但 SHA-256 不同，因此当前只承诺确定性编译设置，不宣称跨路径逐字节可复现。
- 解决方案现已纳入 `QingTab.Tests` 项目，后续从源码包直接构建解决方案不会再漏掉测试项目。

## 用户验收与已知限制

- 2026-08-13 用户完成 0.2.6 日常操作测试，结论为“提升不明显，但当前没有发现明显功能错误，整体情况与 0.2.4 相同”。这与既有五轮 A/B 一致：0.2.5 Lite 与 0.2.4 的目标匹配中位数约为 `1718 / 1661 ms`，差异小于 Explorer 自身波动。
- 高标签数或连续打开时，活动新标签仍可能先短暂显示 Explorer 的“此电脑”默认页，再切到目标目录。既有样本中默认页可见中位数约为 `1.2–1.3 秒`；这是 Windows 先创建空标签、随后才向 Shell 暴露可导航对象的过渡，不是 0.2.6 新增回归。
- 已验证的遮罩与“恢复旧标签后后台导航”方案虽然能隐藏默认页，但前者让等待和失败分支更明显，后者在高标签数样本中增加两次切换并达到约 `4.66 秒`，用户体验均差于直接路径，因此不恢复。
- 唯一仍值得独立研究的方向是 site-aware `opennewtab` 一步调用，但它依赖 Explorer 未承诺的宿主行为，并存在同步 Shell/COM 调用卡住的风险。该实验不进入 0.2.6，也不阻塞发布。

## 当前系统状态

- 唯一 QingTab 驻留为用户当时安装的 `QingTab-v0.2.5-lite-rc2-portable\QingTab.exe`，版本 `0.2.5.0`；公开报告不记录本机绝对路径。
- `HKCU Run`、Folder 默认打开命令与所有权记录均指向该 0.2.5 RC2。
- 本阶段没有启动 0.2.6、没有打开或导航 Explorer、没有抢占鼠标键盘。

## 发布结论

- 直接路径仍保留导航结果分类、Shell 重连、操作生命周期、300 ms 去抖、有界 FIFO 和安全退出恢复；静态程序集审计未发现被撤回的视觉类型或 UIAutomation 引用。
- 用户验收没有发现新的功能错误；已知的 Explorer 默认页过渡已写入说明，不再宣传成“真正零闪烁”。
- 建议以 `0.2.6 Lite` 发布。后续若继续研究一步建标签，应作为独立实验版本，不回灌到稳定分支，除非多标签、焦点切换、Explorer 重启和阻塞恢复均得到重复实机验证。
