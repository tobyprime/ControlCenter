<script setup lang="ts">
import { computed } from 'vue'
import type { MetricOverviewItem } from '@/api/metrics'
import { formatLatestTime, metricCardStateText, type MetricCardDegradedReason } from '@/dashboard/cardConfig'

// TOB-368 状态卡：enum/bool 指标当前状态文本
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
    hasData: (props.item?.latestValueText !== null && props.item?.latestValueText !== undefined) ||
      (props.item?.latestValueNum !== null && props.item?.latestValueNum !== undefined),
  }),
)

// bool 指标无文本值时按 0/1 映射 true/false
const statusText = computed(() => {
  if (props.item === null) {
    return ''
  }
  if (props.item.latestValueText !== null && props.item.latestValueText !== undefined) {
    return props.item.latestValueText
  }
  return props.item.latestValueNum === 1 ? 'true' : 'false'
})
</script>

<template>
  <span class="overview-label">{{ label }}</span>
  <span v-if="stateText" class="card-state">{{ stateText }}</span>
  <template v-else>
    <span class="overview-value">{{ statusText }}</span>
    <span class="overview-hint">更新于 {{ formatLatestTime(item?.latestTimeUtc) }}</span>
  </template>
</template>
