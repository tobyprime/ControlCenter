<script setup lang="ts">
import { computed } from 'vue'
import type { MetricOverviewItem } from '@/api/metrics'
import { formatLatestTime, formatMetricValue, metricCardStateText, type MetricCardDegradedReason } from '@/dashboard/cardConfig'

// TOB-368 数值卡：number 指标最新值 + 单位
const props = defineProps<{
  label: string
  item: MetricOverviewItem | null
  degradedReason: MetricCardDegradedReason
  loading: boolean
}>()

const stateText = computed(() =>
  metricCardStateText({
    degradedReason: props.degradedReason,
    loading: props.loading,
    hasData: props.item?.latestValueNum !== null && props.item?.latestValueNum !== undefined,
  }),
)

const valueText = computed(() =>
  props.item?.latestValueNum === null || props.item?.latestValueNum === undefined
    ? ''
    : formatMetricValue(props.item.latestValueNum, props.item.unit),
)
</script>

<template>
  <span class="overview-label">{{ label }}</span>
  <span v-if="stateText" class="card-state">{{ stateText }}</span>
  <template v-else>
    <span class="overview-value">{{ valueText }}</span>
    <span class="overview-hint">更新于 {{ formatLatestTime(item?.latestTimeUtc) }}</span>
  </template>
</template>
