# 启动与运行时性能结构

实测环境：VS 2022 (17.14) + ProjectLungfish（UE 大解决方案），2026-08-29 用 `devenv /log` + ActivityLog 解析（277s 跨度、4429 条 entry）。

## 启动期成本分布（实测）

| 项 | 量级 | 备注 |
|---|---|---|
| P4VsProvider 包加载 | 0.3s | ActivityLog 实测，包初始化很轻 |
| CodeLensOopProviderVsix（Blame 所在包）加载 | <0.1s | BackgroundLoad，不在 top 列表 |
| Blame margin 挂载 | 毫秒级/视图 | 折叠态零数据请求 |
| **PkgDef 缓存全量重建** | 数十秒，一次性 | **每次安装/升级扩展后的第一次启动必触发**（日志特征：`PkgDef cache fast check ... not current ... cache will be regenerated`）。同一天反复重装 = 每次都慢，属安装机制不是插件开销 |
| UE 解决方案加载 | 分钟级 | 与插件无关的固有大头（VC 项目系统扫描） |

结论：插件自身在 devenv 进程内的启动开销可忽略；体感"装完插件启动卡"主要来自 ① PkgDef 重建（每次安装一次）② 下面两个**网络往返**源。

## 插件的两个 P4 网络往返源（ recurring ）

### 1. 主包文件状态缓存（P4ScmProvider）

- Solution Explorer 的 Scc 图标触发 `GetFileStatus(file)` → `Fetch(file)`
- `TreatProjectsAsFolders=true`（默认）时按**目录**批量缓存：目录未缓存则 `AddDirectoryFilesToCache(dir)`（目录级 fstat 往返）
- **`LazyLoadStatus` 偏好**：true = 未缓存文件直接返回 Unknown、不主动取。官方默认值在源码里就不一致（`P4VsProviderService` 三处默认 true，其余 false）。**本 fork 已统一翻成 true**（全部 `GetBool("LazyLoadStatus", ...)` 读取点 + 选项页复选框默认值）
- 相关同伴偏好：`LazyLoadFullMenu`（菜单懒加载，与 LazyLoadStatus 共同决定）、`AutoUpdateFileData`

### 2. Helix CodeLens provider（ServiceHub OOP 进程）

- `CanCreateDataPointAsync` **每个代码元素描述符**都调 `HelixUtility.GetRepository()`（内部含 `GetLastCommit`）
- 数据有按文件缓存：`changelistCache`（file → 最近 5 个 CL），所以每个打开的文件启动期一次 `p4 changes -m5`，之后命中缓存
- 连接共享：读主包写的 memory-mapped file `connection-info-{devenvPid}`（读完即清，之后走静态 `repository` 缓存），拿不到且没有缓存连接时 `GetRepository` 返回 null → provider 静默不出数据
- ⚠️ 该 provider 出错时弹 `MessageBox.Show`（OOP 进程弹窗！），是它的官方实现习惯
- ActivityLog 看不到这些（发生在 ServiceHub.Host.CLR 进程，不在 devenv 日志里）

## 测量方法

- `devenv /log`（**单独一个参数，后面别跟文件路径**）→ ActivityLog.xml（UTF-16）→ 解析 `Begin/End package load [X]` 成对时间差 + 相邻 entry 大 gap
- 包加载耗时不背锅时，瓶颈在网络往返/解决方案加载，用源码侧缓存结构分析定位
