# QingTab 0.2.6 Lite 完整内存基准 B

日期：2026-08-13（Asia/Hong_Kong）

场景 B：**正式 Release EXE、主功能逻辑启用、无打开文件夹请求、托盘空闲驻留**。

## 结论

QingTab 0.2.6 在 5 轮独立进程启动后 `T+30 s` 的中位数为：

| 指标 | 中位数 | 5 轮范围 | 含义 |
|---|---:|---:|---|
| 总工作集 | **44.39 MiB** | 44.33–44.66 MiB | 当前驻留在物理内存中的全部页面，包含共享 DLL/.NET/WinForms 页面 |
| 私有工作集 | **9.70 MiB** | 9.62–11.48 MiB | 当前驻留且仅归 QingTab 使用的物理页面 |
| 私有提交（Private Bytes） | **26.53 MiB** | 26.41–28.27 MiB | 进程独占的已提交虚拟内存，不等于全部都驻留在 RAM |
| 峰值总工作集 | **44.64 MiB** | 44.58–44.91 MiB | 每轮从启动到 T+30 s 的峰值 |
| 句柄 | **354** | 354–359 | Windows 内核对象句柄 |
| 线程 | **13** | 12–14 | T+30 s 活跃线程数 |
| GDI / USER 对象 | **13 / 11** | 每轮一致 | 托盘 WinForms GUI 资源 |

180 秒空闲驻留段从 `T+30 s` 到 `T+180 s`：

| 指标 | T+30 s | T+60 s | T+120 s | T+180 s | 30→180 s |
|---|---:|---:|---:|---:|---:|
| 总工作集 | 44.434 MiB | 44.395 MiB | 44.375 MiB | **44.336 MiB** | −0.098 MiB |
| 私有工作集 | 9.723 MiB | 9.691 MiB | 9.672 MiB | **9.625 MiB** | −0.098 MiB |
| 私有提交 | 26.523 MiB | 26.441 MiB | 26.406 MiB | **26.320 MiB** | −0.203 MiB |
| 句柄 | 354 | 354 | 352 | **346** | −8 |
| 线程 | 14 | 12 | 11 | **8** | −6 |
| 累计 CPU 时间 | 421.875 ms | 421.875 ms | 421.875 ms | **421.875 ms** | 无可测增长 |
| GDI / USER 对象 | 13 / 11 | 13 / 11 | 13 / 11 | **13 / 11** | 不变 |

这段 150 秒观察窗口内没有出现内存、句柄、线程或 GUI 对象持续上升；它支持“空闲驻留稳定”，但时间仍不足以单独证明长期绝无泄漏。

## 启动与就绪

5 轮从进程创建到 QingTab Ready 事件置位：

`3209 / 530 / 1149 / 609 / 485 ms`

- 中位数：**609 ms**；
- 范围：**485–3209 ms**；
- 第一轮包含系统文件页与程序集冷缓存影响；后四轮明显稳定；
- Ready 表示 Explorer/Shell 连接进入可服务状态，不等于目标文件列表已经渲染完成。

## 测试对象

- EXE：`QingTab.exe`，FileVersion `0.2.6.0`；
- 大小：`116,736 B`；
- SHA-256：`29D738C45FE50B2E4F6EF60C574D4C060572D46582BDC223739BAC65F2D43F61`；
- 启动参数：`--startup --portable --no-registration-repair --test-enable-direct-open`；
- 参数组合的作用：正式 Release 二进制按“主功能已启用”连接 Shell，同时跳过开机启动和 Folder 注册修复；
- 未发送任何 `--open-tab` 请求，未创建或切换 Explorer 标签。

## 测试环境

| 项目 | 值 |
|---|---|
| 操作系统 | Windows 11 专业版 64 位，Build 26200 |
| Explorer | 10.0.26100.8457 |
| CPU | AMD Ryzen 7 5800H，8 核 16 线程 |
| 内存 | 31.9 GiB |
| 机型 | Lenovo 82JQ |
| .NET Framework Release | 533509 |
| 电源方案 | 野兽模式 |

## 方法

1. 只读记录当前 0.2.5 驻留进程、HKCU Run、Folder open 与 QingTab 所有权状态；
2. 通过 QingTab 自己的会话级 Exit 事件让 0.2.5 正常退出，不执行 `--exit`，因此不移除 Folder 注册；
3. 验证 0.2.6 正式 EXE SHA-256；
4. 连续进行 5 次独立进程启动，每轮运行 30 秒；
5. 第 6 次启动连续空闲 180 秒，保留 30、60、120、180 秒稳定点；
6. 采集 `System.Diagnostics.Process`、Windows Process 性能计数器与 `GetGuiResources` 数据；
7. 结束 0.2.6 后对注册快照逐字段比较；
8. 按原路径与原参数重启 0.2.5，并再次核对进程和注册状态。

测试完成状态：

- `Completed = true`；
- `RegistryUnchanged = true`；
- `OriginalResidentRestored = true`。

## 数据口径与限制

- 文件和内存均使用二进制单位 MiB（`1 MiB = 1,048,576 B`）；
- 宣传图应优先写清指标名，不能只写一个含义模糊的“内存占用”；
- Windows 的 `.NET CLR Memory` 性能计数器集在本机不可用，因此本次没有单列托管堆；OS 级工作集和私有提交仍覆盖进程的实际总体资源；
- 性能计数器实例首次发现较慢，CSV 中目标 `T+1/T+5` 的部分样本实际采于 11–24 秒，未用于结论；所有 `T+30`、`T+60`、`T+120`、`T+180` 样本均按 `ActualSeconds` 核对后纳入；
- 本测试是空闲驻留基准，不是连续打开目录后的压力内存基准；后者会操作 Explorer，不在本轮执行；
- 3 分钟无上升趋势不能代替数小时或数天泄漏测试。

## 可复核文件

- `artifacts/memory-benchmark-0.2.6-20260813/memory-samples.csv`
- `artifacts/memory-benchmark-0.2.6-20260813/run-summary.csv`
- `artifacts/memory-benchmark-0.2.6-20260813/benchmark-status.json`
- `artifacts/memory-benchmark-0.2.6-20260813/registry-before.json`
- `artifacts/memory-benchmark-0.2.6-20260813/registry-after.json`
- `artifacts/memory-benchmark-0.2.6-20260813/benchmark-progress.log`
- `artifacts/memory-benchmark-0.2.6-20260813/Invoke-QingTab026MemoryBenchmark.ps1`
