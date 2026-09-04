-- 设备台账：名称、多标签（JSON 数组）、agent token（仅存 SHA-256）、最近心跳时间
-- last_seen_at_utc 为最近一次收到该设备消息（auth/心跳）的 UTC 时间；
-- 在线判定 = last_seen 距当前不超过连续 2 个心跳周期（默认 60s，见 AgentOptions）
CREATE TABLE devices (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT    NOT NULL,
    tags_json        TEXT    NOT NULL DEFAULT '[]',
    agent_token_hash TEXT    NOT NULL UNIQUE,
    created_at_utc   TEXT    NOT NULL,
    updated_at_utc   TEXT    NOT NULL,
    last_seen_at_utc TEXT
);
