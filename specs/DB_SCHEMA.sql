-- =====================================================================================================
--  Iris — 历史记录数据库 Schema（SQLite，文件：{DataDirectory}\history.db）
--  访问：Microsoft.Data.Sqlite + Dapper；实现：Iris.Infrastructure/Storage/SqliteHistoryRepository
--  版本：PRAGMA user_version。IDatabaseInitializer 读取 user_version，按顺序执行 V1..Vn 迁移，每个迁移一个事务。
--  时间：一律存 UTC ISO-8601 文本（DateTimeOffset.UtcNow.ToString("O")），可按字典序排序。
-- =====================================================================================================

-- 每次打开连接时执行（非迁移）：
--   PRAGMA foreign_keys = ON;
--   PRAGMA journal_mode = WAL;
--   PRAGMA busy_timeout = 3000;

-- ------------------------------------------------ V1 ------------------------------------------------
BEGIN;

CREATE TABLE IF NOT EXISTS translation_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at      TEXT    NOT NULL,                 -- UTC ISO-8601
    origin          TEXT    NOT NULL,                 -- TextOrigin 枚举名：Input / Selection / Screenshot ...
    source_text     TEXT    NOT NULL,
    source_lang     TEXT    NOT NULL,                 -- LanguageCodes 值（可能为 'auto'）
    target_lang     TEXT    NOT NULL,
    text_hash       TEXT    NOT NULL,                 -- SHA-256 hex（预处理后的 source_text），用于去重
    is_favorite     INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1))
);

CREATE INDEX IF NOT EXISTS ix_history_created_at ON translation_history (created_at DESC);
CREATE INDEX IF NOT EXISTS ix_history_favorite   ON translation_history (is_favorite, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_history_hash       ON translation_history (text_hash, target_lang);

CREATE TABLE IF NOT EXISTS translation_result (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    history_id      INTEGER NOT NULL REFERENCES translation_history (id) ON DELETE CASCADE,
    sort_order      INTEGER NOT NULL,                 -- 与结果卡片顺序一致
    provider_id     TEXT    NOT NULL,
    provider_name   TEXT    NOT NULL,                 -- 冗余存储：Provider 被删除后历史仍可展示
    model           TEXT,
    result_text     TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_result_history ON translation_result (history_id, sort_order);

-- 全文检索（独立 FTS5 表，由仓储在同一事务内维护：rowid = translation_history.id）
-- trigram 分词器支持中日韩子串检索（要求 SQLite ≥ 3.34，Microsoft.Data.Sqlite 自带的 e_sqlite3 满足）。
-- 关键字长度 < 3 时 trigram 无法匹配，仓储改用 LIKE 查询 source_text / result_text。
CREATE VIRTUAL TABLE IF NOT EXISTS history_fts USING fts5 (
    source_text,
    result_text,                                      -- 该条历史所有结果以 '\n' 拼接
    tokenize = 'trigram'
);

-- 删除历史时同步删除 FTS 行（插入 / 更新由仓储显式维护）
CREATE TRIGGER IF NOT EXISTS trg_history_delete_fts
AFTER DELETE ON translation_history
BEGIN
    DELETE FROM history_fts WHERE rowid = old.id;
END;

PRAGMA user_version = 1;

COMMIT;

-- ------------------------------------------------ 查询参考 ------------------------------------------------
-- 分页（无关键字）：
--   SELECT * FROM translation_history
--   WHERE (@favoritesOnly = 0 OR is_favorite = 1)
--   ORDER BY created_at DESC LIMIT @limit OFFSET @offset;
--
-- 关键字（≥ 3 字符）：
--   SELECT h.* FROM history_fts f JOIN translation_history h ON h.id = f.rowid
--   WHERE history_fts MATCH @keyword AND (@favoritesOnly = 0 OR h.is_favorite = 1)
--   ORDER BY h.created_at DESC LIMIT @limit OFFSET @offset;
--   注意：@keyword 需转义为 FTS5 字符串字面量：'"' || replace(keyword, '"', '""') || '"'
--
-- 修剪（保留最新 @max 条，收藏不删）：
--   DELETE FROM translation_history
--   WHERE is_favorite = 0 AND id NOT IN (SELECT id FROM translation_history ORDER BY created_at DESC LIMIT @max);
--
-- 去重：AddAsync 前查询最新一条；若 text_hash 与 target_lang 都相同 → 删除其 translation_result、
--       插入新结果、更新 created_at / origin，并重写对应 history_fts 行。
