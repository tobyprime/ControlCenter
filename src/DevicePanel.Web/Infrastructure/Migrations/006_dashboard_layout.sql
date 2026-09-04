-- 主页布局持久化（TOB-366）：单用户单套布局，整份布局以 JSON 存储
-- layout_json 由服务层校验后整体序列化；卡片条目含卡片 id、类型、排序、显隐与
-- config（业务透传字段，后端只存不校验语义）。无外键，独立于设备/目标数据，
-- 设备增删不影响已存布局完整性。
CREATE TABLE dashboard_layouts (
    id             INTEGER PRIMARY KEY CHECK (id = 1),
    layout_json    TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
