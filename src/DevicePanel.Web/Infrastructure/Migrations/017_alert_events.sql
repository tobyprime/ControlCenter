-- 模块C（TOB-403 F2）：告警事件历史。触发/恢复各记一行；规则与目标信息在写入时快照
-- （规则删除、目标更名后历史仍可读），样本值快照供"当时值"回看。
-- delivery_status 由分发链路回写：pending → delivered / failed（分发语义不变，只补记账）。
-- alert_outbox 增加可空关联列 alert_event_id：历史机制上线前的在队消息为 NULL，不回写。

CREATE TABLE alert_events (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id          INTEGER,
    rule_type        TEXT    NOT NULL,
    target_id        INTEGER,
    target_name      TEXT    NOT NULL,
    metric_key       TEXT    NOT NULL,
    metric_display   TEXT    NOT NULL,
    kind             TEXT    NOT NULL,
    title            TEXT    NOT NULL,
    content          TEXT    NOT NULL,
    sample_json      TEXT,
    delivery_status  TEXT    NOT NULL DEFAULT 'pending',
    delivered_at_utc TEXT,
    delivery_error   TEXT,
    created_at_utc   TEXT    NOT NULL
);

CREATE INDEX idx_alert_events_time ON alert_events(created_at_utc DESC);
CREATE INDEX idx_alert_events_target_time ON alert_events(target_id, created_at_utc DESC);

ALTER TABLE alert_outbox ADD COLUMN alert_event_id INTEGER;
