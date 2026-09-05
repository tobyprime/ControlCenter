<script setup lang="ts">
import { computed } from 'vue'

export interface ChartSeries {
  name: string
  color: string
  data: { x: number; y: number }[]
}

const props = defineProps<{
  title: string
  unit: string
  series: ChartSeries[]
  /** 固定纵轴上限（如百分比用 100）；缺省时按数据自动放大 */
  yMax?: number
  /** 纵轴数值格式化（如字节速率换算单位） */
  formatValue?: (value: number) => string
  /** 卡片副标题（如内存卡片的 used/total 实际数值、温度卡片的传感器名） */
  subtitle?: string
}>()

const WIDTH = 640
const HEIGHT = 240
const PADDING = { top: 12, right: 16, bottom: 28, left: 56 }

const flatValues = computed(() => props.series.flatMap((s) => s.data.map((d) => d.y)))

const yTop = computed(() => {
  if (props.yMax !== undefined) {
    return props.yMax
  }
  const max = Math.max(0, ...flatValues.value)
  return max <= 0 ? 1 : max * 1.15
})

function formatAxisValue(value: number): string {
  if (props.formatValue) {
    return props.formatValue(value)
  }
  if (props.yMax !== undefined && props.yMax === 100) {
    return `${Math.round(value)}%`
  }
  return Math.abs(value) >= 100 ? Math.round(value).toString() : value.toFixed(1)
}

const xRange = computed(() => {
  const times = props.series.flatMap((s) => s.data.map((d) => d.x))
  if (times.length === 0) {
    return { min: 0, max: 1 }
  }
  const min = Math.min(...times)
  const max = Math.max(...times)
  return { min, max: max > min ? max : min + 1 }
})

function toX(time: number): number {
  const { min, max } = xRange.value
  const ratio = (time - min) / (max - min)
  return PADDING.left + ratio * (WIDTH - PADDING.left - PADDING.right)
}

function toY(value: number): number {
  const ratio = Math.min(Math.max(value / yTop.value, 0), 1)
  return HEIGHT - PADDING.bottom - ratio * (HEIGHT - PADDING.top - PADDING.bottom)
}

const gridLines = computed(() => {
  const count = 4
  return Array.from({ length: count + 1 }, (_, i) => {
    const value = (yTop.value / count) * i
    return { value, y: toY(value), label: formatAxisValue(value) }
  })
})

const xTicks = computed(() => {
  const { min, max } = xRange.value
  const count = Math.min(4, Math.max(2, props.series[0]?.data.length ?? 0))
  return Array.from({ length: count }, (_, i) => {
    const time = min + ((max - min) / (count - 1)) * i
    return { x: toX(time), label: formatTick(time) }
  })
})

function formatTick(time: number): string {
  const date = new Date(time)
  const pad = (n: number) => n.toString().padStart(2, '0')
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

const paths = computed(() =>
  props.series.map((s) => ({
    ...s,
    d: s.data.map((d, i) => `${i === 0 ? 'M' : 'L'}${toX(d.x).toFixed(1)},${toY(d.y).toFixed(1)}`).join(''),
    latest: s.data.length > 0 ? s.data[s.data.length - 1].y : null,
  })),
)

const hasData = computed(() => flatValues.value.length > 0)

const latestText = computed(() =>
  paths.value
    .map((p) => (p.latest === null ? '' : `${p.name} ${props.formatValue ? props.formatValue(p.latest) : formatAxisValue(p.latest)}`))
    .filter(Boolean)
    .join('　'),
)
</script>

<template>
  <div class="chart-card">
    <div class="chart-head">
      <h2 class="chart-title">{{ title }}</h2>
      <span class="chart-unit">{{ unit }}</span>
      <span v-if="subtitle" class="chart-subtitle">{{ subtitle }}</span>
    </div>
    <p v-if="!hasData" class="chart-empty">暂无数据</p>
    <svg v-else :viewBox="`0 0 ${WIDTH} ${HEIGHT}`" class="chart-svg" role="img" :aria-label="title">
      <g>
        <line
          v-for="line in gridLines"
          :key="line.y"
          :x1="PADDING.left"
          :y1="line.y"
          :x2="WIDTH - PADDING.right"
          :y2="line.y"
          class="grid-line"
        />
        <text v-for="line in gridLines" :key="`label-${line.y}`" :x="PADDING.left - 8" :y="line.y + 4" class="axis-label" text-anchor="end">
          {{ line.label }}
        </text>
      </g>
      <g>
        <text v-for="(tick, i) in xTicks" :key="`tick-${i}`" :x="tick.x" :y="HEIGHT - 8" class="axis-label" text-anchor="middle">
          {{ tick.label }}
        </text>
      </g>
      <g>
        <polyline
          v-for="path in paths"
          :key="path.name"
          :points="path.data.map((d) => `${toX(d.x).toFixed(1)},${toY(d.y).toFixed(1)}`).join(' ')"
          fill="none"
          :stroke="path.color"
          stroke-width="2"
          stroke-linejoin="round"
          stroke-linecap="round"
        />
      </g>
    </svg>
    <p v-if="hasData && latestText" class="chart-latest">最新：{{ latestText }}</p>
    <div class="chart-legend">
      <span v-for="path in paths" :key="`legend-${path.name}`" class="legend-item">
        <span class="legend-dot" :style="{ background: path.color }"></span>{{ path.name }}
      </span>
    </div>
  </div>
</template>

<style scoped>
.chart-card {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
  padding: 14px 16px 10px;
  min-width: 0;
}

.chart-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.chart-title {
  margin: 0;
  font-size: 0.95rem;
}

.chart-unit {
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.chart-subtitle {
  margin-left: auto;
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.chart-svg {
  width: 100%;
  height: auto;
  display: block;
  margin-top: 8px;
}

.grid-line {
  stroke: var(--color-border);
  stroke-width: 1;
}

.axis-label {
  fill: var(--color-text-light);
  font-size: 11px;
}

.chart-empty {
  margin: 24px 0;
  text-align: center;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.chart-latest {
  margin: 4px 0 0;
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.chart-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 4px;
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 0.75rem;
  color: var(--color-text-light);
}

.legend-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  display: inline-block;
}
</style>
