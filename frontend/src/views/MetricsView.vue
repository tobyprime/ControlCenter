<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  deleteMetricKey,
  fetchTargetSeries,
  listMetricKeys,
  registerMetricKey,
  type Granularity,
  type MetricKeyInfo,
  type MetricValueType,
  type TargetSeries,
} from '@/api/metrics'
import { listTargets, type Target } from '@/api/targets'
import MetricChart, { type ChartSeries } from '@/components/MetricChart.vue'

interface RangeOption {
  label: string
  hours: number
}

const rangeOptions: RangeOption[] = [
  { label: '最近 1 小时', hours: 1 },
  { label: '最近 6 小时', hours: 6 },
  { label: '最近 24 小时', hours: 24 },
  { label: '最近 7 天', hours: 24 * 7 },
  { label: '最近 30 天', hours: 24 * 30 },
]

const chartColors = ['#2563eb', '#16a34a', '#d97706', '#9333ea', '#dc2626', '#0891b2', '#65a30d', '#db2777']

const targets = ref<Target[]>([])
const metricKeys = ref<MetricKeyInfo[]>([])
const selectedTargetId = ref<number | null>(null)
const selectedKeys = ref<string[]>([])
const selectedRange = ref<RangeOption>(rangeOptions[0])
const series = ref<TargetSeries | null>(null)
const loading = ref(true)
const errorMessage = ref('')
let refreshTimer: number | undefined

// 指标注册表单（约束 A：新增一种指标 = 注册 key + 类型）
const showRegister = ref(false)
const regKey = ref('')
const regValueType = ref<MetricValueType>('number')
const regDisplayName = ref('')
const regUnit = ref('')
const regMessage = ref('')
const regError = ref('')
const regSubmitting = ref(false)

const granularityText = computed(() => {
  switch (series.value?.granularity) {
    case 'raw':
      return '明细（约 30 秒/点）'
    case 'hour':
      return '小时聚合'
    case 'day':
      return '天聚合'
    default:
      return ''
  }
})

function keyInfo(key: string): MetricKeyInfo | undefined {
  return metricKeys.value.find((info) => info.key === key)
}

function keyLabel(key: string): string {
  return keyInfo(key)?.displayName ?? key
}

function isNumberKey(key: string): boolean {
  return keyInfo(key)?.valueType === 'number'
}

const chartModels = computed(() => {
  return (series.value?.series ?? []).map((entry, index) => {
    const info = keyInfo(entry.key)
    const chartSeries: ChartSeries[] = [
      {
        name: keyLabel(entry.key),
        color: chartColors[index % chartColors.length],
        data: entry.points
          .filter((p) => p.v !== null)
          .map((p) => ({ x: Date.parse(p.t), y: p.v as number })),
      },
    ]
    const isPercent = info?.unit === '%'
    return {
      key: entry.key,
      title: info?.displayName ?? entry.key,
      unit: info?.unit || '数值',
      series: chartSeries,
      yMax: isPercent ? 100 : undefined,
      hasData: entry.points.length > 0,
    }
  })
})

async function refresh(showError = true) {
  const targetId = selectedTargetId.value
  if (targetId === null || selectedKeys.value.length === 0) {
    series.value = null
    loading.value = false
    return
  }

  const to = new Date()
  const from = new Date(to.getTime() - selectedRange.value.hours * 3600 * 1000)
  try {
    series.value = await fetchTargetSeries(
      targetId,
      selectedKeys.value,
      from.toISOString(),
      to.toISOString(),
    )
    errorMessage.value = ''
  } catch (e) {
    if (showError) {
      errorMessage.value = e instanceof Error ? e.message : '指标数据加载失败'
    }
  } finally {
    loading.value = false
  }
}

async function loadBaseline() {
  try {
    const [targetList, keys] = await Promise.all([listTargets(), listMetricKeys()])
    targets.value = targetList
    metricKeys.value = keys
    const defaultKeys = ['cpu', 'mem', 'disk', 'net_rx', 'net_tx'].filter((key) =>
      keys.some((info) => info.key === key),
    )
    selectedKeys.value = defaultKeys
    if (targets.value.length > 0 && selectedTargetId.value === null) {
      selectedTargetId.value = targets.value[0].id
    }
    await refresh()
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '目标或指标列表加载失败'
    loading.value = false
  }
}

function onSelectTarget() {
  loading.value = true
  void refresh()
}

function onSelectRange() {
  loading.value = true
  void refresh()
}

function onToggleKey() {
  loading.value = true
  void refresh()
}

async function submitRegister() {
  if (regSubmitting.value) return
  regSubmitting.value = true
  regMessage.value = ''
  regError.value = ''
  try {
    await registerMetricKey({
      key: regKey.value.trim(),
      valueType: regValueType.value,
      displayName: regDisplayName.value.trim(),
      unit: regUnit.value.trim(),
    })
    metricKeys.value = await listMetricKeys()
    if (!selectedKeys.value.includes(regKey.value.trim())) {
      selectedKeys.value = [...selectedKeys.value, regKey.value.trim()]
    }
    regMessage.value = `指标「${regDisplayName.value.trim()}」已注册，agent 上报该 key 后自动入库展示`
    regKey.value = ''
    regDisplayName.value = ''
    regUnit.value = ''
  } catch (e) {
    regError.value = e instanceof Error ? e.message : '注册失败'
  } finally {
    regSubmitting.value = false
  }
}

async function onDeleteKey(info: MetricKeyInfo) {
  if (!window.confirm(`确定删除指标「${info.displayName}」（${info.key}）？未上报且无规则的指标才能删除。`)) {
    return
  }
  try {
    await deleteMetricKey(info.key)
    metricKeys.value = await listMetricKeys()
    selectedKeys.value = selectedKeys.value.filter((key) => key !== info.key)
    await refresh()
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '删除失败'
  }
}

function valueTypeLabel(type: MetricValueType): string {
  switch (type) {
    case 'number':
      return '数值'
    case 'enum':
      return '枚举'
    case 'string':
      return '字符串'
    case 'bool':
      return '布尔'
  }
}

onMounted(() => {
  void loadBaseline()
  refreshTimer = window.setInterval(() => void refresh(false), 30000)
})

onBeforeUnmount(() => {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
  }
})
</script>

<template>
  <section class="metrics">
    <div class="metrics-header">
      <div>
        <h1 class="metrics-title">指标曲线</h1>
        <p class="metrics-description">目标每 30 秒采集上报一次；长跨度自动切换小时/天聚合，数据保留约 30 天。</p>
      </div>
      <button type="button" class="ghost-button" @click="showRegister = !showRegister">
        {{ showRegister ? '收起指标注册' : '注册新指标' }}
      </button>
    </div>

    <section v-if="showRegister" class="card register-card">
      <h2 class="card-title">指标注册</h2>
      <p class="card-description">新增一种指标只需注册 key 与值类型，agent 上报后自动走同一管道（语义中立，核心不感知具体指标）。</p>
      <div class="register-form">
        <label class="control-field">
          <span class="control-label">指标 key（小写字母开头，可用 . 与 _）</span>
          <input v-model="regKey" type="text" class="control-input" placeholder="如 temp.cpu / player.count" />
        </label>
        <label class="control-field">
          <span class="control-label">值类型</span>
          <select v-model="regValueType" class="control-select">
            <option value="number">数值（number）</option>
            <option value="enum">枚举（enum）</option>
            <option value="string">字符串（string）</option>
            <option value="bool">布尔（bool）</option>
          </select>
        </label>
        <label class="control-field">
          <span class="control-label">显示名</span>
          <input v-model="regDisplayName" type="text" class="control-input" placeholder="如 CPU 温度" maxlength="50" />
        </label>
        <label class="control-field">
          <span class="control-label">单位（可选）</span>
          <input v-model="regUnit" type="text" class="control-input" placeholder="如 °C、%、B/s" maxlength="20" />
        </label>
        <button type="button" class="primary-button" :disabled="regSubmitting" @click="submitRegister">
          {{ regSubmitting ? '注册中…' : '注册' }}
        </button>
      </div>
      <p v-if="regMessage" class="success-note">{{ regMessage }}</p>
      <p v-if="regError" class="error-note">{{ regError }}</p>

      <table class="keys-table">
        <thead>
          <tr>
            <th>key</th>
            <th>显示名</th>
            <th>类型</th>
            <th>单位</th>
            <th>来源</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="info in metricKeys" :key="info.key">
            <td><code>{{ info.key }}</code></td>
            <td>{{ info.displayName }}</td>
            <td>{{ valueTypeLabel(info.valueType) }}</td>
            <td>{{ info.unit || '—' }}</td>
            <td>{{ info.builtIn ? '内置' : '自定义' }}</td>
            <td>
              <button v-if="!info.builtIn" type="button" class="link-button danger" @click="onDeleteKey(info)">删除</button>
            </td>
          </tr>
        </tbody>
      </table>
    </section>

    <div class="metrics-controls">
      <label class="control-field">
        <span class="control-label">目标</span>
        <select v-model.number="selectedTargetId" class="control-select" @change="onSelectTarget">
          <option v-for="target in targets" :key="target.id" :value="target.id">{{ target.name }}</option>
        </select>
      </label>

      <div class="range-group" role="group" aria-label="时间范围">
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

    <div class="key-group" role="group" aria-label="指标选择">
      <button
        v-for="info in metricKeys"
        :key="info.key"
        type="button"
        class="range-button"
        :class="{ active: selectedKeys.includes(info.key) }"
        @click="selectedKeys.includes(info.key) ? selectedKeys = selectedKeys.filter(k => k !== info.key) : selectedKeys = [...selectedKeys, info.key]; onToggleKey()"
      >
        {{ info.displayName }}
      </button>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>

    <div v-if="loading" class="empty-state">加载中…</div>
    <div v-else-if="targets.length === 0" class="empty-state">
      还没有目标。先到「目标管理」登记目标并接入 agent，指标数据会自动上报。
    </div>
    <template v-else>
      <p class="granularity-note">
        当前口径：<strong>{{ granularityText || '—' }}</strong>
        <span v-if="series && series.series.every((s) => s.points.length === 0)">　所选范围内暂无数据，试试更长的时间范围。</span>
      </p>
      <div class="chart-grid">
        <MetricChart
          v-for="model in chartModels"
          :key="model.key"
          :title="model.title"
          :unit="model.unit"
          :series="model.series"
          :y-max="model.yMax"
        />
      </div>
    </template>
  </section>
</template>

<style scoped>
.metrics-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.metrics-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.metrics-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.card {
  padding: 16px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
  margin-bottom: 16px;
}

.card-title {
  margin: 0 0 4px;
  font-size: 1rem;
}

.card-description {
  margin: 0 0 12px;
  color: var(--color-text-light);
  font-size: 0.8rem;
}

.register-form {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 12px;
  margin-bottom: 12px;
}

.register-form .control-field {
  min-width: 180px;
}

.control-input {
  padding: 8px 10px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  font-size: 0.875rem;
  color: var(--color-text);
}

.success-note {
  margin: 0 0 12px;
  padding: 8px 12px;
  border-radius: 8px;
  background: #ecfdf5;
  border: 1px solid #a7f3d0;
  color: #047857;
  font-size: 0.8rem;
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

.keys-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.82rem;
}

.keys-table th,
.keys-table td {
  padding: 8px 10px;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
}

.keys-table th {
  color: var(--color-text-light);
  font-weight: 500;
  font-size: 0.75rem;
}

.keys-table code {
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

.metrics-controls {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 16px;
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

.control-select {
  min-width: 220px;
  padding: 8px 10px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: var(--color-surface);
  font-size: 0.875rem;
  color: var(--color-text);
}

.range-group,
.key-group {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.key-group {
  margin-bottom: 12px;
}

.range-button {
  padding: 7px 12px;
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

.granularity-note {
  margin: 0 0 12px;
  font-size: 0.8rem;
  color: var(--color-text-light);
}

.granularity-note strong {
  color: var(--color-text);
}

.chart-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 12px;
}

.error-banner {
  margin: 0 0 12px;
  padding: 10px 14px;
  border-radius: 8px;
  background: #fef2f2;
  border: 1px solid #fecaca;
  color: var(--color-danger);
  font-size: 0.85rem;
}

.empty-state {
  padding: 48px 16px;
  text-align: center;
  color: var(--color-text-light);
  font-size: 0.9rem;
  background: var(--color-surface);
  border: 1px dashed var(--color-border);
  border-radius: 10px;
}

.ghost-button {
  padding: 8px 14px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.8rem;
  cursor: pointer;
  white-space: nowrap;
}

.ghost-button:hover {
  border-color: var(--color-primary);
  color: var(--color-primary);
}

.primary-button {
  padding: 8px 16px;
  border: none;
  border-radius: 8px;
  background: var(--color-primary, #2563eb);
  color: #fff;
  font-size: 0.875rem;
  cursor: pointer;
}

.primary-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

@media (max-width: 768px) {
  .chart-grid {
    grid-template-columns: 1fr;
  }

  .control-select {
    min-width: 0;
    width: 100%;
  }

  .metrics-controls {
    gap: 10px;
  }

  .metrics-header {
    flex-direction: column;
  }
}
</style>
