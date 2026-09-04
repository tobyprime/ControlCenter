-- 二期地基（TOB-360 模块 0）：
-- ① targets 统一设备与服务两类目标（device 目标挂接现有 devices 行，历史数据零迁移零重装）；
-- ② metric_keys 指标键注册表（类型 + unit 展示元数据，核心不内置指标业务含义）；
-- ③ metric_values 通用类型化指标序列（number 落 num_value，enum/string/bool 落 text_value）；
-- ④ alert_rules 告警规则实例（绑定 target+metric，规则类型可插拔，替代一期全局默认阈值+按设备覆盖）。
CREATE TABLE targets (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    type           TEXT    NOT NULL CHECK (type IN ('device', 'service')),
    name           TEXT    NOT NULL,
    device_id      INTEGER NULL REFERENCES devices(id) ON DELETE CASCADE,
    probe_json     TEXT    NULL,
    created_at_utc TEXT    NOT NULL,
    updated_at_utc TEXT    NOT NULL
);

CREATE UNIQUE INDEX idx_targets_device ON targets(device_id) WHERE device_id IS NOT NULL;

-- 现有设备自动迁移为 device 目标（display name 运行时联查 devices.name，改名自动跟随）
INSERT INTO targets(type, name, device_id, created_at_utc, updated_at_utc)
SELECT 'device', name, id, created_at_utc, updated_at_utc FROM devices;

CREATE TABLE metric_keys (
    key            TEXT PRIMARY KEY,
    value_type     TEXT NOT NULL CHECK (value_type IN ('number', 'enum', 'string', 'bool')),
    unit           TEXT,
    display_name   TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE metric_values (
    target_id        INTEGER NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
    key              TEXT    NOT NULL,
    collected_at_utc TEXT    NOT NULL,
    num_value        REAL,
    text_value       TEXT,
    PRIMARY KEY (target_id, key, collected_at_utc)
);

CREATE INDEX idx_metric_values_target_time ON metric_values(target_id, key, collected_at_utc);
CREATE INDEX idx_metric_values_time ON metric_values(collected_at_utc);

CREATE TABLE alert_rules (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    target_id      INTEGER NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
    metric         TEXT,
    rule_type      TEXT    NOT NULL,
    params_json    TEXT    NOT NULL,
    enabled        INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT    NOT NULL,
    updated_at_utc TEXT    NOT NULL
);

CREATE INDEX idx_alert_rules_target ON alert_rules(target_id);
CREATE INDEX idx_alert_rules_enabled ON alert_rules(enabled);
