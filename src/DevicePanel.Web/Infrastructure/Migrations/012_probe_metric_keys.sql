-- 二期模块2（TOB-363）：探针内置指标注册（约束 A）
-- status（服务可达性，bool，探针成功 true / 连续失败判定异常 false）、latency_ms（响应耗时，number，ms）。
-- INSERT OR IGNORE：升级存量库中用户已手工注册同名 key 时不覆盖（保留其自定义展示元数据）。
-- 告警不预置任何规则（约束 B：通知一律经告警规则配置，无默认规则）。
INSERT OR IGNORE INTO metric_keys(key, value_type, display_name, unit, built_in, created_at_utc, updated_at_utc) VALUES
    ('status',     'bool',   '服务状态', '',  1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('latency_ms', 'number', '响应时间', 'ms', 1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));
