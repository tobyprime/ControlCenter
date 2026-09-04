-- 二期模块0（TOB-361）：告警规则化（约束 B）
-- 一期"全局默认阈值 + 按设备覆盖"（alert_thresholds）迁移为规则实例（alert_rules）：
--   全局默认行（device_id = 0）→ 全局规则（target_id 为 NULL，作用于所有上报该指标的目标）；
--   按设备覆盖行 → 目标级规则，与一期"覆盖 ?? 全局 ?? 内置默认 90"优先级一致；
--   无全局行时内置默认 90 补种为可编辑全局规则，升级后对同一指标序列的告警行为与一期一致。
-- 规则类型可插拔（threshold_above / threshold_below / no_data / state_mismatch 四种内置），
-- 参数用户可配（parameters_json）、防抖窗口参数化（sustain_seconds）、可关闭（enabled）。
-- 设备在线状态播种为"状态不符"规则（online != true 即告警，sustain=0 对齐一期"判定离线即告警"），
-- 替代一期硬编码的离线告警扫描；旧评估状态（瞬态防抖数据）随模型切换清理。
CREATE TABLE alert_rules (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    target_id       INTEGER REFERENCES targets(id) ON DELETE CASCADE,
    metric_key      TEXT    NOT NULL,
    rule_type       TEXT    NOT NULL,
    enabled         INTEGER NOT NULL DEFAULT 1,
    parameters_json TEXT    NOT NULL,
    sustain_seconds INTEGER NOT NULL DEFAULT 60,
    repeat_minutes  INTEGER NOT NULL DEFAULT 0,
    created_at_utc  TEXT    NOT NULL,
    updated_at_utc  TEXT    NOT NULL,
    UNIQUE(target_id, metric_key, rule_type)
);

CREATE INDEX idx_alert_rules_metric ON alert_rules(metric_key);
CREATE INDEX idx_alert_rules_target ON alert_rules(target_id);

-- 全局默认阈值 → 全局规则
INSERT INTO alert_rules(target_id, metric_key, rule_type, enabled, parameters_json, sustain_seconds, repeat_minutes, created_at_utc, updated_at_utc)
SELECT NULL, metric, 'threshold_above', 1,
       '{"threshold":' || CAST(threshold AS REAL) || '}',
       60, 0, updated_at_utc, updated_at_utc
FROM alert_thresholds
WHERE device_id = 0;

-- 一期内置默认阈值 90（无全局行时生效）补种为可编辑全局规则
INSERT INTO alert_rules(target_id, metric_key, rule_type, enabled, parameters_json, sustain_seconds, repeat_minutes, created_at_utc, updated_at_utc)
SELECT NULL, m.metric, 'threshold_above', 1, '{"threshold":90}', 60, 0,
       strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')
FROM (SELECT 'cpu' AS metric UNION ALL SELECT 'mem' UNION ALL SELECT 'disk') AS m
WHERE NOT EXISTS (
    SELECT 1 FROM alert_rules r
    WHERE r.target_id IS NULL AND r.metric_key = m.metric AND r.rule_type = 'threshold_above');

-- 按设备覆盖 → 目标级规则（同类型下优先于全局规则）
INSERT INTO alert_rules(target_id, metric_key, rule_type, enabled, parameters_json, sustain_seconds, repeat_minutes, created_at_utc, updated_at_utc)
SELECT device_id, metric, 'threshold_above', 1,
       '{"threshold":' || CAST(threshold AS REAL) || '}',
       60, 0, updated_at_utc, updated_at_utc
FROM alert_thresholds
WHERE device_id <> 0;

-- 设备在线状态规则：online != true 告警（状态不符，可编辑可关闭）
INSERT INTO alert_rules(target_id, metric_key, rule_type, enabled, parameters_json, sustain_seconds, repeat_minutes, created_at_utc, updated_at_utc)
VALUES (NULL, 'online', 'state_mismatch', 1, '{"expected":"true"}', 0, 0,
        strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));

-- 旧模型评估状态为瞬态防抖数据：切换后由新引擎按 rule:{id} 重新学习（最多一个持续窗口恢复告警能力）
DELETE FROM alert_state;

DROP TABLE alert_thresholds;
