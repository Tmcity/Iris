# 历史存储与查询

> 版本 1 · 2026-09-29 · T-034 从发布阶段前移至 T-013 之前。精确契约变更见 CCR-005，DDL 见 DB_SCHEMA.sql。此单交付真实仓储与初始化，不交付 HistoryWindow。

## 1. 依赖与部署

- Infrastructure 使用 Microsoft.Data.Sqlite **10.0.12** 与 Dapper **2.1.89**，版本已通过 NuGet 官方索引核实；保持中央包管理，现有架构白名单允许这两个包。
- 数据库为 IAppPaths.DatabaseFile；使用 SqliteConnectionStringBuilder，不能将路径拼入连接串。建议 Pooling=false，以便便携目录/测试关闭后释放所有文件句柄；不用 ClearAllPools 影响无关连接。
- 每个连接设置 foreign_keys=ON、busy_timeout=3000（同时设置合理的 DefaultTimeout）；初始化时启用 WAL。实现核验 PRAGMA，不假设所有连接自动继承配置。
- 构造函数无 I/O。数据库初始化与仓储操作在后台执行完整同步工作单元，不在 UI 线程调用伪异步数据库 API。

官方依据：[Microsoft.Data.Sqlite 异步限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)：SQLite 不支持异步 I/O，async ADO.NET 方法同步执行；官方建议使用 WAL 改善并发。已读取该页面，不仅依据搜索摘要。

## 2. 数据与写入

HistoryEntry 保存预处理后的原文、解析后的语言对、来源、UTC 时间和按卡片顺序排列的成功结果。HistoryResult.Truncated 标记结果不完整；T-013 可以将成功但截断的结果写历史，缓存仍禁止写入截断结果。

- AddAsync 忽略输入 Id，返回实际新建或去重更新后的 Id。CreatedAt 由调用方提供（T-013 使用 TimeProvider）；保存为 ToUniversalTime().ToString("O", InvariantCulture)，统一 UTC、固定精度与格式。
- 在调用边界复制 Results 集合，固定不可变记录值，之后再调度后台。调用方在返回 Task 后改动原列表，不得影响已捕获的写入。只复制所属数据，不再次预处理原文、不从 Provider 读取当前名称。
- 入参 entry、Results 及其元素不得为 null；SourceText 不能是空白，SourceLanguage/TargetLanguage、ProviderId/ProviderName 非空白，Result.Text 非 null，ProviderId 在同条记录内不能重复。Model 可为 null。参数错误不回显具体文本。
- 时间、Origin、源文本/语言、结果列表完整写入同一事务；原始/译文/搜索词均作为 SQL 参数，不拼接 SQL。

### 2.1 去重与收藏保护

以 created_at DESC,id DESC 找到最新一条记录。**仅当其未收藏，并且原文的完整 SHA-256、原文自身、source_lang、target_lang 都精确相同**时，更新该条记录的时间、来源、收藏状态及全部结果，保持 Id；否则插入。

- 原文比较是精确比较，不裁剪/大小写折叠、不忽略换行；语言代码由调用方解析，仓储按精确值保存。
- 原文哈希为 UTF-8 的完整 SHA-256 小写十六进制，同时比较 source_text，避免只依赖哈希决定内容相同。
- 最新一条已收藏时，即使内容完全相同也插入新项，绝不通过去重改写收藏内容。
- 更新结果采用删除旧结果/插入新结果并更新 FTS 的同一事务；任一步失败或提交前观察到取消，均回滚。

### 2.2 容量、收藏、删除

HistorySettings.MaxEntries 限制**非收藏记录数**，收藏额外保留。每次 AddAsync 后以及取消收藏后，在同一写事务中保留最新 MaxEntries 条非收藏记录，其余删除。收藏不占此额度。

SetFavoriteAsync/DeleteAsync 的 id 必须为正；不存在的正 id 为幂等 no-op。ClearAsync(keepFavorites=true) 仅删除非收藏，false 则全部删除。所有删除途径都须级联清理 translation_result 和 history_fts。

写入期间从最新 Current 捕获一次 History.MaxEntries；仓储不依据 History.Enabled 静默丢弃 Add 请求，是否记录由编排器执行规则决定。

## 3. 摘要分页与按需详情

查询列表返回 HistorySummary，**不批量装载完整原文或所有译文**。GetAsync(id) 才返回完整 HistoryEntry 与按 sort_order,id 排序的结果；不存在时返回 null。所有返回列表封装为独立只读集合。

HistorySummary 包含 Id、CreatedAt、Origin、SourcePreview、SourceLanguage、TargetLanguage、IsFavorite、ResultCount、HasTruncatedResults。

- SourcePreview 为原文前 200 个 Unicode 标量，超出追加省略号“…”；不切断代理对。查询 SQL 可用 substr/length 生成预览，避免先加载完整原文再截断。
- Page Total 与当前页在同一读事务快照中取得，按 created_at DESC,id DESC 稳定排序。使用聚合/有限次数查询计算 ResultCount 和 HasTruncatedResults，不对每条历史再单独查询一次。
- Offset>=0，Limit 在 1..200，默认 50；非法值抛安全 ArgumentOutOfRangeException，不静默返回无限数据。详情 GetAsync 的 id<=0 同样拒绝。
- 列表展开、复制全文、重新翻译等 UI 操作在 T-035 使用 GetAsync；不能拿 SourcePreview 当全文。查询期间条目被删除时，详情 null 是正常情况。

### 3.1 搜索语义

Keyword 为 null/全空白时不筛选，其余 Trim 后作为**字面量子串**搜索原文或任意译文，不解释为 FTS 操作语法。

- 按 Unicode 标量数判断长度，>=3 使用 FTS5 trigram；整体加双引号，内部双引号写成两个，再作为 MATCH 参数绑定。OR、NEAR、引号、星号均不能成为注入语法。
- <3 使用 LIKE，依次转义反斜杠、% 和 _，绑定模式；ESCAPE 使用单个字符，可通过独立参数 `@escape` 传入反斜杠，避免 C# 与 SQL 双重转义错误。
- Keyword 含 NUL 时抛安全 ArgumentException，避免 SQLite 文本搜索终止字符导致误解；空白判断/Trim 之后再选路径。
- 搜索命中译文仍只返回一条历史摘要，不因多结果重复。FavoritesOnly 与 Keyword 同时生效。FTS/LIKE 的大小写行为沿用 SQLite 默认，不承诺额外地区化折叠。

## 4. 迁移与损坏保护

保留原 V1；新增 V2 添加 is_truncated（旧数据默认 0），并补充稳定分页和语言去重索引。新空库顺序执行 V1→V2；已有 V1 库原数据/Id/收藏及 FTS 不丢失。每个迁移一个事务，仅成功提交时更新 user_version。

- InitializeAsync 幂等、并发安全。取消或失败不标记成功，后续调用可重试；Repository 的公共操作也确保初始化完成，不依赖 UI 正好先调用过。
- 已知最新版本直接使用；高于支持版本、损坏数据库或不兼容结构返回安全 StorageFailure，**不删除、不覆盖、不自动降级或重建用户数据库**。
- V1 的 IF NOT EXISTS 不可被用于掩盖版本 0 下已有的不兼容表；初始化器应检查预期表/列结构。不得仅凭 user_version 就吞掉缺列/损坏问题。
- FTS 插入和更新由仓储在写事务中维护；删除使用 DDL 中的触发器，子结果使用外键级联。不得在主记录提交后另起事务更新索引。

## 5. 并发、取消和错误

- 仓储实例用写门闩串行化写操作；SQLite 写事务同时防护其它连接/进程竞争。读后写的去重/修剪使用立即写事务（非 deferred）避免升级竞态。
- 每次操作独立连接，Query/Get 使用读事务建立一致视图；总数与页记录、头记录与子结果不能来自不同时间点。
- 公共 async 端口通过 Task.Run 等将打开连接、SQL 与事务整个工作单元放在后台。同步操作期间不能承诺即时取消；在进入工作、取得门闩、循环批量写入、提交前检查令牌。取消时仍完成连接/事务释放。
- 提交成功是结果边界；不能在提交之后追加取消检查并将已保存操作伪装成取消，也不能因此重试一遍。提交前观察到取消则回滚并抛 OCE；其它 SQLite/IO 失败为安全 IrisException(StorageFailure)。
- 日志仅包含操作类别与 SQLite 错误码等元数据，不记录源文、译文、关键词、SQL 参数、整条记录或原始异常链。

## 6. 启动和测试

Infrastructure 注册一个 DatabaseInitializer 和一个 SqliteHistoryRepository 单例。AppBootstrapper 在 Settings.Load（含损坏恢复）之后、托盘/热键入口启用之前 await 初始化。启动存储失败沿用既有安全错误出口，不能用 no-op 仓储让 Host 看似可用。

测试使用临时真实文件 SQLite；覆盖 V1→V2、未来版本/损坏不覆盖、收藏重复翻译、源语言不同不去重、稳定排序、摘要/详情分离、FTS 字面量/短词 LIKE、修剪/删除索引一致、事务失败/取消、并发和后台线程。单元/集成测试不联网，不读取用户实际 history.db。

### 已完成的选型探针（不是仓储验收）

临时项目 `C:\Users\28910\AppData\Local\Temp\iris-history-design\Probe.csproj` 使用上述包版本。真实输出：

```text
SQLite=3.53.3
Journal=wal
V1=1
V2=2
LegacyRows=1
LegacyTruncated=0
FTS 中文测 => 1
FTS 🙂🙂🙂 => 1
FTS 50% => 1
FTS quote" => 1
FTS test OR => 0
FTS missing => 0
LIKE % => 1
LIKE _ => 1
LIKE " => 1
LIKE 中文 => 1
Truncated=1
DeleteResults=0
DeleteFts=0
```

该探针仅证明锁定依赖支持所需 SQLite/FTS/级联能力，并验证了 LIKE 的单字符 ESCAPE 写法；正式迁移器、仓储事务及 UI 仍须在各工单独立验收。
