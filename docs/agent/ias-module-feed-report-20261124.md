# W5 IAS 官方源下载、验签与部件存储

工作树 `D:\gitCode\HD2ModManager-ias-feed`，分支 `feat/ias-module-feed`，起点 `3eed986`。实现依据总规划 §0、§3。未安装工具，未提交、切分支或推送。

## 实现与范围

| 项目 | 状态 | 结果 |
| --- | --- | --- |
| 官方清单与验签 | done | 固定官方仓库地址与 RSA 公钥。验签后检查 schema，按版本和序号定位最新发行条目。非 200、超时、网络异常、外层格式、验签、schema 和字段错误各有枚举与中文提示。显式取消沿用 `CancellationToken`。 |
| 下载与续传 | done | 复用管理器根目录，按包 SHA-256 命名缓存。短流、取消或连接中断保留 `.part`，下次通过 Range 和 Content-Range 接续。服务器返回 200 时重新写入，超长和校验失败的包丢弃。 |
| 核心解压 | done | 整包大小和 SHA-256 校验通过后，逐个校验 `app/`、三个运行包与 `bundle.json`。拒绝越界、设备名、ADS、链接及重复条目。ReShade 和两项 DLSS 留在整包缓存，解压结果只含核心部件。 |
| 激活、查询和移除 | done | 校验后才发布暂存目录，激活读取 ArmorBase 的两个游戏哈希。目录在同卷移动，原子替换 `current.json` 提交状态，提交失败把目录移回暂存，随后清除指针临时文件。提交后清理旧版，清理错误通过 `CleanupWarning` 返回。移除仅清理 `components/` 和 `cache/`。 |
| 游戏版本闸与状态 | done | 版本闸同时检查 `bin/helldivers2.exe` 和 `data/game/game.dll`，返回期望及实际哈希。状态覆盖未安装、已装、可更新、游戏不匹配、忙碌和错误，已装且清单不可达时回落到本地版本。 |
| CLI | done | `hd2mod ias feed`、`hd2mod ias download --output <目录>`、`hd2mod ias status` 接入现有分派。下载命令会用 `--manager-root` 或既有根目录定位器确定缓存，输出目录须尚未存在。手工输出调用原子导出，不切换当前版本。 |
| IAS 部署、UI、角色导入 | not-applicable | 由其余任务负责。 |

### 新增文件

- `HD2ModCore/Ias/IasFeedClient.cs`、`IasPaths.cs`、`IasProgress.cs`、`IasComponentStore.cs`、`IasGameCompatibility.cs`、`IasStatusService.cs`
- `HD2ModCli/CliIasFeedCommands.cs`
- `HD2ModCore.Tests/IasFeedTests.cs`、`IasFeedFixture.cs`
- `docs/agent/ias-module-feed-report-20261124.md`

现有文件只在 `HD2ModCli/CliApplication.cs` 增加两行命令分派。测试向量包含 1.6.4 的原始签名和签名覆盖的元数据，供离线单测使用。

### 公共 API

以下成员位于 `HD2ModCore.Ias`。下列记录的位置参数也对应其公开属性。

```csharp
IasFeedClient(HttpClient httpClient)
static Uri IasFeedClient.FeedUri { get; }
Task<IasFeedResult> IasFeedClient.GetLatestAsync(
    IProgress<IasProgress>? progress = null, CancellationToken cancellationToken = default)
static IasFeedResult IasFeedClient.Verify(ReadOnlyMemory<byte> signedFeed)
record IasFeedResult(IasRelease? Latest, IasFeedFailure Failure, string Message)
enum IasFeedFailure { None, Timeout, HttpStatus, Network, InvalidEnvelope, InvalidSignature,
    InvalidSchema, InvalidPayload }
IasRelease // 由清单验证器生成，只读属性如下
    string Version, long Sequence, long MinimumSequence, string Notes,
    Uri ZipUrl, string ZipSha256, long ZipBytes, IReadOnlyList<IasReleaseFile> Files, Uri ReleaseUrl
record IasReleaseFile(string Path, long Bytes, string Sha256)

IasComponentStore(StoragePaths paths, HttpClient httpClient, string? gameRootDirectory = null)
IasProgress? IasComponentStore.CurrentProgress { get; }
Task<string> IasComponentStore.DownloadAsync(IasRelease release, IProgress<IasProgress>? progress = null,
    CancellationToken cancellationToken = default)
Task<string> IasComponentStore.ExtractCoreAsync(IasRelease release, string packagePath,
    IProgress<IasProgress>? progress = null, CancellationToken cancellationToken = default)
Task<string> IasComponentStore.ExportCoreAsync(IasRelease release, string packagePath, string outputDirectory,
    IProgress<IasProgress>? progress = null, CancellationToken cancellationToken = default)
Task<IasActivationResult> IasComponentStore.ActivateAsync(string version, IProgress<IasProgress>? progress = null,
    CancellationToken cancellationToken = default)
IasInstalled? IasComponentStore.GetInstalled()
Task IasComponentStore.RemoveAsync(CancellationToken cancellationToken = default)
record IasInstalled(string Version, string ComponentsDirectory)
record IasActivationResult(IasInstalled Installed, string? CleanupWarning = null)
record IasProgress(IasStage Stage, long CompletedBytes, long TotalBytes, string Message)
enum IasStage { FetchFeed, Download, Verify, Extract, Activate }

static IasGameCompatibilityResult IasGameCompatibility.Check(string? gameRootDirectory, string componentsDirectory)
static Task<IasGameCompatibilityResult> IasGameCompatibility.CheckAsync(string? gameRootDirectory,
    string componentsDirectory, CancellationToken cancellationToken = default)
record IasGameFileDifference(string Path, string? ExpectedSha256, string? ActualSha256)
record IasGameCompatibilityResult(IasGameCompatibilityStatus Status, IReadOnlyList<IasGameFileDifference> Details)
string IasGameCompatibilityResult.Message { get; }
enum IasGameCompatibilityStatus { Compatible, Incompatible, GameFilesMissing }
IasGameCompatibilityException(IasGameCompatibilityResult result) : InvalidOperationException
IasGameCompatibilityResult IasGameCompatibilityException.Result { get; }

IasStatusService(IasComponentStore store, IasFeedClient feed)
Task<IasModuleState> IasStatusService.GetStatusAsync(string? gameRoot, CancellationToken cancellationToken = default)
abstract record IasModuleState
    record NotInstalled()
    record Installed(string Version)
    record UpdateAvailable(string InstalledVersion, string LatestVersion)
    record GameMismatch(IasGameCompatibilityResult Details, string? InstalledVersion = null)
    record Busy(IasProgress Progress)
    record Error(string Message)
```

激活调用应在构造存储时传入游戏根，缺少路径或游戏文件时版本闸会阻止激活。状态服务共用同一个存储实例即可读取进度。调用者持有并负责释放注入的 `HttpClient`，官方 CLI 明确关闭自动重定向。

## 验签证据

官方 `client-config.json` 中 `rsa_modulus` 和 `rsa_exponent` 固定进代码，指数 Base64 为 `AQAB`。本地原始 1.6.4 签名清单 SHA-256 为 `1f77ea08b20393dfbe6d1dd386c51465e5647f509e9dd265d4a06260fa0c26f1`。[官方公钥配置](https://raw.githubusercontent.com/Estecsky/HD2-Integrated-Armor-System/main/client-config.json)

| 输入 | SHA-256 PKCS#1 v1.5 | SHA-256 PSS | SHA-384 PKCS#1 v1.5 | SHA-384 PSS | SHA-512 PKCS#1 v1.5 | SHA-512 PSS |
| --- | --- | --- | --- | --- | --- | --- |
| Base64 解码的原始 payload 字节 | **True** | False | False | False | False | False |
| Base64 字符串的 UTF-8 字节 | False | False | False | False | False | False |

固定算法 **RSA PKCS#1 v1.5 + SHA-256**，覆盖 Base64 解码后的原始字节。真实签名向量覆盖通过验签、篡改 payload、篡改签名。官方私钥不可用，schema 错误在验证后的解析入口单独测试。

## 集成验证

离线包 `D:\gitCode\HD2ModManager\tmp\ias-lab\download-1.6.4\HD2IAS-1.6.4-offline-update.zip` 提供内层签名清单和 `release.zip`。临时探针调用公开 API 验签、校验整包、解出核心部件和激活，对最终目录再次逐一校验签名清单里的大小和 SHA-256。真实游戏文件只读，两个文件均匹配。沙盒保留 `tmp\ias-feed-check\ias\components\1.6.4\` 和 `current.json`。

```text
GitHub main feed: None, 1.6.4, 20261122012510
Offline signed feed: None, 1.6.4, 20261122012510
Release: 248618011 bytes, SHA-256 541dc4341945b048651d16551e6eeebc052e70cd63162758c6b52c07dcd25192
Host compatibility: Compatible
Activated: 1.6.4; 10 verified files
app/HD2综合护甲系统.deps.json
app/HD2综合护甲系统.dll
app/HD2综合护甲系统.exe
app/HD2综合护甲系统.pri
app/MainWindow.xbf
app/update_notes.json
ExternalRuntime/ArmorAssets.zip
ExternalRuntime/ArmorBase.zip
ExternalRuntime/bundle.json
ExternalRuntime/Generator.zip
```

`hd2mod ias status --manager-root tmp/ias-feed-check --game-data <游戏 data 目录>` 返回 `已安装 IAS 1.6.4。`。`hd2mod ias download` 缺少 `--output` 时返回 `IasOutputRequired`。

`hd2mod ias feed` 直接联网执行退出码 2，终端原文如下。通过 GitHub 连接器取回官方 `main` 的文件后，使用同一套公开验签代码核验通过，版本和序号见上。直接 CLI 联网验收仍受本机代理阻塞。[官方 main 清单](https://github.com/Estecsky/HD2-Integrated-Armor-System/blob/main/feed.json)

```text
错误 [IasFeedNetwork]：无法读取 IAS 官方清单：由于目标计算机积极拒绝，无法连接。 (127.0.0.1:9)
```

## 构建与测试

- **done** · Debug 全方案构建，退出码 0。本机 MSBuild 多节点执行提前返回退出码 1，摘要显示 0 警告、0 错误。保留要求的参数，在末尾追加 `-maxcpucount:1` 覆盖节点数，并关闭项目并行和共享编译。最终命令和输出如下。

```text
dotnet build WpfApp1.sln -c Debug -nodeReuse:false -m:8 -p:NuGetAudit=false -p:BuildInParallel=false -p:UseSharedCompilation=false -maxcpucount:1

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:04.06
```

- **done** · IAS 定向单测，退出码 0。覆盖官方签名、逐字段篡改、schema、超时与 HTTP 错误、206 续传、200 从头覆盖、整包哈希失败清理、部件哈希失败保留旧版、越界条目、游戏兼容性、状态、指针替换失败回滚以及限定范围的移除。HTTP 请求全部经由假 `HttpMessageHandler`。

```text
dotnet test HD2ModCore.Tests/HD2ModCore.Tests.csproj -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -maxcpucount:1 --filter FullyQualifiedName~IasFeedTests

已通过! - 失败:     0，通过:    18，已跳过:     0，总计:    18，持续时间: 376 ms - HD2ModCore.Tests.dll (net8.0)
```

- **partial** · 全量测试退出码 1，合计通过 2,740，失败 1，跳过 1。为遵守本任务的写入范围，只在当前进程中把 `TEMP`、`TMP` 和 `VSTEST_ARTIFACTS_DIRECTORY` 指向工作树沙盒。既有 `EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists` 从临时目录向上寻找模板，命中工作树里的 `tools/ref/HD2SDK-CommunityEdition/materials`，测试预期 `embedded`。该测试及材料查找代码与起点一致。尝试将同一沙盒临时映射为无仓库祖先的盘符，`subst.exe` 返回 `Access denied`，盘符未创建。

```text
dotnet test WpfApp1.sln -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false -maxcpucount:1

已通过! - 失败:     0，通过:  1673，已跳过:     0，总计:  1673，持续时间: 5 s - HD2ModCore.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 136 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 6 s - HD2ModCli.Tests.dll (net10.0)
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 13 ms - Decryptor.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 34 ms - HD2SourceModel.Tests.dll (net8.0)
[xUnit.net 00:00:00.26]     HD2SourceMigration.Tests.Hd2MaterialTemplateTests.EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists [FAIL]
Expected: "embedded"
Actual:   "D:\\gitCode\\HD2ModManager-ias-feed\\tools\\r"···
失败!  - 失败:     1，通过:   565，已跳过:     1，总计:   567，持续时间: 5 s - HD2SourceMigration.Tests.dll (net8.0)
```

- **done** · Lua 回归退出码 0，`conform-stub-test.lua` 因缺夹具跳过。

```text
python tools/physbone-harness/run_physbone_regressions.py

合计 PASS=925 FAIL=0；红=0
```

保留 Debug 构建产物与离线激活沙盒。临时探针、重复整包、诊断日志和本轮生成的 Serena 项目文件已删除。全量测试在 `tmp/ias-feed-check/test-temp` 留下只读测试文件，其中有硬链接。常规递归删除因只读文件终止，强制递归删除被工具的自动审批策略拒绝。剩余测试文件位于 Git 忽略的目录内。清理后再次查询沙盒，CLI 返回 `已安装 IAS 1.6.4。`。

## 待决

- 出站代理需允许访问官方地址后复跑 `hd2mod ias feed`。249 MB 真实完整包的验证来自本地离线包，在线断点续传来自假 HTTP 测试。
- 全量测试需在获准写入且祖先链不含 SDK 模板的临时目录条件下重跑，当前交付门仍有 1 例未通过。
- 状态服务按数字版本比较可用更新。它在提出可更新状态前检查当前安装的游戏兼容性，候选版本的两项哈希只有解出其 ArmorBase 后才能确认，激活时强制检查候选版本。
- 同版本号重发的替换策略和 `minimum_sequence` 的部署语义留待发布策略明确。当前读取最低序号，现有同版本部件仍在使用时拒绝以同一版本号切换暂存部件。
- 操作内正常异常与取消路径已处理。进程被强杀时可能留下未引用的解压临时目录，或在目录移动与指针切换之间留下未引用的版本目录。已提交指针决定当前版本，同版重新解压并激活时清理该版本的孤立目录，`RemoveAsync` 清理暂存和缓存。
