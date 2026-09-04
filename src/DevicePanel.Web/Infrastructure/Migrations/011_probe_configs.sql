-- 二期模块2（TOB-363）：服务目标 HTTP/JSON 探针配置
-- 一目标一配置（PRIMARY KEY = target_id），随目标删除级联清理（foreign_keys = ON 每连接强制）。
-- mappings_json：[{ "metricKey": "...", "jsonPath": "...", "valueType": "number|enum|string", "displayName": "...", "unit": "..." }]
CREATE TABLE probe_configs (
    target_id        INTEGER PRIMARY KEY REFERENCES targets(id) ON DELETE CASCADE,
    url              TEXT NOT NULL,
    interval_seconds INTEGER NOT NULL,
    mappings_json    TEXT NOT NULL,
    created_at_utc   TEXT NOT NULL,
    updated_at_utc   TEXT NOT NULL
);
