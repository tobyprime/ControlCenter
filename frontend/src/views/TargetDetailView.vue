<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import MetricChart, { type ChartSeries } from '@/components/MetricChart.vue'
import { listTargets, type Target } from '@/api/targets'
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
    const [targetList, overviewList, ruleList] = await Promise.all([
      listTargets(),
      fetchTargetOverview(targetId.value),
      listAlertRules({ targetId: targetId.value }),
    ])
    target.value = targetList.find((t) => t.id === targetId.value) ?? null
    overview.value = overviewList
    rules.value = ruleList
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

async function onEditThreshold(rule: AlertRule) {
  const raw = rule.parameters['threshold']
  const input = window.prompt(`修改「${rule.metricDisplayName}」阈值（当前 ${String(raw)}）：`, String(raw ?? ''))
  if (input === null) return
  const value = Number(input)
  if (Number.isNaN(value)) {
    window.alert('请输入数值')
    return
  }
  try {
    await updateAlertRule(rule.id, {
      parameters: { threshold: value },
      sustainSeconds: rule.sustainSeconds,
      repeatMinutes: rule.repeatMinutes,
      enabled: rule.enabled,
    })
    await refresh(false)
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '修改规则失败'
  }
}

async function onEditSustain(rule: AlertRule) {
  const input = window.prompt(`修改「${rule.metricDisplayName}」规则的持续窗口（秒，当前 ${rule.sustainSeconds}）：`, String(rule.sustainSeconds))
  if (input === null) return
  const value = Number(input)
  if (Number.isNaN(value) || value < 0) {
    window.alert('请输入不小于 0 的秒数')
    return
  }
  try {
    await updateAlertRule(rule.id, {
      parameters: rule.parameters,
      sustainSeconds: value,
      repeatMinutes: rule.repeatMinutes,
      enabled: rule.enabled,
    })
    await refresh(false)
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '修改规则失败'
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
        <span class="status-badge" :class="target.online ? 'online' : 'offline'">
          <span class="status-dot"></span>
          {{ target.online ? '在线' : '离线' }}
        </span>
      </div>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>
    <div v-if="loading" class="empty-state">加载中…</div>

    <template v-else>
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
                  <button type="button" class="link-button" @click="onEditThreshold(rule)">修改</button>
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
                <button type="button" class="link-button" @click="onEditSustain(rule)">修改</button>
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
      </section>
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
</style>
