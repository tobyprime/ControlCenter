-- 二期模块0（TOB-361）：设备泛化为目标（Target）
-- devices 更名 targets 并新增 type 列（device/service）；存量设备自动成为 device 目标，
-- token、指标、终端、告警等外键引用随更名由 SQLite 自动改写，agent 零重装、历史数据无损。
ALTER TABLE devices RENAME TO targets;

ALTER TABLE targets ADD COLUMN type TEXT NOT NULL DEFAULT 'device';
