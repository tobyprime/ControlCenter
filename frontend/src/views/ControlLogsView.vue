<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { listCollectors, type Collector } from '@/api/collectors'
import { controlStatusLabel, controlTypeLabel, listControlLogs, type ControlLogEntry } from '@/api/controls'

// 控制留痕（三期模块4 验收3）：回答「何时对哪台设备的哪个控制器下发了什么、结果如何」，
// 按设备/控制器/时间筛选，最新在前
const devices = ref<Collector[]>([])
const selectedDeviceId = ref<number | 'all'>('all')
const controllerKey = ref('')
const rangeDays = ref(7)
const logs = ref<ControlLogEntry[]>([])
const loading = ref(false)
const errorMessage = ref('')

const rangeOptions = [
  { label: '最近 24 小时', days: 1 },
  { label: '最近 7 天', days: 7 },
  { label: '最近 30 天', days: 30 },
  { label: '全部', days: 0 },
]

async function refresh() {
  loading.value = true
  try {
    const { logs: entries } = await listControlLogs({
      collectorId: selectedDeviceId.value === 'all' ? undefined : selectedDeviceId.value,
      controllerKey: controllerKey.value.trim() || undefined,
      fromUtc: rangeDays.value > 0 ? new Date(Date.now() - rangeDays.value * 24 * 3600 * 1000).toISOString() : undefined,
      limit: 200,
    })
    logs.value = entries
    errorMessage.value = ''
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '留痕加载失败'
  } finally {
    loading.value = false
  }
}

function deviceName(collectorId: number): string {
  return devices.value.find((device) => device.id === collectorId)?.name ?? `#${collectorId}`
}

function formatTime(utc: string): string {
  return new Date(utc).toLocaleString('zh-CN', { hour12: false })
}

function formatParams(parameters: unknown): string {
  try {
    return JSON.stringify(parameters)
  } catch {
    return '—'
  }
}

onMounted(async () => {
  try {
    devices.value = await listCollectors()
  } catch {
    // 设备清单失败不阻塞留痕查询（筛选项退化为「全部设备」）
  }
  await refresh()
})
</script>

<template>
  <section class="control-logs">
    <div class="logs-header">
      <div>
        <h1 class="logs-title">控制留痕</h1>
        <p class="logs-description">每次真实下发（含失败与超时）全量留档：何时、对哪台设备的哪个控制器、带了什么参数、结果如何。</p>
      </div>
      <div class="logs-controls">
        <select v-model.number="selectedDeviceId" class="control-select">
          <option value="all">全部设备</option>
          <option v-for="device in devices" :key="device.id" :value="device.id">{{ device.name }}</option>
        </select>
        <input
          v-model="controllerKey"
          type="text"
          class="control-input"
          placeholder="控制器 key，可留空"
          @keydown.enter="refresh()"
        />
        <select v-model.number="rangeDays" class="control-select">
          <option v-for="option in rangeOptions" :key="option.days" :value="option.days">{{ option.label }}</option>
        </select>
        <button type="button" class="ghost-button" @click="refresh()">刷新</button>
      </div>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>

    <div v-if="loading" class="empty-state">加载中…</div>
    <div v-else-if="logs.length === 0" class="empty-state">该范围内没有控制留痕。</div>
    <table v-else class="log-table">
      <thead>
        <tr>
          <th>时间</th>
          <th>设备</th>
          <th>控制器</th>
          <th>类型</th>
          <th>参数</th>
          <th>操作者</th>
          <th>状态</th>
          <th>结果</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="entry in logs" :key="entry.id">
          <td class="nowrap">{{ formatTime(entry.createdAtUtc) }}</td>
          <td>{{ deviceName(entry.collectorId) }}</td>
          <td>{{ entry.controllerLabel }}（{{ entry.controllerKey }}）</td>
          <td>{{ controlTypeLabel(entry.controllerType) }}</td>
          <td class="params">{{ formatParams(entry.parameters) }}</td>
          <td>{{ entry.operator || '—' }}</td>
          <td>
            <span class="status-badge" :class="entry.status">{{ controlStatusLabel(entry.status) }}</span>
          </td>
          <td>{{ entry.resultMessage ?? '—' }}</td>
        </tr>
      </tbody>
    </table>
  </section>
</template>

<style scoped>
.logs-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.logs-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.logs-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.logs-controls {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.control-select,
.control-input {
  padding: 8px 10px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  font-size: 0.85rem;
  background: #fff;
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

.ghost-button:hover {
  border-color: var(--color-primary, #2563eb);
  color: var(--color-primary, #2563eb);
}

.error-banner {
  margin: 0 0 12px;
  padding: 10px 12px;
  border: 1px solid #fecaca;
  border-radius: 8px;
  background: #fef2f2;
  color: var(--color-danger, #dc2626);
  font-size: 0.85rem;
}

.log-table {
  width: 100%;
  border-collapse: collapse;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
  font-size: 0.8rem;
}

.log-table th,
.log-table td {
  padding: 8px 10px;
  text-align: left;
  border-bottom: 1px solid var(--color-border);
  vertical-align: top;
}

.log-table th {
  color: var(--color-text-light);
  font-weight: 600;
  font-size: 0.75rem;
  white-space: nowrap;
}

.log-table tbody tr:last-child td {
  border-bottom: none;
}

.nowrap {
  white-space: nowrap;
}

.params {
  font-family: Menlo, Monaco, Consolas, 'Courier New', monospace;
  font-size: 0.72rem;
  word-break: break-all;
  max-width: 220px;
}

.status-badge {
  display: inline-block;
  padding: 1px 8px;
  border-radius: 4px;
  font-size: 0.72rem;
}

.status-badge.success {
  background: #ecfdf5;
  color: #047857;
}

.status-badge.failure {
  background: #fef2f2;
  color: #dc2626;
}

.status-badge.timeout {
  background: #fffbeb;
  color: #92400e;
}

.empty-state {
  padding: 48px 16px;
  border: 1px dashed var(--color-border);
  border-radius: 10px;
  text-align: center;
  color: var(--color-text-light);
  font-size: 0.9rem;
}

@media (max-width: 900px) {
  .logs-header {
    flex-direction: column;
  }
}
</style>
