-- 二期模块0（TOB-361）：指标语义中立（约束 A）
-- 1) MetricKey 注册表：key + 值类型（number/enum/string/bool）+ unit 等展示元数据；
--    新增一种指标 = 注册 key + 类型，核心管道零改动。内置指标随迁移播种。
-- 2) 指标存储由"五列宽表"泛化为 (target, metric_key, value) 窄表：明细 + 小时/天级聚合，
--    number 指标保留 sum/max 聚合；存量数据按 cpu/mem/disk/net_rx/net_tx 逐列平移，历史曲线无损。
CREATE TABLE metric_keys (
    key            TEXT PRIMARY KEY,
    value_type     TEXT    NOT NULL,
    display_name   TEXT    NOT NULL,
    unit           TEXT    NOT NULL DEFAULT '',
    built_in       INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT    NOT NULL,
    updated_at_utc TEXT    NOT NULL
);

INSERT INTO metric_keys(key, value_type, display_name, unit, built_in, created_at_utc, updated_at_utc) VALUES
    ('cpu',    'number', 'CPU 使用率',   '%',   1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('mem',    'number', '内存使用率',   '%',   1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('disk',   'number', '磁盘使用率',   '%',   1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('net_rx', 'number', '网络接收速率', 'B/s', 1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('net_tx', 'number', '网络发送速率', 'B/s', 1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('online', 'bool',   '设备在线状态', '',    1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));

-- 明细窄表：number 存 value_num，enum/string/bool 存 value_text（bool 同时存 value_num 0/1 便于查询）
CREATE TABLE metric_samples_v2 (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    target_id  INTEGER NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
    metric_key TEXT    NOT NULL,
    time_utc   TEXT    NOT NULL,
    value_num  REAL,
    value_text TEXT
);

INSERT INTO metric_samples_v2(target_id, metric_key, time_utc, value_num)
    SELECT device_id, 'cpu',    collected_at_utc, cpu_percent FROM metric_samples
    UNION ALL
    SELECT device_id, 'mem',    collected_at_utc, mem_percent FROM metric_samples
    UNION ALL
    SELECT device_id, 'disk',   collected_at_utc, disk_percent FROM metric_samples
    UNION ALL
    SELECT device_id, 'net_rx', collected_at_utc, net_rx_bps FROM metric_samples
    UNION ALL
    SELECT device_id, 'net_tx', collected_at_utc, net_tx_bps FROM metric_samples;

DROP TABLE metric_samples;
ALTER TABLE metric_samples_v2 RENAME TO metric_samples;
CREATE INDEX idx_metric_samples_target_key_time ON metric_samples(target_id, metric_key, time_utc);

-- 小时级聚合窄表（仅 number 指标参与聚合：sum/count 求平均、max 取峰值）
CREATE TABLE metric_samples_hourly_v2 (
    target_id        INTEGER NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
    metric_key       TEXT    NOT NULL,
    bucket_start_utc TEXT    NOT NULL,
    sample_count     INTEGER NOT NULL,
    value_sum        REAL    NOT NULL DEFAULT 0,
    value_max        REAL,
    PRIMARY KEY (target_id, metric_key, bucket_start_utc)
);

INSERT INTO metric_samples_hourly_v2(target_id, metric_key, bucket_start_utc, sample_count, value_sum, value_max)
    SELECT device_id, 'cpu',    bucket_start_utc, sample_count, cpu_sum,    cpu_max    FROM metric_samples_hourly
    UNION ALL
    SELECT device_id, 'mem',    bucket_start_utc, sample_count, mem_sum,    mem_max    FROM metric_samples_hourly
    UNION ALL
    SELECT device_id, 'disk',   bucket_start_utc, sample_count, disk_sum,   disk_max   FROM metric_samples_hourly
    UNION ALL
    SELECT device_id, 'net_rx', bucket_start_utc, sample_count, net_rx_sum, net_rx_max FROM metric_samples_hourly
    UNION ALL
    SELECT device_id, 'net_tx', bucket_start_utc, sample_count, net_tx_sum, net_tx_max FROM metric_samples_hourly;

DROP TABLE metric_samples_hourly;
ALTER TABLE metric_samples_hourly_v2 RENAME TO metric_samples_hourly;

-- 天级聚合窄表（结构与小时级一致）
CREATE TABLE metric_samples_daily_v2 (
    target_id        INTEGER NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
    metric_key       TEXT    NOT NULL,
    bucket_start_utc TEXT    NOT NULL,
    sample_count     INTEGER NOT NULL,
    value_sum        REAL    NOT NULL DEFAULT 0,
    value_max        REAL,
    PRIMARY KEY (target_id, metric_key, bucket_start_utc)
);

INSERT INTO metric_samples_daily_v2(target_id, metric_key, bucket_start_utc, sample_count, value_sum, value_max)
    SELECT device_id, 'cpu',    bucket_start_utc, sample_count, cpu_sum,    cpu_max    FROM metric_samples_daily
    UNION ALL
    SELECT device_id, 'mem',    bucket_start_utc, sample_count, mem_sum,    mem_max    FROM metric_samples_daily
    UNION ALL
    SELECT device_id, 'disk',   bucket_start_utc, sample_count, disk_sum,   disk_max   FROM metric_samples_daily
    UNION ALL
    SELECT device_id, 'net_rx', bucket_start_utc, sample_count, net_rx_sum, net_rx_max FROM metric_samples_daily
    UNION ALL
    SELECT device_id, 'net_tx', bucket_start_utc, sample_count, net_tx_sum, net_tx_max FROM metric_samples_daily;

DROP TABLE metric_samples_daily;
ALTER TABLE metric_samples_daily_v2 RENAME TO metric_samples_daily;
