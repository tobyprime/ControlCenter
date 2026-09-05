<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { listTargets, type Target } from '@/api/targets'
import { listMetricKeys, type MetricKeyInfo } from '@/api/metrics'
import {
  createAlertRule,
  deleteAlertRule,
  listAlertRules,
  listRuleTypes,
  updateAlertRule,
  type AlertRule,
  type AlertRuleTypeInfo,
} from '@/api/alertRules'
import {
  fetchAlertQueue,
  fetchAlertSettings,
  saveAlertSettings,
  type AlertQueue,
  type NapcatSettings,
} from '@/api/alerts'

const napcat = ref<NapcatSettings | null>(null)
const napcatBaseUrl = ref('')
const napcatToken = ref('')
const napcatTargetType = ref('private')
const napcatTargetId = ref('')
const savingNapcat = ref(false)
const napcatMessage = ref('')
const napcatError = ref('')

const targets = ref<Target[]>([])
const metricKeys = ref<MetricKeyInfo[]>([])
const ruleTypes = ref<AlertRuleTypeInfo[]>([])
const rules = ref<AlertRule[]>([])
const rulesError = ref('')

const rulesLoading = ref(true)
const queue = ref<AlertQueue | null>(null)
const queueError = ref('')
const loadingQueue = ref(false)

// 规则创建表单
const showRuleForm = ref(false)
const formScope = ref<'global' | 'target'>('global')
const formTargetId = ref<number | null>(null)
const formMetricKey = ref('')
const formRuleType = ref('')
const formThreshold = ref('90')
const formMinutes = ref('10')
const formExpected = ref('')
const formSustain = ref('60')
const formRepeat = ref('0')
const formSubmitting = ref(false)
const formError = ref('')

function ruleTypeLabel(typeId: string): string {
  return ruleTypes.value.find((t) => t.typeId === typeId)?.displayName ?? typeId
}

function ruleTypeOf(typeId: string): AlertRuleTypeInfo | undefined {
  return ruleTypes.value.find((t) => t.typeId === typeId)
}

function metricLabelOf(key: string): string {
  return metricKeys.value.find((info) => info.key === key)?.displayName ?? key
}

const formValueType = computed(() => metricKeys.value.find((info) => info.key === formMetricKey.value)?.valueType ?? 'number')

const formUsableTypes = computed(() =>
  ruleTypes.value.filter((t) => t.supportedValueTypes.includes(formValueType.value)),
)

const availableMetricKeys = computed(() => {
  const scope = formScope.value
  const tid = formTargetId.value
  return metricKeys.value.filter((info) => {
    const existing = rules.value.some(
      (rule) =>
        rule.metricKey === info.key &&
        rule.ruleType === formRuleType.value &&
        (scope === 'global' ? rule.targetId === null : rule.targetId === tid),
    )
    return !existing
  })
})

async function loadSettings(): Promise<void> {
  napcat.value = (await fetchAlertSettings()).napcat
  napcatBaseUrl.value = napcat.value.baseUrl ?? ''
  napcatTargetType.value = napcat.value.targetType || 'private'
  napcatTargetId.value = napcat.value.targetId ?? ''
  napcatToken.value = ''
}

async function loadRules(): Promise<void> {
  rules.value = await listAlertRules()
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

function openRuleForm(): void {
  formScope.value = 'global'
  formTargetId.value = targets.value[0]?.id ?? null
  formMetricKey.value = metricKeys.value[0]?.key ?? ''
  formRuleType.value = ruleTypes.value[0]?.typeId ?? ''
  formError.value = ''
  showRuleForm.value = true
}

function onFormScopeChange(): void {
  // 切换作用域/指标时若与现有规则冲突，换一个可用指标
  if (!availableMetricKeys.value.some((info) => info.key === formMetricKey.value)) {
    formMetricKey.value = availableMetricKeys.value[0]?.key ?? ''
  }
  onFormMetricChange()
}

function onFormMetricChange(): void {
  const usable = formUsableTypes.value
  if (!usable.some((t) => t.typeId === formRuleType.value)) {
    formRuleType.value = usable[0]?.typeId ?? ''
  }
}

function buildParameters(): Record<string, unknown> {
  if (formRuleType.value === 'threshold_above' || formRuleType.value === 'threshold_below') {
    return { threshold: Number(formThreshold.value) }
  }
  if (formRuleType.value === 'no_data') {
    return { minutes: Number(formMinutes.value) }
  }
  if (formRuleType.value === 'state_mismatch') {
    return { expected: formExpected.value.trim() }
  }
  return {}
}

async function submitRule(): Promise<void> {
  if (formSubmitting.value) return
  formSubmitting.value = true
  formError.value = ''
  try {
    await createAlertRule({
      targetId: formScope.value === 'global' ? null : formTargetId.value,
      metricKey: formMetricKey.value,
      ruleType: formRuleType.value as AlertRule['ruleType'],
      parameters: buildParameters(),
      sustainSeconds: Number(formSustain.value),
      repeatMinutes: Number(formRepeat.value),
      enabled: true,
    })
    showRuleForm.value = false
    await loadRules()
  } catch (e) {
    formError.value = e instanceof Error ? e.message : '创建规则失败'
  } finally {
    formSubmitting.value = false
  }
}

async function onToggleRule(rule: AlertRule): Promise<void> {
  try {
    await updateAlertRule(rule.id, {
      parameters: rule.parameters,
      sustainSeconds: rule.sustainSeconds,
      repeatMinutes: rule.repeatMinutes,
      enabled: !rule.enabled,
    })
    await loadRules()
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '更新规则失败'
  }
}

async function onEditRule(rule: AlertRule): Promise<void> {
  const raw = rule.parameters['threshold'] ?? rule.parameters['minutes'] ?? rule.parameters['expected']
  const input = window.prompt(
    `修改「${rule.metricDisplayName}」${ruleTypeLabel(rule.ruleType)}规则参数（当前 ${String(raw)}）：`,
    String(raw ?? ''),
  )
  if (input === null) return
  const parameters: Record<string, unknown> = { ...rule.parameters }
  if (parameters['threshold'] !== undefined) {
    const value = Number(input)
    if (Number.isNaN(value)) {
      window.alert('请输入数值')
      return
    }
    parameters['threshold'] = value
  } else if (parameters['minutes'] !== undefined) {
    const value = Number(input)
    if (Number.isNaN(value) || value < 1) {
      window.alert('请输入不小于 1 的分钟数')
      return
    }
    parameters['minutes'] = value
  } else if (parameters['expected'] !== undefined) {
    if (!input.trim()) {
      window.alert('期望值不能为空')
      return
    }
    parameters['expected'] = input.trim()
  }
  try {
    await updateAlertRule(rule.id, {
      parameters,
      sustainSeconds: rule.sustainSeconds,
      repeatMinutes: rule.repeatMinutes,
      enabled: rule.enabled,
    })
    await loadRules()
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '修改规则失败'
  }
}

async function onDeleteRule(rule: AlertRule): Promise<void> {
  const scope = rule.targetId === null ? '全局规则' : `目标「${rule.targetName}」的规则`
  if (!window.confirm(`确定删除${scope}：${rule.metricDisplayName} · ${ruleTypeLabel(rule.ruleType)}？`)) {
    return
  }
  try {
    await deleteAlertRule(rule.id)
    await loadRules()
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '删除规则失败'
  }
}

function parameterText(rule: AlertRule): string {
  if (rule.parameters['threshold'] !== undefined) return `阈值 ${rule.parameters['threshold']}`
  if (rule.parameters['minutes'] !== undefined) return `${rule.parameters['minutes']} 分钟`
  if (rule.parameters['expected'] !== undefined) return `期望 ${rule.parameters['expected']}`
  return JSON.stringify(rule.parameters)
}

onMounted(async () => {
  try {
    await Promise.all([loadSettings(), loadQueue(), listRuleTypes().then((types) => (ruleTypes.value = types))])
    const [targetList, keys, ruleList] = await Promise.all([listTargets(), listMetricKeys(), listAlertRules()])
    targets.value = targetList
    metricKeys.value = keys
    rules.value = ruleList
  } catch (e) {
    rulesError.value = e instanceof Error ? e.message : '告警规则加载失败'
  } finally {
    rulesLoading.value = false
  }
})
</script>

<template>
  <section class="alerts">
    <div class="alerts-header">
      <h1 class="alerts-title">告警规则</h1>
      <p class="alerts-description">
        告警按 (目标, 指标) 配置为可插拔的规则实例：内置阈值上越限、阈值下越限、无数据、状态不符四种类型，参数可配、可关闭；
        目标级规则优先于全局规则。告警经 napcat（OneBot v11 HTTP）分发到 QQ，napcat 不可用时进入本地待发队列，恢复后自动补发、无丢失。
      </p>
    </div>

    <section class="card">
      <h2 class="card-title">
        规则列表
        <button type="button" class="primary-button" @click="openRuleForm">新建规则</button>
      </h2>
      <span v-if="rulesError" class="error-note">{{ rulesError }}</span>
      <div v-if="rulesLoading" class="card-note">加载中…</div>
      <table v-else class="override-table">
        <thead>
          <tr>
            <th>范围</th>
            <th>指标</th>
            <th>规则类型</th>
            <th>参数</th>
            <th>持续窗口</th>
            <th>重发间隔</th>
            <th>状态</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="rule in rules" :key="rule.id">
            <td>{{ rule.targetId === null ? '全局' : rule.targetName }}</td>
            <td>{{ rule.metricDisplayName }}</td>
            <td>{{ ruleTypeLabel(rule.ruleType) }}</td>
            <td>
              {{ parameterText(rule) }}
              <button type="button" class="link-button" @click="onEditRule(rule)">修改</button>
            </td>
            <td>{{ rule.sustainSeconds }} 秒</td>
            <td>{{ rule.repeatMinutes === 0 ? '恢复前一次' : `${rule.repeatMinutes} 分钟` }}</td>
            <td>
              <span class="status-badge" :class="rule.enabled ? 'online' : 'offline'">
                <span class="status-dot"></span>
                {{ rule.enabled ? '启用' : '已关闭' }}
              </span>
            </td>
            <td class="row-actions">
              <button type="button" class="link-button" @click="onToggleRule(rule)">
                {{ rule.enabled ? '关闭' : '启用' }}
              </button>
              <button type="button" class="link-button danger" @click="onDeleteRule(rule)">删除</button>
            </td>
          </tr>
        </tbody>
      </table>

      <div v-if="showRuleForm" class="dialog-mask" @click.self="showRuleForm = false">
        <div class="dialog">
          <h2 class="dialog-title">新建告警规则</h2>
          <label class="field">
            <span class="field-label">作用范围</span>
            <select v-model="formScope" class="control-select" @change="onFormScopeChange">
              <option value="global">全局（对所有目标生效）</option>
              <option value="target">单个目标</option>
            </select>
          </label>
          <label v-if="formScope === 'target'" class="field">
            <span class="field-label">目标</span>
            <select v-model.number="formTargetId" class="control-select" @change="onFormScopeChange">
              <option v-for="target in targets" :key="target.id" :value="target.id">{{ target.name }}</option>
            </select>
          </label>
          <label class="field">
            <span class="field-label">指标</span>
            <select v-model="formMetricKey" class="control-select" @change="onFormMetricChange">
              <option v-for="info in availableMetricKeys" :key="info.key" :value="info.key">
                {{ info.displayName }}（{{ info.key }}）
              </option>
            </select>
          </label>
          <label class="field">
            <span class="field-label">规则类型</span>
            <select v-model="formRuleType" class="control-select">
              <option v-for="t in formUsableTypes" :key="t.typeId" :value="t.typeId">{{ t.displayName }}</option>
            </select>
          </label>
          <p v-if="ruleTypeOf(formRuleType)" class="field-hint">{{ ruleTypeOf(formRuleType)!.description }}</p>
          <label v-if="formRuleType === 'threshold_above' || formRuleType === 'threshold_below'" class="field">
            <span class="field-label">阈值</span>
            <input v-model="formThreshold" type="number" step="any" class="control-input" />
          </label>
          <label v-if="formRuleType === 'no_data'" class="field">
            <span class="field-label">无数据判定时长（分钟）</span>
            <input v-model="formMinutes" type="number" min="1" max="1440" class="control-input" />
          </label>
          <label v-if="formRuleType === 'state_mismatch'" class="field">
            <span class="field-label">期望状态值（bool 写 true/false）</span>
            <input v-model="formExpected" type="text" class="control-input" placeholder="如 true / online" />
          </label>
          <label class="field">
            <span class="field-label">持续窗口（秒，0 = 判定即告警）</span>
            <input v-model="formSustain" type="number" min="0" max="86400" class="control-input" />
          </label>
          <label class="field">
            <span class="field-label">重发间隔（分钟，0 = 恢复前只发一次）</span>
            <input v-model="formRepeat" type="number" min="0" max="1440" class="control-input" />
          </label>
          <p v-if="formError" class="error-note">{{ formError }}</p>
          <div class="dialog-actions">
            <button type="button" class="ghost-button" @click="showRuleForm = false">取消</button>
            <button type="button" class="primary-button" :disabled="formSubmitting || formUsableTypes.length === 0" @click="submitRule">
              {{ formSubmitting ? '创建中…' : '创建规则' }}
            </button>
          </div>
        </div>
      </div>
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
      <table v-if="queue && queue.items.length > 0" class="override-table">
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

.card-note {
  margin: 8px 0 0;
  font-size: 0.78rem;
  color: var(--color-text-light);
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
  padding: 8px 14px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.85rem;
  cursor: pointer;
}

.card-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
}

.ok-note {
  font-size: 0.8rem;
  color: #16a34a;
}

.error-note {
  font-size: 0.8rem;
  color: var(--color-danger);
  display: inline-block;
}

.override-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.override-table th,
.override-table td {
  padding: 8px 10px;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
}

.override-table th {
  color: var(--color-text-light);
  font-weight: 500;
  font-size: 0.78rem;
}

.row-actions {
  display: flex;
  gap: 12px;
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

.link-button.danger {
  color: var(--color-danger);
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
  max-width: 480px;
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

.field-hint {
  margin: -6px 0 12px;
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.field-label {
  display: block;
  margin-bottom: 6px;
  font-size: 0.8rem;
  color: var(--color-text-light);
}

.dialog .control-select,
.dialog .control-input {
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 16px;
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
