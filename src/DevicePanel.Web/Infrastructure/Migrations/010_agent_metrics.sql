-- 二期模块1（TOB-362）：agent 新增采集项的内置指标注册（约束 A）
-- 温度（hwmon/thermal，CPU 相关传感器最大值）、温度传感器名、磁盘读写速率（/proc/diskstats 扇区差值）、
-- 内存实际数值（/proc/meminfo used/total）均由 agent 经 metrics.report 的 extra 携带，
-- 按此处注册的 metric key 入库——新增一种指标 = 注册 key + 类型 + unit 展示元数据，核心管道零改动。
-- 告警不预置任何规则（约束 B：新指标默认无规则，用户按需自配）。
-- INSERT OR IGNORE：升级存量库中用户已手工注册同名 key 时不覆盖（保留其自定义展示元数据）。
INSERT OR IGNORE INTO metric_keys(key, value_type, display_name, unit, built_in, created_at_utc, updated_at_utc) VALUES
    ('temp',        'number', '温度',        '°C',  1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('temp_sensor', 'string', '温度传感器',  '',    1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('disk_rx',     'number', '磁盘读取速率', 'B/s', 1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('disk_tx',     'number', '磁盘写入速率', 'B/s', 1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('mem_used',    'number', '内存已用',    'B',   1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('mem_total',   'number', '内存总量',    'B',   1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));
