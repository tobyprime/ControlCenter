<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { listCollectors, type Collector } from '@/api/collectors'
import { fetchTargetAvailableMetrics, listMetricKeys, type MetricKeyInfo } from '@/api/metrics'
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
  fetchAlertEvents,
  fetchAlertQueue,
  fetchAlertSettings,
  saveAlertSettings,
  type AlertEvent,
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

const targets = ref<Collector[]>([])
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

// 按来源可用指标（TOB-374 ①）：targetId → 该来源可用指标集合（优先已上报 key，回退按类型内置 key）
const availableByTarget = ref<Record<number, MetricKeyInfo[]>>({})

async function loadAvailableMetrics(): Promise<void> {
  const entries = await Promise.all(
    targets.value.map(async (target) => {
      try {
        return [target.id, await fetchTargetAvailableMetrics(target.id)] as const
      } catch {
        // 单目标拉取失败按未知处理：该来源回退全量口径
        return [target.id, null] as const
      }
    }),
  )
  const next: Record<number, MetricKeyInfo[]> = {}
  for (const [targetId, infos] of entries) {
    if (infos) {
      next[targetId] = infos
    }
  }
  availableByTarget.value = next
}

// 作用域基准指标：目标作用域 = 该来源可用指标；全局作用域 = 各来源可用指标的并集。
// 集合未就绪（页面刚加载）或整体拉取失败时回退全量注册表，保持原有可用性
const scopeBaseKeys = computed<MetricKeyInfo[]>(() => {
  if (formScope.value === 'target') {
    return availableByTarget.value[formTargetId.value ?? -1] ?? metricKeys.value
  }
  const lists = Object.values(availableByTarget.value)
  if (lists.length === 0) {
    return metricKeys.value
  }
  const union = new Map<string, MetricKeyInfo>()
  for (const list of lists) {
    for (const info of list) {
      if (!union.has(info.key)) {
        union.set(info.key, info)
      }
    }
  }
  return [...union.values()]
})

const availableMetricKeys = computed(() => {
  const scope = formScope.value
  const tid = formTargetId.value
  return scopeBaseKeys.value.filter((info) => {
    const existing = rules.value.some(
      (rule) =>
        rule.metricKey === info.key &&
        rule.ruleType === formRuleType.value &&
        (scope === 'global' ? rule.targetId === null : rule.targetId === tid),
    )
    return !existing
  })
})

// 可用集合异步就绪后收窄：表单打开时若当前指标已不可选，自动换一个可用指标
watch(availableMetricKeys, (keys) => {
  if (!showRuleForm.value || keys.some((info) => info.key === formMetricKey.value)) {
    return
  }
  formMetricKey.value = keys[0]?.key ?? ''
  onFormMetricChange()
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
  // 先定规则类型再选默认指标：可用指标受既有同类型规则冲突过滤
  formRuleType.value = ruleTypes.value[0]?.typeId ?? ''
  formMetricKey.value = availableMetricKeys.value[0]?.key ?? ''
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

// 规则参数修改表单（替代 window.prompt）：校验错误显示在弹窗内，不再用 alert 打断
const showEditForm = ref(false)
const editRule = ref<AlertRule | null>(null)
const editValue = ref('')
const editSubmitting = ref(false)
const editError = ref('')

const editParamKey = computed(() => {
  const params = editRule.value?.parameters ?? {}
  if (params['threshold'] !== undefined) return 'threshold'
  if (params['minutes'] !== undefined) return 'minutes'
  if (params['expected'] !== undefined) return 'expected'
  return ''
})

const editFieldLabel = computed(() => {
  if (editParamKey.value === 'threshold') return '阈值'
  if (editParamKey.value === 'minutes') return '无数据判定时长（分钟）'
  if (editParamKey.value === 'expected') return '期望状态值（bool 写 true/false）'
  return '参数值'
})

function openEditForm(rule: AlertRule): void {
  editRule.value = rule
  editValue.value = String(rule.parameters['threshold'] ?? rule.parameters['minutes'] ?? rule.parameters['expected'] ?? '')
  editError.value = ''
  showEditForm.value = true
}

async function submitEditRule(): Promise<void> {
  const rule = editRule.value
  if (!rule || editSubmitting.value) return
  const parameters: Record<string, unknown> = { ...rule.parameters }
  if (editParamKey.value === 'threshold') {
    const value = Number(editValue.value)
    if (Number.isNaN(value)) {
      editError.value = '请输入数值'
      return
    }
    parameters['threshold'] = value
  } else if (editParamKey.value === 'minutes') {
    const value = Number(editValue.value)
    if (Number.isNaN(value) || value < 1) {
      editError.value = '请输入不小于 1 的分钟数'
      return
    }
    parameters['minutes'] = value
  } else if (editParamKey.value === 'expected') {
    if (!editValue.value.trim()) {
      editError.value = '期望值不能为空'
      return
    }
    parameters['expected'] = editValue.value.trim()
  } else {
    return
  }
  editSubmitting.value = true
  editError.value = ''
  try {
    await updateAlertRule(rule.id, {
      parameters,
      sustainSeconds: rule.sustainSeconds,
      repeatMinutes: rule.repeatMinutes,
      enabled: rule.enabled,
    })
    showEditForm.value = false
    await loadRules()
  } catch (e) {
    editError.value = e instanceof Error ? e.message : '修改规则失败'
  } finally {
    editSubmitting.value = false
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

// 告警事件历史（TOB-403 F2）：按目标与起始时间筛选，倒序展示触发/恢复与 QQ 投递结果
const history = ref<AlertEvent[]>([])
const historyLoading = ref(false)
const historyError = ref('')
const historyTargetId = ref<number | null>(null)
const historyFrom = ref('')

const kindLabels: Record<string, string> = { trigger: '触发', recover: '恢复' }
const deliveryLabels: Record<string, string> = { pending: '待发送', delivered: '已投递', failed: '投递失败' }

function sampleText(event: AlertEvent): string {
  if (!event.sample) return '—'
  if (event.sample.valueText !== null) return event.sample.valueText
  if (event.sample.valueNum !== null) return String(event.sample.valueNum)
  return '—'
}

async function loadHistory(): Promise<void> {
  historyLoading.value = true
  historyError.value = ''
  try {
    const fromUtc = historyFrom.value ? new Date(historyFrom.value).toISOString() : null
    const result = await fetchAlertEvents({ targetId: historyTargetId.value, fromUtc, limit: 200 })
    history.value = result.items
  } catch (e) {
    historyError.value = e instanceof Error ? e.message : '告警历史获取失败'
  } finally {
    historyLoading.value = false
  }
}

watch(historyTargetId, () => loadHistory())

onMounted(async () => {
  try {
    await Promise.all([loadSettings(), loadQueue(), loadHistory(), listRuleTypes().then((types) => (ruleTypes.value = types))])
    const [targetList, keys, ruleList] = await Promise.all([listCollectors(), listMetricKeys(), listAlertRules()])
    targets.value = targetList
    metricKeys.value = keys
    rules.value = ruleList
    await loadAvailableMetrics()
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
              <button type="button" class="link-button" @click="openEditForm(rule)">修改</button>
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

      <div v-if="showEditForm" class="dialog-mask" @click.self="showEditForm = false">
        <div class="dialog">
          <h2 class="dialog-title">修改规则参数</h2>
          <p v-if="editRule" class="field-hint">
            {{ editRule.targetId === null ? '全局规则' : `目标「${editRule.targetName}」` }} ·
            {{ editRule.metricDisplayName }} · {{ ruleTypeLabel(editRule.ruleType) }}
          </p>
          <label class="field">
            <span class="field-label">{{ editFieldLabel }}</span>
            <input
              v-if="editParamKey === 'expected'"
              v-model="editValue"
              type="text"
              class="control-input"
              placeholder="如 true / online"
            />
            <input
              v-else-if="editParamKey === 'minutes'"
              v-model="editValue"
              type="number"
              min="1"
              max="1440"
              class="control-input"
            />
            <input v-else v-model="editValue" type="number" step="any" class="control-input" />
          </label>
          <p v-if="editError" class="error-note">{{ editError }}</p>
          <div class="dialog-actions">
            <button type="button" class="ghost-button" @click="showEditForm = false">取消</button>
            <button type="button" class="primary-button" :disabled="editSubmitting" @click="submitEditRule">
              {{ editSubmitting ? '保存中…' : '保存' }}
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

    <section class="card">
      <h2 class="card-title">
        告警历史
        <span class="queue-count">{{ history.length }} 条</span>
        <button type="button" class="link-button" :disabled="historyLoading" @click="loadHistory">
          {{ historyLoading ? '刷新中…' : '刷新' }}
        </button>
      </h2>
      <div class="history-filters">
        <label class="history-filter">
          <span class="history-filter-label">目标</span>
          <select v-model.number="historyTargetId" class="control-select history-filter-control">
            <option :value="null">全部目标</option>
            <option v-for="target in targets" :key="target.id" :value="target.id">{{ target.name }}</option>
          </select>
        </label>
        <label class="history-filter">
          <span class="history-filter-label">起始时间</span>
          <input v-model="historyFrom" type="datetime-local" class="control-input history-filter-control" @change="loadHistory" />
        </label>
      </div>
      <span v-if="historyError" class="error-note">{{ historyError }}</span>
      <table v-if="history.length > 0" class="override-table">
        <thead>
          <tr>
            <th>时间</th>
            <th>类型</th>
            <th>目标</th>
            <th>指标</th>
            <th>当时值</th>
            <th>内容</th>
            <th>QQ 投递</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="event in history" :key="event.id">
            <td class="history-time">{{ new Date(event.createdAtUtc).toLocaleString() }}</td>
            <td>
              <span class="history-kind" :class="event.kind === 'trigger' ? 'kind-trigger' : 'kind-recover'">
                {{ kindLabels[event.kind] ?? event.kind }}
              </span>
            </td>
            <td>{{ event.targetName }}</td>
            <td>{{ event.metricDisplayName }}</td>
            <td>{{ sampleText(event) }}</td>
            <td class="history-content">{{ event.title }}：{{ event.content }}</td>
            <td>
              <span class="history-delivery" :class="`delivery-${event.deliveryStatus}`">
                {{ deliveryLabels[event.deliveryStatus] ?? event.deliveryStatus }}
              </span>
              <div v-if="event.deliveryError" class="history-delivery-error">{{ event.deliveryError }}</div>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else-if="!historyLoading" class="card-note">暂无告警记录：触发或恢复后这里会保留事件快照（规则、目标、当时值与投递结果）。</p>
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

.history-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  margin-bottom: 12px;
}

.history-filter {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.history-filter-label {
  font-size: 0.75rem;
  color: var(--color-text-light);
}

.history-filter-control {
  min-width: 200px;
}

.history-time {
  white-space: nowrap;
}

.history-kind {
  display: inline-block;
  padding: 2px 10px;
  border-radius: 999px;
  font-size: 0.75rem;
  white-space: nowrap;
}

.history-kind.kind-trigger {
  background: #fef2f2;
  color: #b91c1c;
}

.history-kind.kind-recover {
  background: #ecfdf5;
  color: #047857;
}

.history-content {
  max-width: 380px;
  word-break: break-all;
}

.history-delivery {
  font-size: 0.78rem;
  white-space: nowrap;
}

.history-delivery.delivery-pending {
  color: #b45309;
}

.history-delivery.delivery-delivered {
  color: #16a34a;
}

.history-delivery.delivery-failed {
  color: var(--color-danger);
}

.history-delivery-error {
  font-size: 0.72rem;
  color: var(--color-danger);
  max-width: 200px;
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
