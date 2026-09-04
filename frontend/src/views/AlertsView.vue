<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  createRule,
  deleteRule,
  fetchAlertQueue,
  fetchAlertSettings,
  listRuleTypes,
  listRules,
  saveAlertSettings,
  setRuleEnabled,
  updateRule,
  type AlertQueue,
  type AlertRule,
  type AlertRuleType,
  type NapcatSettings,
} from '@/api/alerts'
import { listMetricKeys, listTargets, type MetricKey, type Target } from '@/api/targets'

const napcat = ref<NapcatSettings | null>(null)
const napcatBaseUrl = ref('')
const napcatToken = ref('')
const napcatTargetType = ref('private')
const napcatTargetId = ref('')
const savingNapcat = ref(false)
const napcatMessage = ref('')
const napcatError = ref('')

const queue = ref<AlertQueue | null>(null)
const queueError = ref('')
const loadingQueue = ref(false)

const targets = ref<Target[]>([])
const metricKeys = ref<MetricKey[]>([])
const ruleTypes = ref<AlertRuleType[]>([])
const rules = ref<AlertRule[]>([])
const rulesError = ref('')
const rulesMessage = ref('')
const loadingRules = ref(false)

// 规则编辑表单：editingId 为 null 表示新建
const editingId = ref<number | null>(null)
const formTargetId = ref<number | null>(null)
const formRuleType = ref('threshold_above')
const formMetric = ref<string | null>('cpu')
const formParamValues = ref<Record<string, string>>({})
const formEnabled = ref(true)
const formError = ref('')
const savingRule = ref(false)

const activeRuleType = computed(() => ruleTypes.value.find((t) => t.type === formRuleType.value) ?? null)

const metricOptions = computed(() => {
  if (activeRuleType.value?.allowsNullMetric) {
    return metricKeys.value
  }
  return metricKeys.value
})

async function loadSettings(): Promise<void> {
  napcat.value = (await fetchAlertSettings()).napcat
  napcatBaseUrl.value = napcat.value.baseUrl ?? ''
  napcatTargetType.value = napcat.value.targetType || 'private'
  napcatTargetId.value = napcat.value.targetId ?? ''
  napcatToken.value = ''
}

async function loadQueue(): Promise<void> {
  loadingQueue.value = true
  queueError.value = ''
  try {
    queue.value = await fetchAlertQueue()
  } catch (e) {
    queueError.value = e instanceof Error ? e.message : '待发队列获取失败'
  } finally {
    loadingQueue.value = false
  }
}

async function loadRules(): Promise<void> {
  loadingRules.value = true
  rulesError.value = ''
  try {
    rules.value = (await listRules()).items
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '规则加载失败'
  } finally {
    loadingRules.value = false
  }
}

async function onSaveNapcat(): Promise<void> {
  savingNapcat.value = true
  napcatMessage.value = ''
  napcatError.value = ''
  try {
    const input: Record<string, string> = { baseUrl: napcatBaseUrl.value }
    if (napcatToken.value) {
      input.token = napcatToken.value
    }
    if (napcatTargetId.value) {
      input.targetType = napcatTargetType.value
      input.targetId = napcatTargetId.value
    }
    await saveAlertSettings(input)
    await loadSettings()
    napcatMessage.value = '已保存，napcat 配置即时生效'
  } catch (e) {
    napcatError.value = e instanceof Error ? e.message : '保存失败'
  } finally {
    savingNapcat.value = false
  }
}

function resetRuleForm(): void {
  editingId.value = null
  formTargetId.value = targets.value[0]?.id ?? null
  formRuleType.value = 'threshold_above'
  formMetric.value = 'cpu'
  formEnabled.value = true
  applyParamDefaults()
  formError.value = ''
}

function applyParamDefaults(): void {
  const values: Record<string, string> = {}
  for (const descriptor of activeRuleType.value?.paramDescriptors ?? []) {
    values[descriptor.name] = descriptor.defaultValue ?? ''
  }
  formParamValues.value = values
}

function onRuleTypeChange(): void {
  if (!activeRuleType.value?.allowsNullMetric && formMetric.value === null) {
    formMetric.value = metricKeys.value[0]?.key ?? null
  }
  if (activeRuleType.value?.allowsNullMetric) {
    formMetric.value = null
  }
  applyParamDefaults()
}

function onEditRule(rule: AlertRule): void {
  editingId.value = rule.id
  formTargetId.value = rule.targetId
  formRuleType.value = rule.ruleType
  formMetric.value = rule.metric
  formEnabled.value = rule.enabled
  let parsed: Record<string, unknown> = {}
  try {
    parsed = JSON.parse(rule.paramsJson) as Record<string, unknown>
  } catch {
    parsed = {}
  }
  const values: Record<string, string> = {}
  for (const descriptor of activeRuleType.value?.paramDescriptors ?? []) {
    const current = parsed[descriptor.name]
    values[descriptor.name] = current === undefined || current === null ? (descriptor.defaultValue ?? '') : String(current)
  }
  formParamValues.value = values
  formError.value = ''
}

function buildParams(): Record<string, number | string> | null {
  const params: Record<string, number | string> = {}
  for (const descriptor of activeRuleType.value?.paramDescriptors ?? []) {
    const value = formParamValues.value[descriptor.name]
    // type=number 的 v-model 会返回 number，统一字符串化后判空
    const raw = value === null || value === undefined ? '' : String(value).trim()
    if (raw === '') {
      if (descriptor.required) {
        formError.value = `参数 ${descriptor.name} 必填`
        return null
      }
      continue
    }
    if (descriptor.type === 'number') {
      const parsed = Number(raw)
      if (Number.isNaN(parsed)) {
        formError.value = `参数 ${descriptor.name} 必须是数字`
        return null
      }
      params[descriptor.name] = parsed
    } else {
      params[descriptor.name] = raw
    }
  }
  return params
}

async function onSaveRule(): Promise<void> {
  if (formTargetId.value === null) {
    formError.value = '请选择目标'
    return
  }
  const params = buildParams()
  if (params === null) {
    return
  }
  savingRule.value = true
  formError.value = ''
  rulesMessage.value = ''
  try {
    if (editingId.value === null) {
      await createRule({
        targetId: formTargetId.value,
        metric: formMetric.value,
        ruleType: formRuleType.value,
        params,
        enabled: formEnabled.value,
      })
      rulesMessage.value = '规则已创建'
    } else {
      await updateRule(editingId.value, {
        targetId: formTargetId.value,
        metric: formMetric.value,
        ruleType: formRuleType.value,
        params,
        enabled: formEnabled.value,
      })
      rulesMessage.value = '规则已更新'
    }
    resetRuleForm()
    await loadRules()
  } catch (e) {
    formError.value = e instanceof Error ? e.message : '保存失败'
  } finally {
    savingRule.value = false
  }
}

async function onToggleRule(rule: AlertRule): Promise<void> {
  try {
    await setRuleEnabled(rule.id, !rule.enabled)
    await loadRules()
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '操作失败'
  }
}

async function onDeleteRule(rule: AlertRule): Promise<void> {
  try {
    await deleteRule(rule.id)
    if (editingId.value === rule.id) {
      resetRuleForm()
    }
    await loadRules()
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '删除失败'
  }
}

function ruleTypeLabel(type: string): string {
  return ruleTypes.value.find((t) => t.type === type)?.displayName ?? type
}

function paramsSummary(rule: AlertRule): string {
  try {
    const parsed = JSON.parse(rule.paramsJson) as Record<string, unknown>
    return Object.entries(parsed)
      .map(([key, value]) => `${key}=${String(value)}`)
      .join('，')
  } catch {
    return rule.paramsJson
  }
}

function metricLabel(rule: AlertRule): string {
  return rule.metricDisplayName ?? '（无指标 = 心跳离线）'
}

onMounted(async () => {
  try {
    await Promise.all([loadSettings(), loadQueue(), loadRules()])
    const [targetList, keyList, typeList] = await Promise.all([listTargets(), listMetricKeys(), listRuleTypes()])
    targets.value = targetList
    metricKeys.value = keyList
    ruleTypes.value = typeList
    resetRuleForm()
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '告警配置加载失败'
  }
})
</script>

<template>
  <section class="alerts">
    <div class="alerts-header">
      <h1 class="alerts-title">告警规则</h1>
      <p class="alerts-description">
        告警按「目标 + 指标」逐条配置：规则类型可插拔（阈值上/下限、无数据、状态不符），参数可调、可关闭；
        告警经 napcat（OneBot v11 HTTP）分发到 QQ，napcat 不可用时进入本地待发队列，恢复后自动补发、无丢失。
      </p>
    </div>

    <section class="card">
      <h2 class="card-title">{{ editingId === null ? '新建规则' : `编辑规则 #${editingId}` }}</h2>
      <div class="form-grid">
        <label class="control-field">
          <span class="control-label">目标</span>
          <select v-model.number="formTargetId" class="control-select">
            <option :value="null" disabled>选择目标</option>
            <option v-for="target in targets" :key="target.id" :value="target.id">
              {{ target.name }}（{{ target.type === 'device' ? '设备' : '服务' }}）
            </option>
          </select>
        </label>
        <label class="control-field">
          <span class="control-label">规则类型</span>
          <select v-model="formRuleType" class="control-select" @change="onRuleTypeChange">
            <option v-for="ruleType in ruleTypes" :key="ruleType.type" :value="ruleType.type">
              {{ ruleType.displayName }}
            </option>
          </select>
        </label>
        <label class="control-field">
          <span class="control-label">指标</span>
          <select
            v-model="formMetric"
            class="control-select"
            :disabled="activeRuleType?.allowsNullMetric && activeRuleType?.requiresMetric === false"
          >
            <option v-if="activeRuleType?.allowsNullMetric" :value="null">无指标（设备心跳离线）</option>
            <option v-for="key in metricOptions" :key="key.key" :value="key.key">{{ key.displayName }}</option>
          </select>
        </label>
        <label v-for="descriptor in activeRuleType?.paramDescriptors ?? []" :key="descriptor.name" class="control-field">
          <span class="control-label">{{ descriptor.name }}{{ descriptor.required ? ' *' : '' }}</span>
          <input
            v-model="formParamValues[descriptor.name]"
            :type="descriptor.type === 'number' ? 'number' : 'text'"
            class="control-input control-number"
            :placeholder="descriptor.defaultValue ?? descriptor.description"
          />
        </label>
        <label class="control-field control-checkbox">
          <span class="control-label">启用</span>
          <input v-model="formEnabled" type="checkbox" />
        </label>
        <div class="control-field">
          <span class="control-label">&nbsp;</span>
          <div class="inline-buttons">
            <button type="button" class="primary-button" :disabled="savingRule" @click="onSaveRule">
              {{ savingRule ? '保存中…' : editingId === null ? '创建规则' : '保存修改' }}
            </button>
            <button v-if="editingId !== null" type="button" class="ghost-button" @click="resetRuleForm">取消编辑</button>
          </div>
        </div>
      </div>
      <span v-if="formError" class="error-note">{{ formError }}</span>
      <span v-else-if="rulesMessage" class="ok-note">{{ rulesMessage }}</span>
      <p class="card-note">{{ activeRuleType?.description }}</p>
    </section>

    <section class="card">
      <h2 class="card-title">
        规则列表
        <span class="queue-count">{{ rules.length }} 条</span>
        <button type="button" class="link-button" :disabled="loadingRules" @click="loadRules">
          {{ loadingRules ? '刷新中…' : '刷新' }}
        </button>
      </h2>
      <span v-if="rulesError" class="error-note">{{ rulesError }}</span>
      <table v-if="rules.length > 0" class="rule-table">
        <thead>
          <tr>
            <th>目标</th>
            <th>指标</th>
            <th>类型</th>
            <th>参数</th>
            <th>状态</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="rule in rules" :key="rule.id">
            <td>{{ rule.targetName }}</td>
            <td>{{ metricLabel(rule) }}</td>
            <td>{{ ruleTypeLabel(rule.ruleType) }}</td>
            <td class="rule-params">{{ paramsSummary(rule) }}</td>
            <td>
              <button type="button" class="link-button" :class="{ 'rule-disabled': !rule.enabled }" @click="onToggleRule(rule)">
                {{ rule.enabled ? '已启用' : '已关闭' }}
              </button>
            </td>
            <td>
              <button type="button" class="link-button" @click="onEditRule(rule)">编辑</button>
              <button type="button" class="link-button rule-delete" @click="onDeleteRule(rule)">删除</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="card-note">暂无告警规则。</p>
    </section>

    <section class="card">
      <h2 class="card-title">消息渠道（QQ · napcat）</h2>
      <div class="form-grid">
        <label class="control-field">
          <span class="control-label">OneBot HTTP 地址</span>
          <input v-model="napcatBaseUrl" type="url" class="control-input" placeholder="如 http://127.0.0.1:3000" />
        </label>
        <label class="control-field">
          <span class="control-label">access token{{ napcat?.tokenSet ? '（已设置，留空保持不变）' : '' }}</span>
          <input v-model="napcatToken" type="password" class="control-input" placeholder="napcat access_token" />
        </label>
        <label class="control-field">
          <span class="control-label">通知目标类型</span>
          <select v-model="napcatTargetType" class="control-select">
            <option value="private">私聊（QQ 号）</option>
            <option value="group">群聊（群号）</option>
          </select>
        </label>
        <label class="control-field">
          <span class="control-label">通知目标 ID</span>
          <input v-model="napcatTargetId" type="text" class="control-input" placeholder="QQ 号或群号（数字）" />
        </label>
      </div>
      <div class="card-actions">
        <button type="button" class="primary-button" :disabled="savingNapcat" @click="onSaveNapcat">
          {{ savingNapcat ? '保存中…' : '保存渠道配置' }}
        </button>
        <span v-if="napcatMessage" class="ok-note">{{ napcatMessage }}</span>
        <span v-if="napcatError" class="error-note">{{ napcatError }}</span>
      </div>
    </section>

    <section class="card">
      <h2 class="card-title">
        待发队列
        <span class="queue-count">{{ queue?.count ?? 0 }} 条</span>
        <button type="button" class="link-button" :disabled="loadingQueue" @click="loadQueue">
          {{ loadingQueue ? '刷新中…' : '刷新' }}
        </button>
      </h2>
      <span v-if="queueError" class="error-note">{{ queueError }}</span>
      <table v-if="queue && queue.items.length > 0" class="rule-table">
        <thead>
          <tr>
            <th>时间</th>
            <th>渠道</th>
            <th>内容</th>
            <th>已尝试</th>
            <th>最近错误</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in queue.items" :key="item.id">
            <td>{{ new Date(item.createdAtUtc).toLocaleString() }}</td>
            <td>{{ item.channel }}</td>
            <td class="queue-content">{{ item.title }}：{{ item.content }}</td>
            <td>{{ item.attempts }}</td>
            <td class="queue-error">{{ item.lastError ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
      <p v-else class="card-note">队列空闲：napcat 正常或暂无待补发的告警。</p>
    </section>
  </section>
</template>

<style scoped>
.alerts-header {
  margin-bottom: 16px;
}

.alerts-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.alerts-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.card {
  margin-bottom: 16px;
  padding: 16px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
}

.card-title {
  margin: 0 0 12px;
  font-size: 1rem;
  display: flex;
  align-items: center;
  gap: 10px;
}

.form-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  margin-bottom: 12px;
}

.control-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.control-checkbox {
  flex-direction: row;
  align-items: center;
  gap: 8px;
}

.control-label {
  font-size: 0.75rem;
  color: var(--color-text-light);
}

.control-input,
.control-select {
  min-width: 220px;
  padding: 8px 10px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: var(--color-surface);
  font-size: 0.875rem;
  color: var(--color-text);
}

.control-number {
  min-width: 120px;
}

.primary-button {
  padding: 8px 16px;
  border: 1px solid var(--color-primary);
  border-radius: 8px;
  background: var(--color-primary);
  color: #fff;
  font-size: 0.875rem;
  cursor: pointer;
}

.primary-button:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.ghost-button {
  padding: 8px 16px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.875rem;
  cursor: pointer;
}

.inline-buttons {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.card-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
}

.card-note {
  margin: 8px 0 0;
  font-size: 0.78rem;
  color: var(--color-text-light);
}

.ok-note {
  font-size: 0.8rem;
  color: #16a34a;
}

.error-note {
  font-size: 0.8rem;
  color: var(--color-danger);
}

.rule-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.rule-table th,
.rule-table td {
  padding: 8px 10px;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
}

.rule-table th {
  color: var(--color-text-light);
  font-weight: 500;
  font-size: 0.78rem;
}

.rule-params {
  max-width: 320px;
  word-break: break-all;
  color: var(--color-text-light);
}

.rule-disabled {
  color: var(--color-text-light);
}

.rule-delete {
  margin-left: 10px;
  color: var(--color-danger);
}

.queue-count {
  padding: 2px 10px;
  border-radius: 999px;
  background: rgba(100, 116, 139, 0.14);
  font-size: 0.75rem;
  font-weight: 400;
}

.queue-content {
  max-width: 420px;
  word-break: break-all;
}

.queue-error {
  color: var(--color-danger);
  max-width: 240px;
  word-break: break-all;
}

.link-button {
  padding: 0;
  border: none;
  background: transparent;
  color: var(--color-primary);
  font-size: 0.8rem;
  cursor: pointer;
}

.link-button:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

@media (max-width: 768px) {
  .control-input,
  .control-select {
    min-width: 0;
    width: 100%;
  }

  .form-grid {
    gap: 10px;
  }
}
</style>
