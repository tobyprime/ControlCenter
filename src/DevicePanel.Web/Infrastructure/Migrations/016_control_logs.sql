-- 三期模块4（TOB-377）：控制留痕。每次下发一条记录（操作者/时间/控制器/参数/结果），
-- 供控制日志页按控制器/时间筛选。采集器删除时随行清理（外键级联，与 terminal_sessions 一致）。

CREATE TABLE control_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    collector_id INTEGER NOT NULL REFERENCES collectors(id) ON DELETE CASCADE,
    controller_key TEXT NOT NULL,
    controller_type TEXT NOT NULL,
    controller_label TEXT NOT NULL,
    operator TEXT NOT NULL,
    params_json TEXT NOT NULL,
    status TEXT NOT NULL,
    result_message TEXT,
    created_at_utc TEXT NOT NULL
);

CREATE INDEX idx_control_logs_collector_time ON control_logs(collector_id, created_at_utc DESC);
CREATE INDEX idx_control_logs_controller_time ON control_logs(collector_id, controller_key, created_at_utc DESC);
