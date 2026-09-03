-- 初版表结构：单用户账号、服务端会话
-- 约定：所有时间列均为 UTC，ISO-8601 文本存储
CREATE TABLE users (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    username      TEXT    NOT NULL UNIQUE,
    password_hash TEXT    NOT NULL,
    created_at_utc TEXT   NOT NULL
);

CREATE TABLE sessions (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    token_hash     TEXT    NOT NULL UNIQUE,
    username       TEXT    NOT NULL REFERENCES users(username) ON DELETE CASCADE,
    created_at_utc TEXT    NOT NULL,
    expires_at_utc TEXT    NOT NULL
);

CREATE INDEX idx_sessions_expires ON sessions(expires_at_utc);
