<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  createAgent,
  deleteAgent,
  listAgents,
  resetAgentToken,
  updateAgentLabels,
  type Agent,
} from '@/api/agents'

const agents = ref<Agent[]>([])
const loading = ref(true)
const errorMessage = ref('')
let refreshTimer: number | undefined

// 标签筛选：选项来自当前全量列表的去重标签；选中后服务端筛选
const selectedLabel = ref('')
const allLabels = computed(() => {
  const labels = new Set<string>()
  for (const agent of agents.value) {
    for (const label of agent.labels) {
      labels.add(label)
    }
  }
  return [...labels].sort((a, b) => a.localeCompare(b, 'zh-CN'))
})

// 创建表单 / 编辑标签表单（编辑只改标签，token 不变）
const showForm = ref(false)
const editingAgent = ref<Agent | null>(null)
const formName = ref('')
const formLabels = ref('')
const submitting = ref(false)
const formError = ref('')

// token 展示（仅在创建/重置时返回一次）
const tokenDialog = ref<{ agentName: string; token: string } | null>(null)
const tokenCopied = ref(false)

async function refresh(showError = true) {
  try {
    agents.value = await listAgents(selectedLabel.value || undefined)
    errorMessage.value = ''
  } catch (e) {
    if (showError) {
      errorMessage.value = e instanceof Error ? e.message : 'Agent 列表加载失败'
    }
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingAgent.value = null
  formName.value = ''
  formLabels.value = ''
  formError.value = ''
  showForm.value = true
}

function openEdit(agent: Agent) {
  editingAgent.value = agent
  formName.value = agent.name
  formLabels.value = agent.labels.join('，')
  formError.value = ''
  showForm.value = true
}

function parseLabels(raw: string): string[] {
  return raw
    .split(/[，,]/)
    .map((label) => label.trim())
    .filter((label) => label.length > 0)
}

async function submitForm() {
  if (submitting.value) return
  const name = formName.value.trim()
  if (!name) {
    formError.value = '请填写 agent 名称'
    return
  }

  submitting.value = true
  formError.value = ''
  try {
    const labels = parseLabels(formLabels.value)
    if (editingAgent.value) {
      await updateAgentLabels(editingAgent.value.id, labels)
    } else {
      const created = await createAgent({ name, labels })
      tokenDialog.value = { agentName: created.name, token: created.agentToken }
    }
    showForm.value = false
    await refresh()
  } catch (e) {
    formError.value = e instanceof Error ? e.message : '保存失败'
  } finally {
    submitting.value = false
  }
}

async function onResetToken(agent: Agent) {
  if (!window.confirm(`确定重置「${agent.name}」的 token？旧 token 将立即失效，在线连接会被断开，需用新 token 更新该设备上的 agent。`)) {
    return
  }
  try {
    const result = await resetAgentToken(agent.id)
    tokenDialog.value = { agentName: agent.name, token: result.agentToken }
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : 'token 重置失败'
  }
}

async function onDelete(agent: Agent) {
  if (agent.collectorId) {
    errorMessage.value = '该 Agent 已关联目标，请在目标管理页删除'
    return
  }
  if (!window.confirm(`确定删除 Agent「${agent.name}」？其 token 立即失效，在线连接将被断开，删除后不可恢复。`)) {
    return
  }
  try {
    await deleteAgent(agent.id)
    await refresh()
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '删除失败'
  }
}

async function copyToken() {
  if (!tokenDialog.value) return
  try {
    await navigator.clipboard.writeText(tokenDialog.value.token)
    tokenCopied.value = true
    window.setTimeout(() => {
      tokenCopied.value = false
    }, 2000)
  } catch {
    // 剪贴板不可用时用户可手动选择复制
  }
}

function formatTime(utc?: string | null): string {
  if (!utc) return '—'
  return new Date(utc).toLocaleString('zh-CN', { hour12: false })
}

function capabilityLabel(capability: string): string {
  switch (capability) {
    case 'metrics':
      return '指标上报'
    case 'terminal':
      return 'Web 终端'
    case 'logs':
      return '日志拉取'
    default:
      return capability
  }
}

onMounted(() => {
  refresh()
  refreshTimer = window.setInterval(() => refresh(false), 15000)
})

onBeforeUnmount(() => {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
  }
})
</script>

<template>
  <section class="agents">
    <div class="agents-header">
      <div>
        <h1 class="agents-title">Agent 管理</h1>
        <p class="agents-description">一 agent 一 token，标签自由标注，能力由 agent 连接后自动上报，状态每 15 秒自动刷新。</p>
      </div>
      <div class="header-controls">
        <select v-model="selectedLabel" class="label-filter" @change="refresh()">
          <option value="">全部标签</option>
          <option v-for="label in allLabels" :key="label" :value="label">{{ label }}</option>
        </select>
        <button type="button" class="primary-button" @click="openCreate">新建 Agent</button>
      </div>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>

    <div v-if="loading" class="empty-state">加载中…</div>
    <div v-else-if="agents.length === 0" class="empty-state">
      <template v-if="selectedLabel">没有带标签「{{ selectedLabel }}」的 Agent。</template>
      <template v-else>还没有 Agent。点击「新建 Agent」签发 token，在目标机上启动 agent 即可接入。</template>
    </div>

    <div v-else class="agent-grid">
      <div v-for="agent in agents" :key="agent.id" class="agent-card">
        <div class="agent-head">
          <span class="agent-name">{{ agent.name }}</span>
          <span class="status-badge" :class="agent.online ? 'online' : 'offline'">
            <span class="status-dot"></span>
            {{ agent.online ? '在线' : '离线' }}
          </span>
        </div>
        <div class="agent-tags">
          <span v-if="agent.collectorId" class="tag linked-tag">已关联目标 #{{ agent.collectorId }}</span>
          <span v-for="label in agent.labels" :key="label" class="tag">{{ label }}</span>
          <span v-if="agent.labels.length === 0" class="no-tags">无标签</span>
        </div>
        <div class="agent-capabilities">
          <span class="cap-label">能力</span>
          <template v-if="agent.capabilities && agent.capabilities.length > 0">
            <span v-for="capability in agent.capabilities" :key="capability" class="tag cap-tag">{{ capabilityLabel(capability) }}</span>
          </template>
          <span v-else-if="agent.capabilities" class="no-tags">未声明能力</span>
          <span v-else class="no-tags">未声明（旧版 agent）</span>
        </div>
        <dl class="agent-meta">
          <div>
            <dt>最近心跳</dt>
            <dd>{{ formatTime(agent.lastSeenAtUtc) }}</dd>
          </div>
          <div>
            <dt>登记时间</dt>
            <dd>{{ formatTime(agent.createdAtUtc) }}</dd>
          </div>
        </dl>
        <div class="agent-actions">
          <button type="button" class="ghost-button" @click="openEdit(agent)">编辑标签</button>
          <button type="button" class="ghost-button" @click="onResetToken(agent)">重置 Token</button>
          <button type="button" class="danger-button" @click="onDelete(agent)">删除</button>
        </div>
      </div>
    </div>

    <div v-if="showForm" class="dialog-mask" @click.self="showForm = false">
      <div class="dialog">
        <h2 class="dialog-title">{{ editingAgent ? '编辑标签' : '新建 Agent' }}</h2>
        <label class="field">
          <span class="field-label">Agent 名称（如：机房A 边缘网关）</span>
          <input v-model="formName" type="text" placeholder="如：机房A 边缘网关" maxlength="100" :disabled="!!editingAgent" />
        </label>
        <label class="field">
          <span class="field-label">标签（用逗号分隔，不限数量，如位置/用途/网络区域）</span>
          <input v-model="formLabels" type="text" placeholder="如：机房A，网关，内网" />
        </label>
        <p v-if="!editingAgent" class="field-hint">提交后签发 token，token 仅显示一次。</p>
        <p v-if="formError" class="error-note">{{ formError }}</p>
        <div class="dialog-actions">
          <button type="button" class="ghost-button" @click="showForm = false">取消</button>
          <button type="button" class="primary-button" :disabled="submitting" @click="submitForm">
            {{ submitting ? '保存中…' : editingAgent ? '保存标签' : '创建并签发 token' }}
          </button>
        </div>
      </div>
    </div>

    <div v-if="tokenDialog" class="dialog-mask" @click.self="tokenDialog = null">
      <div class="dialog">
        <h2 class="dialog-title">「{{ tokenDialog.agentName }}」的 agent token</h2>
        <p class="token-warning">token 仅在创建/重置时显示一次，请立即复制保存。泄露或更换时可在本页重置。</p>
        <code class="token-value">{{ tokenDialog.token }}</code>
        <p class="token-hint">
          在目标设备上启动 agent：<code>devicepanel-agent --url wss://面板地址/agent/ws --token &lt;上面的 token&gt;</code>
        </p>
        <div class="dialog-actions">
          <button type="button" class="primary-button" @click="copyToken">{{ tokenCopied ? '已复制' : '复制 token' }}</button>
          <button type="button" class="ghost-button" @click="tokenDialog = null">我已保存，关闭</button>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.agents-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.agents-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.agents-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.header-controls {
  display: flex;
  align-items: center;
  gap: 8px;
}

.label-filter {
  padding: 8px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: #fff;
  font-size: 0.85rem;
  color: var(--color-text);
}

.primary-button {
  padding: 8px 16px;
  border: none;
  border-radius: 8px;
  background: var(--color-primary, #2563eb);
  color: #fff;
  font-size: 0.875rem;
  cursor: pointer;
  white-space: nowrap;
}

.primary-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.ghost-button,
.danger-button {
  padding: 6px 12px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.8rem;
  cursor: pointer;
}

.ghost-button:hover {
  border-color: var(--color-primary, #2563eb);
  color: var(--color-primary, #2563eb);
}

.danger-button {
  color: var(--color-danger, #dc2626);
}

.danger-button:hover {
  border-color: var(--color-danger, #dc2626);
}

.error-banner {
  margin: 0 0 12px;
  padding: 10px 12px;
  border: 1px solid #fecaca;
  border-radius: 8px;
  background: #fef2f2;
  color: var(--color-danger, #dc2626);
  font-size: 0.85rem;
}

.empty-state {
  padding: 48px 16px;
  border: 1px dashed var(--color-border);
  border-radius: 10px;
  text-align: center;
  color: var(--color-text-light);
  font-size: 0.9rem;
}

.agent-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 12px;
}

.agent-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 16px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
}

.agent-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.agent-name {
  font-weight: 600;
  overflow-wrap: anywhere;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 10px;
  border-radius: 999px;
  font-size: 0.75rem;
  white-space: nowrap;
}

.status-badge .status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
}

.status-badge.online {
  background: #ecfdf5;
  color: #047857;
}

.status-badge.online .status-dot {
  background: #10b981;
}

.status-badge.offline {
  background: #f3f4f6;
  color: #6b7280;
}

.status-badge.offline .status-dot {
  background: #9ca3af;
}

.agent-tags,
.agent-capabilities {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.cap-label {
  font-size: 0.75rem;
  color: var(--color-text-light);
}

.tag {
  padding: 2px 10px;
  border-radius: 999px;
  background: #eff6ff;
  color: #1d4ed8;
  font-size: 0.75rem;
}

.linked-tag {
  background: #f0fdf4;
  color: #15803d;
}

.cap-tag {
  background: #f5f3ff;
  color: #6d28d9;
}

.no-tags {
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.agent-meta {
  margin: 0;
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.agent-meta dt {
  font-size: 0.72rem;
  color: var(--color-text-light);
}

.agent-meta dd {
  margin: 2px 0 0;
  font-size: 0.8rem;
}

.agent-actions {
  display: flex;
  gap: 8px;
  border-top: 1px solid var(--color-border);
  padding-top: 10px;
  margin-top: auto;
}

.dialog-mask {
  position: fixed;
  inset: 0;
  background: rgba(15, 23, 42, 0.45);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
  z-index: 50;
}

.dialog {
  width: 100%;
  max-width: 460px;
  background: #fff;
  border-radius: 12px;
  padding: 20px;
  box-shadow: 0 20px 40px rgba(15, 23, 42, 0.2);
  max-height: 90vh;
  overflow-y: auto;
}

.dialog-title {
  margin: 0 0 14px;
  font-size: 1.05rem;
}

.field {
  display: block;
  margin-bottom: 12px;
}

.field-label {
  display: block;
  margin-bottom: 6px;
  font-size: 0.8rem;
  color: var(--color-text-light);
}

.field input {
  width: 100%;
  padding: 9px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  font-size: 0.875rem;
  box-sizing: border-box;
}

.field input:focus {
  outline: none;
  border-color: var(--color-primary, #2563eb);
}

.field-hint {
  margin: -4px 0 12px;
  color: var(--color-text-light);
  font-size: 0.75rem;
  line-height: 1.5;
}

.error-note {
  margin: 0 0 12px;
  padding: 8px 12px;
  border-radius: 8px;
  background: #fef2f2;
  border: 1px solid #fecaca;
  color: var(--color-danger);
  font-size: 0.8rem;
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 16px;
}

.token-warning {
  margin: 0 0 12px;
  padding: 10px 12px;
  border-radius: 8px;
  background: #fffbeb;
  border: 1px solid #fde68a;
  color: #92400e;
  font-size: 0.8rem;
}

.token-value {
  display: block;
  padding: 10px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: #f8fafc;
  font-size: 0.8rem;
  word-break: break-all;
}

.token-hint {
  margin: 12px 0 0;
  color: var(--color-text-light);
  font-size: 0.78rem;
  word-break: break-all;
}

@media (max-width: 768px) {
  .agents-header {
    flex-direction: column;
  }

  .header-controls {
    width: 100%;
  }

  .label-filter {
    flex: 1;
  }

  .agent-grid {
    grid-template-columns: 1fr;
  }

  .agent-actions {
    flex-wrap: wrap;
  }
}
</style>
