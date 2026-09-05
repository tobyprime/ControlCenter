<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import {
  createTarget,
  deleteTarget,
  listTargets,
  resetTargetToken,
  updateTarget,
  type ProbeMetricMappingInput,
  type Target,
  type TargetType,
} from '@/api/targets'
import { targetStatusInfo } from '@/utils/targetStatus'

const router = useRouter()
const targets = ref<Target[]>([])
const loading = ref(true)
const errorMessage = ref('')
let refreshTimer: number | undefined

// 编辑表单
const showForm = ref(false)
const editingTarget = ref<Target | null>(null)
const formName = ref('')
const formTags = ref('')
const formType = ref<TargetType>('device')
const submitting = ref(false)
const formError = ref('')

// 服务目标的探针配置（创建时填写，修改走详情页）
interface MappingDraft {
  metricKey: string
  jsonPath: string
  valueType: ProbeMetricMappingInput['valueType']
  displayName: string
  unit: string
}
const formProbeUrl = ref('')
const formProbeInterval = ref('60')
const formMappings = ref<MappingDraft[]>([])

// token 展示（仅在创建/重置时返回一次）
const tokenDialog = ref<{ targetName: string; token: string } | null>(null)
const tokenCopied = ref(false)

async function refresh(showError = true) {
  try {
    targets.value = await listTargets()
    errorMessage.value = ''
  } catch (e) {
    if (showError) {
      errorMessage.value = e instanceof Error ? e.message : '目标列表加载失败'
    }
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingTarget.value = null
  formName.value = ''
  formTags.value = ''
  formType.value = 'device'
  formProbeUrl.value = ''
  formProbeInterval.value = '60'
  formMappings.value = []
  formError.value = ''
  showForm.value = true
}

function openEdit(target: Target) {
  editingTarget.value = target
  formName.value = target.name
  formTags.value = target.tags.join('，')
  formType.value = target.type
  formError.value = ''
  showForm.value = true
}

function parseTags(raw: string): string[] {
  return raw
    .split(/[，,]/)
    .map((tag) => tag.trim())
    .filter((tag) => tag.length > 0)
}

function buildProbeInput() {
  const mappings = formMappings.value
    .filter((m) => m.metricKey.trim() && m.jsonPath.trim())
    .map((m) => ({
      metricKey: m.metricKey.trim(),
      jsonPath: m.jsonPath.trim(),
      valueType: m.valueType,
      displayName: m.displayName.trim(),
      unit: m.unit.trim(),
    }))
  const interval = Number(formProbeInterval.value)
  return {
    url: formProbeUrl.value.trim(),
    intervalSeconds: Number.isFinite(interval) && interval > 0 ? interval : undefined,
    mappings,
  }
}

async function submitForm() {
  if (submitting.value) return
  submitting.value = true
  formError.value = ''
  try {
    const tags = parseTags(formTags.value)
    if (editingTarget.value) {
      await updateTarget(editingTarget.value.id, formName.value.trim(), tags)
    } else if (formType.value === 'service') {
      await createTarget({
        type: 'service',
        name: formName.value.trim(),
        tags,
        probe: buildProbeInput(),
      })
    } else {
      const created = await createTarget({ type: 'device', name: formName.value.trim(), tags })
      tokenDialog.value = { targetName: created.name, token: created.agentToken }
    }
    showForm.value = false
    await refresh()
  } catch (e) {
    formError.value = e instanceof Error ? e.message : '保存失败'
  } finally {
    submitting.value = false
  }
}

async function onResetToken(target: Target) {
  if (!window.confirm(`确定重置「${target.name}」的 agent token？旧 token 将立即失效，需要用新 token 更新该设备上的 agent。`)) {
    return
  }
  try {
    const result = await resetTargetToken(target.id)
    tokenDialog.value = { targetName: target.name, token: result.agentToken }
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : 'token 重置失败'
  }
}

async function onDelete(target: Target) {
  if (!window.confirm(`确定删除目标「${target.name}」？其历史曲线与告警规则将一并删除，token 立即失效，删除后不可恢复。`)) {
    return
  }
  try {
    await deleteTarget(target.id)
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
  <section class="devices">
    <div class="devices-header">
      <div>
        <h1 class="devices-title">目标管理</h1>
        <p class="devices-description">设备与服务统一为目标台账，状态每 15 秒自动刷新。</p>
      </div>
      <button type="button" class="primary-button" @click="openCreate">新建目标</button>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>

    <div v-if="loading" class="empty-state">加载中…</div>
    <div v-else-if="targets.length === 0" class="empty-state">
      还没有目标。点击「新建目标」登记第一台设备，然后用签发的 token 在目标机上启动 agent 即可接入。
    </div>

    <div v-else class="device-grid">
      <div v-for="target in targets" :key="target.id" class="device-card">
        <div class="device-head">
          <span class="device-name">{{ target.name }}</span>
          <span class="status-badge" :class="targetStatusInfo(target).cls">
            <span class="status-dot"></span>
            {{ targetStatusInfo(target).label }}
          </span>
        </div>
        <div class="device-tags">
          <span class="tag type-tag">{{ target.type === 'device' ? '设备' : '服务' }}</span>
          <span v-for="tag in target.tags" :key="tag" class="tag">{{ tag }}</span>
          <span v-if="target.tags.length === 0" class="no-tags">无标签</span>
        </div>
        <dl class="device-meta">
          <div>
            <dt>{{ target.type === 'device' ? '最近心跳' : '最近探测' }}</dt>
            <dd>{{ formatTime(target.lastSeenAtUtc) }}</dd>
          </div>
          <div>
            <dt>登记时间</dt>
            <dd>{{ formatTime(target.createdAtUtc) }}</dd>
          </div>
        </dl>
        <div class="device-actions">
          <button type="button" class="ghost-button" @click="router.push(`/targets/${target.id}`)">详情</button>
          <button type="button" class="ghost-button" @click="openEdit(target)">编辑</button>
          <button v-if="target.type === 'device'" type="button" class="ghost-button" @click="onResetToken(target)">重置 Token</button>
          <button type="button" class="danger-button" @click="onDelete(target)">删除</button>
        </div>
      </div>
    </div>

    <div v-if="showForm" class="dialog-mask" @click.self="showForm = false">
      <div class="dialog" :class="{ wide: formType === 'service' && !editingTarget }">
        <h2 class="dialog-title">{{ editingTarget ? '编辑目标' : '新建目标' }}</h2>
        <label class="field">
          <span class="field-label">目标名称</span>
          <input v-model="formName" type="text" placeholder="如：机房A 边缘网关" maxlength="100" />
        </label>
        <label class="field">
          <span class="field-label">标签（用逗号分隔，如位置/用途/网络区域）</span>
          <input v-model="formTags" type="text" placeholder="如：机房A，网关，内网" />
        </label>
        <label class="field">
          <span class="field-label">目标类型</span>
          <select v-model="formType" class="control-select" :disabled="!!editingTarget">
            <option value="device">设备（目标机部署 agent 上报）</option>
            <option value="service">服务（面板侧定时探测，无需 agent）</option>
          </select>
        </label>
        <template v-if="formType === 'service' && !editingTarget">
          <label class="field">
            <span class="field-label">探针 URL（面板定时 GET 该地址，2xx 视为可达）</span>
            <input v-model="formProbeUrl" type="text" placeholder="如：https://map.zenoxs.cn/tiles/settings.json" />
          </label>
          <label class="field">
            <span class="field-label">探测间隔（秒，10~3600，默认 60）</span>
            <input v-model="formProbeInterval" type="number" min="10" max="3600" />
          </label>
          <div class="field">
            <span class="field-label">指标提取映射（从 JSON 响应提取业务指标，可留空）</span>
            <div v-for="(mapping, index) in formMappings" :key="index" class="mapping-row">
              <input v-model="mapping.metricKey" type="text" placeholder="指标名，如 mc.players" />
              <input v-model="mapping.jsonPath" type="text" placeholder="JSONPath，如 $.players.length()" />
              <select v-model="mapping.valueType" class="control-select">
                <option value="number">number</option>
                <option value="enum">enum</option>
                <option value="string">string</option>
              </select>
              <input v-model="mapping.displayName" type="text" placeholder="显示名" />
              <input v-model="mapping.unit" type="text" placeholder="单位" />
              <button type="button" class="mapping-remove" @click="formMappings.splice(index, 1)">删除</button>
            </div>
            <button type="button" class="ghost-button mapping-add" @click="formMappings.push({ metricKey: '', jsonPath: '$', valueType: 'number', displayName: '', unit: '' })">
              + 添加映射
            </button>
          </div>
          <p class="field-hint">服务状态（status）与响应时间（latency_ms）由探针自动产出；连续 3 次探测失败判定异常。提取的指标与状态均可在详情页配置告警规则。</p>
        </template>
        <p v-else-if="editingTarget && editingTarget.type === 'service'" class="field-hint">服务目标的探针配置在目标详情页维护。</p>
        <p v-if="formError" class="error-note">{{ formError }}</p>
        <div class="dialog-actions">
          <button type="button" class="ghost-button" @click="showForm = false">取消</button>
          <button type="button" class="primary-button" :disabled="submitting" @click="submitForm">
            {{ submitting ? '保存中…' : '保存' }}
          </button>
        </div>
      </div>
    </div>

    <div v-if="tokenDialog" class="dialog-mask" @click.self="tokenDialog = null">
      <div class="dialog">
        <h2 class="dialog-title">「{{ tokenDialog.targetName }}」的 agent token</h2>
        <p class="token-warning">token 仅在创建/重置时显示一次，请立即复制保存。泄露或更换时可在目标列表中重置。</p>
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
.devices-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.devices-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.devices-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
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

.device-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 12px;
}

.device-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 16px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
}

.device-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.device-name {
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

.status-badge.alarm {
  background: #fef2f2;
  color: #b91c1c;
}

.status-badge.alarm .status-dot {
  background: #dc2626;
}

.device-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.tag {
  padding: 2px 10px;
  border-radius: 999px;
  background: #eff6ff;
  color: #1d4ed8;
  font-size: 0.75rem;
}

.no-tags {
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.device-meta {
  margin: 0;
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.device-meta dt {
  font-size: 0.72rem;
  color: var(--color-text-light);
}

.device-meta dd {
  margin: 2px 0 0;
  font-size: 0.8rem;
}

.device-actions {
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

.dialog.wide {
  max-width: 760px;
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

.control-select {
  width: 100%;
  padding: 9px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: #fff;
  font-size: 0.875rem;
  color: var(--color-text);
  box-sizing: border-box;
}

.field-hint {
  margin: -4px 0 12px;
  color: var(--color-text-light);
  font-size: 0.75rem;
  line-height: 1.5;
}

.mapping-row {
  display: grid;
  grid-template-columns: 1.2fr 1.4fr 0.7fr 1fr 0.6fr auto;
  gap: 6px;
  margin-bottom: 6px;
  align-items: center;
}

.mapping-row input {
  min-width: 0;
  padding: 7px 8px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  font-size: 0.78rem;
  box-sizing: border-box;
}

.mapping-row .control-select {
  padding: 7px 6px;
  font-size: 0.78rem;
}

.mapping-remove {
  padding: 7px 8px;
  border: 1px solid #fecaca;
  border-radius: 6px;
  background: #fef2f2;
  color: var(--color-danger, #dc2626);
  font-size: 0.75rem;
  cursor: pointer;
  white-space: nowrap;
}

.mapping-add {
  padding: 6px 10px;
  font-size: 0.78rem;
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
  .devices-header {
    flex-direction: column;
  }

  .device-grid {
    grid-template-columns: 1fr;
  }

  .device-actions {
    flex-wrap: wrap;
  }

  .mapping-row {
    grid-template-columns: 1fr 1fr;
  }
}
</style>
