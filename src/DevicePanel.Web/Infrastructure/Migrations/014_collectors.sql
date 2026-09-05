-- 三期模块3（TOB-376）：targets → collectors 统一采集器抽象
-- 1) 台账更名 targets → collectors（沿用 007 devices→targets 模式：SQLite RENAME 自动改写
--    metric_samples×3 / alert_rules / terminal_sessions / collector_pull_configs 的外键引用）。
-- 2) type 硬分类下沉为内置标签：type:device / type:service 追加进 tags_json（已含同名标签不重复），
--    与自定义标签同渠道——可编辑、可筛选；随后移除 type 列（SQLite ≥3.35 DROP COLUMN）。
--    离线判定改由"是否关联 agent"表达：push 采集器有 agent_id，pull 采集器为空。
-- 3) probe_configs 更名 collector_pull_configs（pull 采集器配置），主键列随迁改名为 collector_id。
--    存量明细/聚合表与告警规则的 target_id 列名为内部存储细节，保持不变（外键已随迁）。

ALTER TABLE targets RENAME TO collectors;

UPDATE collectors
SET tags_json = json_insert(tags_json, '$[#]', 'type:device')
WHERE type = 'device'
  AND NOT EXISTS (SELECT 1 FROM json_each(tags_json) WHERE value = 'type:device');

UPDATE collectors
SET tags_json = json_insert(tags_json, '$[#]', 'type:service')
WHERE type = 'service'
  AND NOT EXISTS (SELECT 1 FROM json_each(tags_json) WHERE value = 'type:service');

ALTER TABLE collectors DROP COLUMN type;

ALTER TABLE probe_configs RENAME TO collector_pull_configs;
ALTER TABLE collector_pull_configs RENAME COLUMN target_id TO collector_id;
