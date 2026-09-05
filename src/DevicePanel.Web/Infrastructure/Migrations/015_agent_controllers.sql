-- 三期模块4（TOB-377）：控制器实体声明随能力上报持久化
-- agents.controllers_json 存 [{key,type,label,tags,paramsSchema}]；NULL = 未声明（旧版 agent 兼容）。
-- 控制器不是独立台账：生命周期跟随 agent（随能力重报整体覆盖），删除 agent 随行删除。

ALTER TABLE agents ADD COLUMN controllers_json TEXT;
