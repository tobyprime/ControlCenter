<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { fetchMetricsSeries, type Granularity, type MetricsSeries, type SeriesPoint } from '@/api/metrics'
import { listDevices, type Device } from '@/api/devices'
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

const devices = ref<Device[]>([])
const selectedDeviceId = ref<number | null>(null)
const selectedRange = ref<RangeOption>(rangeOptions[0])
const series = ref<MetricsSeries | null>(null)
const loading = ref(true)
const errorMessage = ref('')
let refreshTimer: number | undefined

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

function formatBytesPerSec(value: number): string {
  if (value >= 1024 * 1024) {
    return `${(value / 1024 / 1024).toFixed(1)} MB/s`
  }
  if (value >= 1024) {
    return `${(value / 1024).toFixed(1)} KB/s`
  }
  return `${value.toFixed(0)} B/s`
}

const cpuSeries = computed<ChartSeries[]>(() => buildSeries((p) => p.cpu, '#2563eb', 'CPU'))
const memSeries = computed<ChartSeries[]>(() => buildSeries((p) => p.mem, '#16a34a', '内存'))
const diskSeries = computed<ChartSeries[]>(() => buildSeries((p) => p.disk, '#d97706', '磁盘'))
const netSeries = computed<ChartSeries[]>(() => [
  {
    name: '下行',
    color: '#2563eb',
    data: (series.value?.points ?? []).map((p) => ({ x: Date.parse(p.t), y: p.netRx })),
  },
  {
    name: '上行',
    color: '#9333ea',
    data: (series.value?.points ?? []).map((p) => ({ x: Date.parse(p.t), y: p.netTx })),
  },
])

function buildSeries(pick: (p: SeriesPoint) => number, color: string, name: string): ChartSeries[] {
  return [
    {
      name,
      color,
      data: (series.value?.points ?? []).map((p) => ({ x: Date.parse(p.t), y: pick(p) })),
    },
  ]
}

async function refresh(showError = true) {
  const deviceId = selectedDeviceId.value
  if (deviceId === null) {
    return
  }

  const to = new Date()
  const from = new Date(to.getTime() - selectedRange.value.hours * 3600 * 1000)
  try {
    series.value = await fetchMetricsSeries(deviceId, from.toISOString(), to.toISOString())
    errorMessage.value = ''
  } catch (e) {
    if (showError) {
      errorMessage.value = e instanceof Error ? e.message : '指标数据加载失败'
    }
  } finally {
    loading.value = false
  }
}

async function loadDevices() {
  try {
    devices.value = await listDevices()
    if (devices.value.length > 0 && selectedDeviceId.value === null) {
      selectedDeviceId.value = devices.value[0].id
      await refresh()
    } else if (selectedDeviceId.value !== null && !devices.value.some((d) => d.id === selectedDeviceId.value)) {
      selectedDeviceId.value = devices.value[0]?.id ?? null
      await refresh()
    } else {
      loading.value = false
    }
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '设备列表加载失败'
    loading.value = false
  }
}

function onSelectDevice() {
  loading.value = true
  void refresh()
}

function onSelectRange() {
  loading.value = true
  void refresh()
}

onMounted(() => {
  void loadDevices()
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
        <p class="metrics-description">设备每 30 秒采集上报一次；长跨度自动切换小时/天聚合，数据保留约 30 天。</p>
      </div>
    </div>

    <div class="metrics-controls">
      <label class="control-field">
        <span class="control-label">设备</span>
        <select v-model.number="selectedDeviceId" class="control-select" @change="onSelectDevice">
          <option v-for="device in devices" :key="device.id" :value="device.id">{{ device.name }}</option>
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

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>

    <div v-if="loading" class="empty-state">加载中…</div>
    <div v-else-if="devices.length === 0" class="empty-state">
      还没有设备。先到「设备管理」登记设备并接入 agent，指标数据会自动上报。
    </div>
    <template v-else>
      <p class="granularity-note">
        当前口径：<strong>{{ granularityText || '—' }}</strong>
        <span v-if="series && series.points.length === 0">　所选范围内暂无数据，试试更长的时间范围。</span>
      </p>
      <div class="chart-grid">
        <MetricChart title="CPU 使用率" unit="百分比" :series="cpuSeries" :y-max="100" />
        <MetricChart title="内存使用率" unit="百分比" :series="memSeries" :y-max="100" />
        <MetricChart title="磁盘使用率" unit="百分比（根文件系统）" :series="diskSeries" :y-max="100" />
        <MetricChart title="网络流量" unit="速率" :series="netSeries" :format-value="formatBytesPerSec" />
      </div>
    </template>
  </section>
</template>

<style scoped>
.metrics-header {
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

.range-group {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
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
}
</style>
