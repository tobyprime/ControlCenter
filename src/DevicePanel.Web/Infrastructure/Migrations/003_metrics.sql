-- 设备指标存储：30s 采集明细 + 小时/天级预聚合
-- 时间统一 UTC（ISO-8601 文本）；聚合桶键为桶起始时间（小时桶 / 天桶，UTC）
-- 聚合为增量 upsert：sum/count 求平均，max 取峰值；删除设备级联清理其指标（foreign_keys = ON）
CREATE TABLE metric_samples (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id        INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    collected_at_utc TEXT    NOT NULL,
    cpu_percent      REAL    NOT NULL,
    mem_percent      REAL    NOT NULL,
    disk_percent     REAL    NOT NULL,
    net_rx_bps       REAL    NOT NULL,
    net_tx_bps       REAL    NOT NULL
);

CREATE INDEX idx_metric_samples_device_time ON metric_samples(device_id, collected_at_utc);

CREATE TABLE metric_samples_hourly (
    device_id        INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    bucket_start_utc TEXT    NOT NULL,
    sample_count     INTEGER NOT NULL,
    cpu_sum          REAL    NOT NULL,
    cpu_max          REAL    NOT NULL,
    mem_sum          REAL    NOT NULL,
    mem_max          REAL    NOT NULL,
    disk_sum         REAL    NOT NULL,
    disk_max         REAL    NOT NULL,
    net_rx_sum       REAL    NOT NULL,
    net_rx_max       REAL    NOT NULL,
    net_tx_sum       REAL    NOT NULL,
    net_tx_max       REAL    NOT NULL,
    PRIMARY KEY (device_id, bucket_start_utc)
);

CREATE TABLE metric_samples_daily (
    device_id        INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    bucket_start_utc TEXT    NOT NULL,
    sample_count     INTEGER NOT NULL,
    cpu_sum          REAL    NOT NULL,
    cpu_max          REAL    NOT NULL,
    mem_sum          REAL    NOT NULL,
    mem_max          REAL    NOT NULL,
    disk_sum         REAL    NOT NULL,
    disk_max         REAL    NOT NULL,
    net_rx_sum       REAL    NOT NULL,
    net_rx_max       REAL    NOT NULL,
    net_tx_sum       REAL    NOT NULL,
    net_tx_max       REAL    NOT NULL,
    PRIMARY KEY (device_id, bucket_start_utc)
);
