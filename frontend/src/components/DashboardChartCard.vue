<script setup lang="ts">
import { computed } from 'vue'
import type { MetricKeyInfo, MetricSeries } from '@/api/metrics'
import MetricChart, { type ChartSeries } from '@/components/MetricChart.vue'
import { formatMetricValue, metricCardStateText, type MetricCardDegradedReason } from '@/dashboard/cardConfig'

// TOB-368 曲线卡：按所选时间窗复用指标曲线组件渲染单条序列
const props = defineProps<{
  label: string
  series: MetricSeries | null
  keyInfo: MetricKeyInfo | undefined
  degradedReason: MetricCardDegradedReason
  loading: boolean
}>()

const stateText = computed(() =>
  metricCardStateText({
    degradedReason: props.degradedReason,
    loading: props.loading,
    hasData: (props.series?.points.length ?? 0) > 0,
  }),
)

const chartSeries = computed<ChartSeries[]>(() => {
  const points = (props.series?.points ?? [])
    .filter((point) => point.v !== null)
    .map((point) => ({ x: new Date(point.t).getTime(), y: point.v as number }))
  if (points.length === 0) {
    return []
  }
  return [{ name: props.keyInfo?.displayName ?? props.label, color: '#2563eb', data: points }]
})

const formatValue = (value: number) => formatMetricValue(value, props.keyInfo?.unit ?? '')
</script>

<template>
  <span class="overview-label">{{ label }}</span>
  <span v-if="stateText" class="card-state">{{ stateText }}</span>
  <template v-else>
    <MetricChart
      :title="keyInfo?.displayName ?? label"
      :unit="keyInfo?.unit ?? ''"
      :series="chartSeries"
      :format-value="formatValue"
    />
  </template>
</template>
