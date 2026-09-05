<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import MetricChart, { type ChartSeries } from '@/components/MetricChart.vue'
import {
  getProbeConfig,
  listTargets,
  updateProbeConfig,
  type ProbeConfig,
  type ProbeMetricMappingInput,
  type Target,
} from '@/api/targets'
import {
  fetchTargetOverview,
  fetchTargetSeries,
  type MetricOverviewItem,
  type MetricValueType,
  type TargetSeries,
} from '@/api/metrics'
import {
  createAlertRule,
  deleteAlertRule,
  listAlertRules,
  listRuleTypes,
  updateAlertRule,
  type AlertRule,
  type AlertRuleTypeInfo,
} from '@/api/alertRules'
import { targetStatusInfo } from '@/utils/targetStatus'

interface RangeOption {
  label: string
  hours: number
}

const rangeOptions: RangeOption[] = [
  { label: '最近 1 小时', hours: 1 },
  { label: '最近 6 小时', hours: 6 },
  { label: '最近 24 小时', hours: 24 },
]

const route = useRoute()
const router = useRouter()
const targetId = computed(() => Number(route.params.id))

const target = ref<Target | null>(null)
const overview = ref<MetricOverviewItem[]>([])
const rules = ref<AlertRule[]>([])
const ruleTypes = ref<AlertRuleTypeInfo[]>([])
const series = ref<TargetSeries | null>(null)
const chartKey = ref<string | null>(null)
const selectedRange = ref<RangeOption>(rangeOptions[2])
const loading = ref(true)
const errorMessage = ref('')
let refreshTimer: number | undefined

// 服务目标的探针配置（模块2）：面板侧定时探测，配置在此维护
const probe = ref<ProbeConfig | null>(null)
const isService = computed(() => target.value?.type === 'service')
const showProbeForm = ref(false)
const probeUrl = ref('')
const probeInterval = ref('60')
interface MappingDraft {
  metricKey: string
  jsonPath: string
  valueType: ProbeMetricMappingInput['valueType']
  displayName: string
  unit: string
}
const probeMappings = ref<MappingDraft[]>([])
const probeSubmitting = ref(false)
const probeError = ref('')

// 规则创建表单
const showRuleForm = ref(false)
const ruleMetricKey = ref('')
const ruleTypeId = ref<string>('')
const ruleThreshold = ref('90')
const ruleMinutes = ref('10')
const ruleExpected = ref('')
const ruleSustain = ref('60')
const ruleRepeat = ref('0')
const ruleSubmitting = ref(false)
const ruleError = ref('')

function valueTypeOf(key: string): MetricValueType {
  return overview.value.find((item) => item.key === key)?.valueType ?? 'number'
}

function ruleTypeFor(): AlertRuleTypeInfo | undefined {
  return ruleTypes.value.find((t) => t.typeId === ruleTypeId.value)
}

function ruleTypeLabel(typeId: string): string {
  return ruleTypes.value.find((t) => t.typeId === typeId)?.displayName ?? typeId
}

const usableRuleTypes = computed(() => {
  const valueType = valueTypeOf(ruleMetricKey.value)
  return ruleTypes.value.filter((t) => t.supportedValueTypes.includes(valueType))
})

const availableMetricKeys = computed(() => {
  const used = new Set(rules.value.map((rule) => `${rule.metricKey}:${rule.ruleType}`))
  return overview.value.filter((item) => {
    return !usableTypes(item.valueType).every((typeId) => used.has(`${item.key}:${typeId}`))
  })
})

function usableTypes(valueType: MetricValueType): string[] {
  return ruleTypes.value
    .filter((t) => t.supportedValueTypes.includes(valueType))
    .map((t) => t.typeId)
}

const chartSeries = computed<ChartSeries[]>(() => {
  const entry = series.value?.series.find((s) => s.key === chartKey.value)
  if (!entry) return []
  const info = overview.value.find((item) => item.key === entry.key)
  return [
    {
      name: info?.displayName ?? entry.key,
      color: '#2563eb',
      data: entry.points.filter((p) => p.v !== null).map((p) => ({ x: Date.parse(p.t), y: p.v as number })),
    },
  ]
})

const chartInfo = computed(() => overview.value.find((item) => item.key === chartKey.value))

function formatValue(item: MetricOverviewItem): string {
  if (item.valueType === 'bool') {
    return item.latestValueNum === 1 ? 'true' : 'false'
  }
  if (item.latestValueNum !== null) {
    return `${item.latestValueNum}${item.unit ? ` ${item.unit}` : ''}`
  }
  return item.latestValueText ?? '—'
}

function formatTime(utc?: string | null): string {
  if (!utc) return '—'
  return new Date(utc).toLocaleString('zh-CN', { hour12: false })
}

async function refresh(showError = true) {
  if (Number.isNaN(targetId.value)) {
    errorMessage.value = '目标不存在'
    loading.value = false
    return
  }
  try {
    const [targetList, overviewList, ruleList, probeConfig] = await Promise.all([
      listTargets(),
      fetchTargetOverview(targetId.value),
      listAlertRules({ targetId: targetId.value }),
      getProbeConfig(targetId.value).catch(() => null),
    ])
    target.value = targetList.find((t) => t.id === targetId.value) ?? null
    overview.value = overviewList
    rules.value = ruleList
    probe.value = probeConfig
    if (chartKey.value === null && overviewList.length > 0) {
      chartKey.value = overviewList.find((item) => item.valueType === 'number')?.key ?? overviewList[0].key
    }
    if (chartKey.value !== null) {
      await refreshChart()
    }
    errorMessage.value = ''
  } catch (e) {
    if (showError) {
      errorMessage.value = e instanceof Error ? e.message : '目标详情加载失败'
    }
  } finally {
    loading.value = false
  }
}

async function refreshChart() {
  if (chartKey.value === null) return
  const to = new Date()
  const from = new Date(to.getTime() - selectedRange.value.hours * 3600 * 1000)
  series.value = await fetchTargetSeries(targetId.value, [chartKey.value], from.toISOString(), to.toISOString())
}

function onSelectChart() {
  void refreshChart().catch(() => {
    series.value = null
  })
}

function onSelectRange() {
  void refreshChart().catch(() => {
    series.value = null
  })
}

function openRuleForm() {
  ruleMetricKey.value = overview.value[0]?.key ?? ''
  ruleTypeId.value = ''
  ruleError.value = ''
  showRuleForm.value = true
}

function buildParameters(): Record<string, unknown> {
  const type = ruleTypeId.value
  if (type === 'threshold_above' || type === 'threshold_below') {
    return { threshold: Number(ruleThreshold.value) }
  }
  if (type === 'no_data') {
    return { minutes: Number(ruleMinutes.value) }
  }
  if (type === 'state_mismatch') {
    return { expected: ruleExpected.value.trim() }
  }
  return {}
}

async function submitRule() {
  if (ruleSubmitting.value) return
  ruleSubmitting.value = true
  ruleError.value = ''
  try {
    await createAlertRule({
      targetId: targetId.value,
      metricKey: ruleMetricKey.value,
      ruleType: ruleTypeId.value as AlertRule['ruleType'],
      parameters: buildParameters(),
      sustainSeconds: Number(ruleSustain.value),
      repeatMinutes: Number(ruleRepeat.value),
      enabled: true,
    })
    showRuleForm.value = false
    await refresh(false)
  } catch (e) {
    ruleError.value = e instanceof Error ? e.message : '创建规则失败'
  } finally {
    ruleSubmitting.value = false
  }
}

async function onToggleRule(rule: AlertRule) {
  try {
    await updateAlertRule(rule.id, {
      parameters: rule.parameters,
      sustainSeconds: rule.sustainSeconds,
      repeatMinutes: rule.repeatMinutes,
      enabled: !rule.enabled,
    })
    await refresh(false)
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '更新规则失败'
  }
}

// 规则参数修改表单（替代 window.prompt）：mode 区分阈值/持续窗口，校验错误显示在弹窗内
const showEditForm = ref(false)
const editRule = ref<AlertRule | null>(null)
const editMode = ref<'threshold' | 'sustain'>('threshold')
const editValue = ref('')
const editSubmitting = ref(false)
const editError = ref('')

const editFieldLabel = computed(() =>
  editMode.value === 'threshold' ? '阈值' : '持续窗口（秒，0 = 判定即告警）',
)

function openEditThreshold(rule: AlertRule) {
  editRule.value = rule
  editMode.value = 'threshold'
  editValue.value = String(rule.parameters['threshold'] ?? '')
  editError.value = ''
  showEditForm.value = true
}

function openEditSustain(rule: AlertRule) {
  editRule.value = rule
  editMode.value = 'sustain'
  editValue.value = String(rule.sustainSeconds)
  editError.value = ''
  showEditForm.value = true
}

async function submitEdit() {
  const rule = editRule.value
  if (!rule || editSubmitting.value) return
  const value = Number(editValue.value)
  if (Number.isNaN(value) || (editMode.value === 'sustain' && value < 0)) {
    editError.value = editMode.value === 'threshold' ? '请输入数值' : '请输入不小于 0 的秒数'
    return
  }
  editSubmitting.value = true
  editError.value = ''
  try {
    await updateAlertRule(rule.id, {
      parameters: editMode.value === 'threshold' ? { threshold: value } : rule.parameters,
      sustainSeconds: editMode.value === 'sustain' ? value : rule.sustainSeconds,
      repeatMinutes: rule.repeatMinutes,
      enabled: rule.enabled,
    })
    showEditForm.value = false
    await refresh(false)
  } catch (e) {
    editError.value = e instanceof Error ? e.message : '修改规则失败'
  } finally {
    editSubmitting.value = false
  }
}

async function onDeleteRule(rule: AlertRule) {
  if (!window.confirm(`确定删除「${rule.metricDisplayName}」的${ruleTypeLabel(rule.ruleType)}规则？`)) {
    return
  }
  try {
    await deleteAlertRule(rule.id)
    await refresh(false)
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '删除规则失败'
  }
}

function onRuleMetricChange() {
  const usable = usableRuleTypes.value
  if (!usable.some((t) => t.typeId === ruleTypeId.value)) {
    ruleTypeId.value = usable[0]?.typeId ?? ''
  }
}

function openProbeForm() {
  probeUrl.value = probe.value?.url ?? ''
  probeInterval.value = String(probe.value?.intervalSeconds ?? 60)
  probeMappings.value = (probe.value?.mappings ?? []).map((m) => ({
    metricKey: m.metricKey,
    jsonPath: m.jsonPath,
    valueType: (['number', 'enum', 'string'].includes(m.valueType) ? m.valueType : 'number') as MappingDraft['valueType'],
    displayName: m.displayName,
    unit: m.unit,
  }))
  probeError.value = ''
  showProbeForm.value = true
}

async function submitProbe() {
  if (probeSubmitting.value) return
  probeSubmitting.value = true
  probeError.value = ''
  try {
    await updateProbeConfig(targetId.value, {
      url: probeUrl.value.trim(),
      intervalSeconds: Number(probeInterval.value) || undefined,
      mappings: probeMappings.value
        .filter((m) => m.metricKey.trim() && m.jsonPath.trim())
        .map((m) => ({
          metricKey: m.metricKey.trim(),
          jsonPath: m.jsonPath.trim(),
          valueType: m.valueType,
          displayName: m.displayName.trim(),
          unit: m.unit.trim(),
        })),
    })
    showProbeForm.value = false
    await refresh(false)
  } catch (e) {
    probeError.value = e instanceof Error ? e.message : '保存探针配置失败'
  } finally {
    probeSubmitting.value = false
  }
}

onMounted(async () => {
  ruleTypes.value = await listRuleTypes()
  await refresh()
  refreshTimer = window.setInterval(() => refresh(false), 30000)
})

onBeforeUnmount(() => {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
  }
})
</script>

<template>
  <section class="detail">
    <div class="detail-header">
      <button type="button" class="link-back" @click="router.push('/targets')">← 返回目标列表</button>
      <div v-if="target" class="detail-title-row">
        <h1 class="detail-title">{{ target.name }}</h1>
        <span class="tag">{{ target.type === 'device' ? '设备' : '服务' }}</span>
        <span class="status-badge" :class="targetStatusInfo(target).cls">
          <span class="status-dot"></span>
          {{ targetStatusInfo(target).label }}
        </span>
      </div>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>
    <div v-if="loading" class="empty-state">加载中…</div>

    <template v-else>
      <section v-if="isService" class="card">
        <div class="card-head">
          <h2 class="card-title">探针配置</h2>
          <button type="button" class="primary-button" @click="openProbeForm">
            {{ probe ? '修改配置' : '配置探针' }}
          </button>
        </div>
        <div v-if="!probe" class="empty-inline">该服务还没有探针配置，配置后面板将按间隔主动探测。</div>
        <template v-else>
          <dl class="probe-meta">
            <div>
              <dt>探测地址</dt>
              <dd><code>{{ probe.url }}</code></dd>
            </div>
            <div>
              <dt>探测间隔</dt>
              <dd>{{ probe.intervalSeconds }} 秒</dd>
            </div>
            <div>
              <dt>最近探测</dt>
              <dd>{{ formatTime(target?.lastSeenAtUtc) }}</dd>
            </div>
          </dl>
          <div v-if="probe.mappings.length === 0" class="empty-inline">未配置指标提取映射（服务状态与响应时间始终自动采集）。</div>
          <table v-else class="overview-table">
            <thead>
              <tr>
                <th>指标</th>
                <th>key</th>
                <th>JSONPath</th>
                <th>类型</th>
                <th>单位</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="mapping in probe.mappings" :key="mapping.metricKey">
                <td>{{ mapping.displayName }}</td>
                <td><code>{{ mapping.metricKey }}</code></td>
                <td><code>{{ mapping.jsonPath }}</code></td>
                <td>{{ mapping.valueType }}</td>
                <td>{{ mapping.unit || '—' }}</td>
              </tr>
            </tbody>
          </table>
        </template>
      </section>

      <section class="card">
        <div class="card-head">
          <h2 class="card-title">指标总览</h2>
          <span class="card-hint">最近 30 秒内上报的最新值</span>
        </div>
        <div v-if="overview.length === 0" class="empty-inline">该目标还没有上报任何指标。</div>
        <table v-else class="overview-table">
          <thead>
            <tr>
              <th>指标</th>
              <th>key</th>
              <th>最新值</th>
              <th>上报时间</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="item in overview" :key="item.key">
              <td>{{ item.displayName }}</td>
              <td><code>{{ item.key }}</code></td>
              <td><strong>{{ formatValue(item) }}</strong></td>
              <td>{{ formatTime(item.latestTimeUtc) }}</td>
              <td>
                <button type="button" class="link-button" @click="chartKey = item.key; onSelectChart()">看曲线</button>
              </td>
            </tr>
          </tbody>
        </table>
      </section>

      <section v-if="chartKey" class="card">
        <div class="card-head">
          <h2 class="card-title">{{ chartInfo?.displayName ?? chartKey }} 曲线</h2>
          <div class="range-group">
            <button
              v-for="option in rangeOptions"
              :key="option.hours"
              type="button"
              class="range-button"
              :class="{ active: selectedRange.hours === option.hours }"
              @click="selectedRange = option; onSelectRange()"
            >
              {{ option.label }}
            </button>
          </div>
        </div>
        <MetricChart
          :title="chartInfo?.displayName ?? chartKey"
          :unit="chartInfo?.unit || '数值'"
          :series="chartSeries"
          :y-max="chartInfo?.unit === '%' ? 100 : undefined"
        />
      </section>

      <section class="card">
        <div class="card-head">
          <h2 class="card-title">告警规则</h2>
          <button type="button" class="primary-button" @click="openRuleForm">新建规则</button>
        </div>
        <div v-if="rules.length === 0" class="empty-inline">该目标还没有告警规则；全局规则仍然生效，也可按指标单独建规则。</div>
        <table v-else class="rules-table">
          <thead>
            <tr>
              <th>指标</th>
              <th>类型</th>
              <th>参数</th>
              <th>持续窗口</th>
              <th>状态</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="rule in rules" :key="rule.id">
              <td>{{ rule.metricDisplayName }}</td>
              <td>{{ ruleTypeLabel(rule.ruleType) }}</td>
              <td>
                <template v-if="rule.parameters['threshold'] !== undefined">
                  阈值 {{ rule.parameters['threshold'] }}
                  <button type="button" class="link-button" @click="openEditThreshold(rule)">修改</button>
                </template>
                <template v-else-if="rule.parameters['minutes'] !== undefined">
                  {{ rule.parameters['minutes'] }} 分钟
                </template>
                <template v-else-if="rule.parameters['expected'] !== undefined">
                  期望 {{ rule.parameters['expected'] }}
                </template>
              </td>
              <td>
                {{ rule.sustainSeconds }} 秒
                <button type="button" class="link-button" @click="openEditSustain(rule)">修改</button>
              </td>
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
              <span class="field-label">指标</span>
              <select v-model="ruleMetricKey" class="control-select" @change="onRuleMetricChange">
                <option v-for="item in availableMetricKeys" :key="item.key" :value="item.key">
                  {{ item.displayName }}（{{ item.key }}）
                </option>
              </select>
            </label>
            <label class="field">
              <span class="field-label">规则类型</span>
              <select v-model="ruleTypeId" class="control-select">
                <option v-for="t in usableRuleTypes" :key="t.typeId" :value="t.typeId">{{ t.displayName }}</option>
              </select>
            </label>
            <p v-if="ruleTypeFor()" class="field-hint">{{ ruleTypeFor()!.description }}</p>
            <label v-if="ruleTypeId === 'threshold_above' || ruleTypeId === 'threshold_below'" class="field">
              <span class="field-label">阈值</span>
              <input v-model="ruleThreshold" type="number" step="any" />
            </label>
            <label v-if="ruleTypeId === 'no_data'" class="field">
              <span class="field-label">无数据判定时长（分钟）</span>
              <input v-model="ruleMinutes" type="number" min="1" max="1440" />
            </label>
            <label v-if="ruleTypeId === 'state_mismatch'" class="field">
              <span class="field-label">期望状态值（bool 写 true/false）</span>
              <input v-model="ruleExpected" type="text" placeholder="如 true / online" />
            </label>
            <label class="field">
              <span class="field-label">持续窗口（秒，0 = 判定即告警）</span>
              <input v-model="ruleSustain" type="number" min="0" max="86400" />
            </label>
            <label class="field">
              <span class="field-label">重发间隔（分钟，0 = 恢复前只发一次）</span>
              <input v-model="ruleRepeat" type="number" min="0" max="1440" />
            </label>
            <p v-if="ruleError" class="error-note">{{ ruleError }}</p>
            <div class="dialog-actions">
              <button type="button" class="ghost-button" @click="showRuleForm = false">取消</button>
              <button type="button" class="primary-button" :disabled="ruleSubmitting || usableRuleTypes.length === 0" @click="submitRule">
                {{ ruleSubmitting ? '创建中…' : '创建规则' }}
              </button>
            </div>
          </div>
        </div>

        <div v-if="showEditForm" class="dialog-mask" @click.self="showEditForm = false">
          <div class="dialog">
            <h2 class="dialog-title">修改规则</h2>
            <p v-if="editRule" class="field-hint">{{ editRule.metricDisplayName }} · {{ ruleTypeLabel(editRule.ruleType) }}</p>
            <label class="field">
              <span class="field-label">{{ editFieldLabel }}</span>
              <input
                v-model="editValue"
                type="number"
                :min="editMode === 'sustain' ? '0' : undefined"
                :step="editMode === 'threshold' ? 'any' : '1'"
              />
            </label>
            <p v-if="editError" class="error-note">{{ editError }}</p>
            <div class="dialog-actions">
              <button type="button" class="ghost-button" @click="showEditForm = false">取消</button>
              <button type="button" class="primary-button" :disabled="editSubmitting" @click="submitEdit">
                {{ editSubmitting ? '保存中…' : '保存' }}
              </button>
            </div>
          </div>
        </div>
      </section>

      <div v-if="showProbeForm" class="dialog-mask" @click.self="showProbeForm = false">
        <div class="dialog wide">
          <h2 class="dialog-title">探针配置</h2>
          <label class="field">
            <span class="field-label">探针 URL（面板定时 GET 该地址，2xx 视为可达）</span>
            <input v-model="probeUrl" type="text" placeholder="如：https://map.zenoxs.cn/tiles/settings.json" />
          </label>
          <label class="field">
            <span class="field-label">探测间隔（秒，10~3600，默认 60）</span>
            <input v-model="probeInterval" type="number" min="10" max="3600" />
          </label>
          <div class="field">
            <span class="field-label">指标提取映射（从 JSON 响应提取业务指标，可留空）</span>
            <div v-for="(mapping, index) in probeMappings" :key="index" class="mapping-row">
              <input v-model="mapping.metricKey" type="text" placeholder="指标名，如 mc.players" />
              <input v-model="mapping.jsonPath" type="text" placeholder="JSONPath，如 $.players.length()" />
              <select v-model="mapping.valueType" class="control-select">
                <option value="number">number</option>
                <option value="enum">enum</option>
                <option value="string">string</option>
              </select>
              <input v-model="mapping.displayName" type="text" placeholder="显示名" />
              <input v-model="mapping.unit" type="text" placeholder="单位" />
              <button type="button" class="mapping-remove" @click="probeMappings.splice(index, 1)">删除</button>
            </div>
            <button
              type="button"
              class="ghost-button mapping-add"
              @click="probeMappings.push({ metricKey: '', jsonPath: '$', valueType: 'number', displayName: '', unit: '' })"
            >
              + 添加映射
            </button>
          </div>
          <p class="field-hint">服务状态（status）与响应时间（latency_ms）由探针自动产出；连续 3 次探测失败判定异常。修改映射不会删除已注册的指标。</p>
          <p v-if="probeError" class="error-note">{{ probeError }}</p>
          <div class="dialog-actions">
            <button type="button" class="ghost-button" @click="showProbeForm = false">取消</button>
            <button type="button" class="primary-button" :disabled="probeSubmitting" @click="submitProbe">
              {{ probeSubmitting ? '保存中…' : '保存' }}
            </button>
          </div>
        </div>
      </div>
    </template>
  </section>
</template>

<style scoped>
.detail-header {
  margin-bottom: 16px;
}

.link-back {
  border: none;
  background: none;
  color: var(--color-primary);
  cursor: pointer;
  font-size: 0.82rem;
  padding: 0;
  margin-bottom: 8px;
}

.detail-title-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.detail-title {
  margin: 0;
  font-size: 1.25rem;
}

.tag {
  padding: 2px 10px;
  border-radius: 999px;
  background: #eff6ff;
  color: #1d4ed8;
  font-size: 0.75rem;
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

.probe-meta {
  margin: 0 0 12px;
  display: grid;
  grid-template-columns: 2fr 1fr 1fr;
  gap: 8px;
}

.probe-meta dt {
  font-size: 0.72rem;
  color: var(--color-text-light);
}

.probe-meta dd {
  margin: 2px 0 0;
  font-size: 0.82rem;
  overflow-wrap: anywhere;
}

.card {
  padding: 16px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
  margin-bottom: 16px;
}

.card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.card-title {
  margin: 0;
  font-size: 1rem;
}

.card-hint {
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.overview-table,
.rules-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.overview-table th,
.overview-table td,
.rules-table th,
.rules-table td {
  padding: 9px 10px;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
}

.overview-table th,
.rules-table th {
  color: var(--color-text-light);
  font-weight: 500;
  font-size: 0.75rem;
}

.overview-table code,
.rules-table code {
  background: #f1f5f9;
  padding: 2px 6px;
  border-radius: 6px;
}

.link-button {
  border: none;
  background: none;
  color: var(--color-primary);
  cursor: pointer;
  font-size: 0.8rem;
  padding: 0;
}

.link-button.danger {
  color: var(--color-danger);
}

.row-actions {
  display: flex;
  gap: 12px;
}

.empty-inline {
  padding: 18px;
  border: 1px dashed var(--color-border);
  border-radius: 8px;
  color: var(--color-text-light);
  font-size: 0.85rem;
  text-align: center;
}

.range-group {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.range-button {
  padding: 6px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.8rem;
  cursor: pointer;
}

.range-button.active {
  border-color: var(--color-primary);
  color: var(--color-primary);
  background: #eff6ff;
  font-weight: 600;
}

.primary-button {
  padding: 8px 16px;
  border: none;
  border-radius: 8px;
  background: var(--color-primary, #2563eb);
  color: #fff;
  font-size: 0.85rem;
  cursor: pointer;
}

.primary-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.ghost-button {
  padding: 7px 14px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.8rem;
  cursor: pointer;
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
  color: var(--color-danger);
  font-size: 0.75rem;
  cursor: pointer;
  white-space: nowrap;
}

.mapping-add {
  padding: 6px 10px;
  font-size: 0.78rem;
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

.field input {
  width: 100%;
  padding: 9px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  font-size: 0.875rem;
  box-sizing: border-box;
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

.error-note {
  margin: 0 0 12px;
  padding: 8px 12px;
  border-radius: 8px;
  background: #fef2f2;
  border: 1px solid #fecaca;
  color: var(--color-danger);
  font-size: 0.8rem;
}

.error-banner {
  margin: 0 0 12px;
  padding: 10px 12px;
  border: 1px solid #fecaca;
  border-radius: 8px;
  background: #fef2f2;
  color: var(--color-danger);
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

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 16px;
}

@media (max-width: 768px) {
  .probe-meta {
    grid-template-columns: 1fr;
  }

  .mapping-row {
    grid-template-columns: 1fr 1fr;
  }
}
</style>
