-- 终端会话留痕：会话元数据（设备、操作者、起止时间、关闭原因）+ 命令与输出留档
-- 会话 ID 为面板生成的 GUID 文本；entries.direction 固定 input / output；
-- data 存解码后的 UTF-8 文本（超长单帧截断存储，见 TerminalStore）；删除设备级联清理（foreign_keys = ON）
CREATE TABLE terminal_sessions (
    id             TEXT    PRIMARY KEY,
    device_id      INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    operator       TEXT    NOT NULL DEFAULT '',
    opened_at_utc  TEXT    NOT NULL,
    closed_at_utc  TEXT,
    close_reason   TEXT
);

CREATE INDEX idx_terminal_sessions_device ON terminal_sessions(device_id, opened_at_utc);

CREATE TABLE terminal_entries (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id       TEXT    NOT NULL REFERENCES terminal_sessions(id) ON DELETE CASCADE,
    direction        TEXT    NOT NULL,
    data             TEXT    NOT NULL,
    recorded_at_utc  TEXT    NOT NULL
);

CREATE INDEX idx_terminal_entries_session ON terminal_entries(session_id, id);
