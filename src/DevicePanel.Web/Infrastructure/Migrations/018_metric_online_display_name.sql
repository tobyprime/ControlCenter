-- TOB-403 F1 术语统一：「设备」→「采集器」（品牌词除外）。
-- online 指标显示名随术语迁移更新（008 的播种文本已同步修正，本迁移覆盖存量库）。
UPDATE metric_keys SET display_name = '采集器在线状态', updated_at_utc = strftime('%Y-%m-%dT%H:%M:%SZ', 'now')
WHERE key = 'online' AND display_name = '设备在线状态';
