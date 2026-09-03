-- 告警分发（TOB-341）：napcat 连接配置（KV）、阈值（device_id=0 为全局默认）、
-- 待发队列（napcat 不可用时落库，恢复后补发、无丢失）、规则状态（防刷屏与重启去重）
CREATE TABLE panel_settings (
    key            TEXT PRIMARY KEY,
    value          TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE alert_thresholds (
    device_id      INTEGER NOT NULL,
    metric         TEXT    NOT NULL,
    threshold      REAL    NOT NULL,
    updated_at_utc TEXT    NOT NULL,
    PRIMARY KEY (device_id, metric)
);

CREATE TABLE alert_outbox (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at_utc   TEXT    NOT NULL,
    channel          TEXT    NOT NULL,
    payload_json     TEXT    NOT NULL,
    attempts         INTEGER NOT NULL DEFAULT 0,
    last_error       TEXT,
    last_attempt_utc TEXT
);

CREATE INDEX idx_alert_outbox_id ON alert_outbox(id);

CREATE TABLE alert_state (
    rule_key       TEXT PRIMARY KEY,
    state_json     TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
