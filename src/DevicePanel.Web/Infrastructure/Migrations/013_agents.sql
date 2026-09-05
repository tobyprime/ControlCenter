-- 三期模块2（TOB-375）：Agent 实体成为唯一能力宿主（一 agent 一 token）
-- 新建 agents 表承接连接身份（token hash）与能力声明；存量 device 型 target 自动迁移为 agent：
-- token hash 原样平移（agent 零重装、PANEL_TOKEN 不变、重连即识别），名字与 last_seen 随迁。
-- targets.agent_id 建立双写期关联；target 侧 agent_token_hash（NOT NULL UNIQUE，历史约束）
-- 从此退化为关联 agent 的镜像列——认证只查 agents.token_hash，不再读 target 侧 hash。
-- service 型 target 无 agent 通道，不生成 agent、关联保持为空。

CREATE TABLE agents (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    name              TEXT NOT NULL,
    labels_json       TEXT NOT NULL DEFAULT '[]',
    token_hash        TEXT NOT NULL,
    capabilities_json TEXT,
    created_at_utc    TEXT NOT NULL,
    updated_at_utc    TEXT NOT NULL,
    last_seen_at_utc  TEXT
);

CREATE UNIQUE INDEX idx_agents_token_hash ON agents(token_hash);

ALTER TABLE targets ADD COLUMN agent_id INTEGER REFERENCES agents(id);

-- 存量 device 目标逐台生成 agent：名字沿用、token hash 平移、last_seen 随 agent（离线状态不因升级跳变）
INSERT INTO agents(name, labels_json, token_hash, capabilities_json, created_at_utc, updated_at_utc, last_seen_at_utc)
SELECT name, '[]', agent_token_hash, NULL, created_at_utc, updated_at_utc, last_seen_at_utc
FROM targets
WHERE type = 'device';

-- 回填双写期关联（device 型 target ↔ 同 token 的 agent）
UPDATE targets
SET agent_id = (SELECT id FROM agents WHERE agents.token_hash = targets.agent_token_hash)
WHERE type = 'device';
