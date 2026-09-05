<script setup lang="ts">
import { computed } from 'vue'
import type { DashboardCard } from '@/api/dashboard'
import type { Target } from '@/api/targets'
import type { MetricKeyInfo, MetricValueType } from '@/api/metrics'
import {
  compatibleCardTypes,
  DEFAULT_WINDOW_HOURS,
  isMetricCardType,
  keyInfoOf,
  METRIC_CARD_TYPE_LABELS,
  WINDOW_OPTIONS,
  type MetricCardConfig,
  type MetricCardType,
} from '@/dashboard/cardConfig'

// TOB-368 指标卡配置表单：来源（目标 → 指标）、卡片类型、时间窗。
// 直接改写编辑态 draft 卡片（保存布局时才随 config 落库），只读的 props 对象在编辑模型内。
const props = defineProps<{
  card: DashboardCard
  targets: Target[]
  metricKeys: MetricKeyInfo[]
  // 按来源可用指标（TOB-374 ①）：targetId → 该来源可用指标集合，由页面预取传入
  availableByTarget: Record<number, MetricKeyInfo[]>
}>()

// 逐字段读取原始 config：配置中途（如只选了目标）也是合法的编辑态，
// 不能经过防御式解析往返，否则半配置状态会把已选字段洗掉
function rawField<K extends keyof MetricCardConfig>(field: K, fallback: MetricCardConfig[K]): MetricCardConfig[K] {
  const value = (props.card.config as Record<string, unknown>)[field]
  return typeof value === typeof fallback ? (value as MetricCardConfig[K]) : fallback
}

function updateConfig(patch: Partial<MetricCardConfig>) {
  props.card.config = {
    targetId: rawField('targetId', 0),
    key: rawField('key', ''),
    windowHours: rawField('windowHours', DEFAULT_WINDOW_HOURS),
    ...patch,
  }
}

function onTargetChange(event: Event) {
  const targetId = Number((event.target as HTMLSelectElement).value)
  if (Number.isInteger(targetId) && targetId > 0) {
    // 切换来源后原指标未必仍可用（TOB-374 ①）：清空让用户按新来源重选
    updateConfig({ targetId, key: '' })
  }
}

function onKeyChange(event: Event) {
  const key = (event.target as HTMLSelectElement).value
  updateConfig({ key })
  // 指标切换后当前卡片类型不再兼容时，自动切到首个兼容类型
  const valueType = keyInfoOf(props.metricKeys, key)?.valueType
  if (!valueType) {
    return
  }
  const compatible = compatibleCardTypes(valueType)
  if (isMetricCardType(props.card.type) && !compatible.includes(props.card.type) && compatible.length > 0) {
    props.card.type = compatible[0]
  }
}

function onWindowChange(event: Event) {
  const windowHours = Number((event.target as HTMLSelectElement).value)
  if (Number.isFinite(windowHours) && windowHours > 0) {
    updateConfig({ windowHours })
  }
}

const selectedValueType = computed<MetricValueType | undefined>(() => {
  const key = rawField('key', '')
  return key ? keyInfoOf(props.metricKeys, key)?.valueType : undefined
})

// 值类型已知时禁用不兼容的卡片类型选项
const typeOptions = computed(() => {
  const compatible = selectedValueType.value ? compatibleCardTypes(selectedValueType.value) : []
  return (Object.entries(METRIC_CARD_TYPE_LABELS) as [MetricCardType, string][]).map(([value, label]) => ({
    value,
    label,
    disabled: compatible.length > 0 && !compatible.includes(value),
  }))
})

const selectedTargetId = computed(() => rawField('targetId', 0) || '')
const selectedKey = computed(() => rawField('key', ''))
const selectedWindowHours = computed(() => rawField('windowHours', DEFAULT_WINDOW_HOURS))

// 指标下拉只列所选来源可用的指标（TOB-374 ①）；可用集合未就绪时维持全量注册表口径，
// 存量配置里的失效指标仍保留为选项，避免编辑态下拉显示空白
const metricOptions = computed(() => {
  const available = props.availableByTarget[rawField('targetId', 0)]
  if (!available || available.length === 0) {
    return props.metricKeys
  }
  const current = selectedKey.value ? keyInfoOf(props.metricKeys, selectedKey.value) : undefined
  if (current && !available.some((info) => info.key === current.key)) {
    return [current, ...available]
  }
  return available
})
</script>

<template>
  <div class="card-config">
    <label class="control-field">
      <span class="control-label">目标</span>
      <select :value="selectedTargetId" @change="onTargetChange">
        <option value="" disabled>请选择目标</option>
        <option v-for="target in targets" :key="target.id" :value="target.id">{{ target.name }}</option>
      </select>
    </label>
    <label class="control-field">
      <span class="control-label">指标</span>
      <select :value="selectedKey" @change="onKeyChange">
        <option value="" disabled>请选择指标</option>
        <option v-for="info in metricOptions" :key="info.key" :value="info.key">{{ info.displayName }}（{{ info.key }}）</option>
      </select>
    </label>
    <label class="control-field">
      <span class="control-label">卡片类型</span>
      <select v-model="card.type">
        <option v-for="option in typeOptions" :key="option.value" :value="option.value" :disabled="option.disabled">
          {{ option.label }}
        </option>
      </select>
    </label>
    <label v-if="card.type === 'metric-chart'" class="control-field">
      <span class="control-label">时间窗</span>
      <select :value="selectedWindowHours" @change="onWindowChange">
        <option v-for="option in WINDOW_OPTIONS" :key="option.hours" :value="option.hours">{{ option.label }}</option>
      </select>
    </label>
  </div>
</template>

<style scoped>
.card-config {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.control-field {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 0.8rem;
}

.control-label {
  flex: none;
  color: var(--color-text-light);
}

.control-field select {
  flex: 1;
  min-width: 0;
  padding: 5px 8px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.8rem;
}
</style>
