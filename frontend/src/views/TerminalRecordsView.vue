<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { listTerminalRecords, listTerminalSessions, type TerminalRecordInfo, type TerminalSessionInfo } from '@/api/terminal'
import { listTargets as listDevices, type Target as Device } from '@/api/targets'

const devices = ref<Device[]>([])
const selectedDeviceId = ref<number | 'all'>('all')
const rangeDays = ref(7)
const sessions = ref<TerminalSessionInfo[]>([])
const records = ref<TerminalRecordInfo[]>([])
const viewingSession = ref<TerminalSessionInfo | null>(null)
const loading = ref(false)
const recordsLoading = ref(false)
const errorMessage = ref('')

const rangeOptions = [
  { label: '最近 24 小时', days: 1 },
  { label: '最近 7 天', days: 7 },
  { label: '最近 30 天', days: 30 },
  { label: '全部', days: 0 },
]

const closeReasonLabels: Record<string, string> = {
  operator: '操作者关闭',
  'agent-exit': 'shell 退出',
  'connection-lost': '设备离线',
  error: '异常结束',
}

async function refresh(showError = true) {
  loading.value = true
  try {
    const fromIso = rangeDays.value > 0
      ? new Date(Date.now() - rangeDays.value * 24 * 3600 * 1000).toISOString()
      : undefined
    sessions.value = await listTerminalSessions(
      selectedDeviceId.value === 'all' ? undefined : selectedDeviceId.value,
      fromIso,
    )
    errorMessage.value = ''
  } catch (e) {
    if (showError) {
      errorMessage.value = e instanceof Error ? e.message : '留痕加载失败'
    }
  } finally {
    loading.value = false
  }
}

async function viewRecords(session: TerminalSessionInfo) {
  viewingSession.value = session
  recordsLoading.value = true
  records.value = []
  try {
    records.value = await listTerminalRecords(session.id)
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '记录加载失败'
  } finally {
    recordsLoading.value = false
  }
}

function formatTime(utc?: string | null): string {
  if (!utc) return '—'
  return new Date(utc).toLocaleString('zh-CN', { hour12: false })
}

function describeSession(session: TerminalSessionInfo): string {
  const reason = session.closeReason ? closeReasonLabels[session.closeReason] ?? session.closeReason : null
  return session.closedAtUtc
    ? `${formatTime(session.openedAtUtc)} → ${formatTime(session.closedAtUtc)}（${reason ?? '已关闭'}）`
    : `${formatTime(session.openedAtUtc)} 起 · 进行中`
}

onMounted(async () => {
  try {
    devices.value = await listDevices()
  } catch {
    // 设备加载失败不阻塞留痕查询
  }
  await refresh()
})
</script>

<template>
  <section class="records">
    <div class="records-header">
      <div>
        <h1 class="records-title">终端留痕</h1>
        <p class="records-description">回答「何时在哪台设备执行过什么」：会话元数据 + 命令与输出留档。注意：键盘输入逐字入档，口令类输入也会以明文留痕，请留意敏感操作。</p>
      </div>
      <div class="records-controls">
        <select v-model.number="selectedDeviceId" class="control-select">
          <option value="all">全部设备</option>
          <option v-for="device in devices" :key="device.id" :value="device.id">{{ device.name }}</option>
        </select>
        <select v-model.number="rangeDays" class="control-select">
          <option v-for="option in rangeOptions" :key="option.days" :value="option.days">{{ option.label }}</option>
        </select>
        <button type="button" class="ghost-button" @click="refresh()">刷新</button>
      </div>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>

    <div class="records-body">
      <div class="session-list">
        <div v-if="loading" class="empty-state">加载中…</div>
        <div v-else-if="sessions.length === 0" class="empty-state">该范围内没有终端会话留痕。</div>
        <button
          v-for="session in sessions"
          :key="session.id"
          type="button"
          class="session-item"
          :class="{ active: viewingSession?.id === session.id }"
          @click="viewRecords(session)"
        >
          <span class="session-device">{{ session.deviceName }}</span>
          <span class="session-meta">{{ describeSession(session) }}</span>
          <span class="session-meta">操作者：{{ session.operator || '—' }}</span>
        </button>
      </div>

      <div class="record-detail">
        <div v-if="!viewingSession" class="empty-state">选择左侧会话查看命令与输出留档。</div>
        <div v-else-if="recordsLoading" class="empty-state">加载中…</div>
        <div v-else-if="records.length === 0" class="empty-state">该会话没有留痕记录。</div>
        <div v-else class="record-list">
          <div v-for="record in records" :key="record.id" class="record-line" :class="record.direction">
            <span class="record-direction">{{ record.direction === 'input' ? '输入' : '输出' }}</span>
            <span class="record-time">{{ formatTime(record.recordedAtUtc) }}</span>
            <pre class="record-data">{{ record.data }}</pre>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.records-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.records-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.records-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.records-controls {
  display: flex;
  align-items: center;
  gap: 10px;
}

.control-select {
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

.records-body {
  display: grid;
  grid-template-columns: minmax(260px, 360px) 1fr;
  gap: 12px;
}

.session-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.session-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px;
  text-align: left;
  border: 1px solid var(--color-border);
  border-radius: 10px;
  background: var(--color-surface);
  cursor: pointer;
  font-size: 0.85rem;
}

.session-item:hover,
.session-item.active {
  border-color: var(--color-primary, #2563eb);
}

.session-device {
  font-weight: 600;
}

.session-meta {
  color: var(--color-text-light);
  font-size: 0.78rem;
}

.record-detail {
  min-height: 240px;
}

.record-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 68vh;
  overflow: auto;
  padding: 10px;
  border: 1px solid var(--color-border);
  border-radius: 10px;
  background: var(--color-surface);
}

.record-line {
  display: grid;
  grid-template-columns: 44px auto 1fr;
  gap: 8px;
  align-items: baseline;
  font-size: 0.8rem;
}

.record-direction {
  font-size: 0.72rem;
  color: var(--color-text-light);
  white-space: nowrap;
}

.record-line.input .record-direction {
  color: var(--color-primary, #2563eb);
  font-weight: 600;
}

.record-time {
  color: var(--color-text-light);
  font-size: 0.72rem;
  white-space: nowrap;
}

.record-data {
  margin: 0;
  font-family: Menlo, Monaco, Consolas, 'Courier New', monospace;
  font-size: 0.78rem;
  white-space: pre-wrap;
  word-break: break-all;
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
  .records-body {
    grid-template-columns: 1fr;
  }

  .records-header {
    flex-direction: column;
  }
}
</style>
