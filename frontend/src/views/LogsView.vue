<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { listTargets as listDevices, type Target as Device } from '@/api/targets'
import { fetchLogTail, listLogServices, type LogKind, type LogLineInfo, type LogServiceInfo } from '@/api/logs'

const devices = ref<Device[]>([])
const services = ref<LogServiceInfo[]>([])
const lines = ref<LogLineInfo[]>([])

const selectedDeviceId = ref<number | null>(null)
const selectedService = ref('')
const selectedLines = ref(200)
const keyword = ref('')
const selectedLevel = ref('all')
const levelOptions = [
  { value: 'all', label: '全部级别' },
  { value: 'info', label: 'info' },
  { value: 'warn', label: 'warn' },
  { value: 'error', label: 'error' },
  { value: 'debug', label: 'debug' },
]
const linesOptions = [100, 200, 500, 1000]

const loadingDevices = ref(true)
const loadingServices = ref(false)
const loadingTail = ref(false)
const errorMessage = ref('')
const tailError = ref('')
let fetchToken = 0

// 服务条目以 kind+name 唯一确定：systemd unit 与 docker 容器跨来源同名时（容器名允许含 "."）
// 仅按 name 定位会静默命中另一来源的条目；kind 取值限定 systemd/docker，不含分隔符 '/'
function serviceKey(service: LogServiceInfo): string {
  return `${service.kind}/${service.name}`
}

const selectedServiceInfo = computed<LogServiceInfo | null>(
  () => services.value.find((s) => serviceKey(s) === selectedService.value) ?? null,
)

function serviceLabel(service: LogServiceInfo): string {
  const source = service.kind === 'systemd' ? 'systemd' : 'docker'
  return service.description ? `${service.name}（${source} · ${service.description}）` : `${service.name}（${source}）`
}

const filteredLines = computed<LogLineInfo[]>(() => {
  const text = keyword.value.trim().toLowerCase()
  return lines.value.filter((line) => {
    if (selectedLevel.value !== 'all' && line.level !== selectedLevel.value) {
      return false
    }
    if (text && !line.message.toLowerCase().includes(text)) {
      return false
    }
    return true
  })
})

const levelCounts = computed<Record<string, number>>(() => {
  const counts: Record<string, number> = { info: 0, warn: 0, error: 0, debug: 0 }
  for (const line of lines.value) {
    if (line.level in counts) {
      counts[line.level] += 1
    }
  }
  return counts
})

function formatTs(ts: string): string {
  if (!ts) {
    return '—'
  }
  const parsed = Date.parse(ts)
  if (Number.isNaN(parsed)) {
    return ts
  }
  const date = new Date(parsed)
  const pad = (n: number, width = 2) => String(n).padStart(width, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

async function loadServices(): Promise<void> {
  const deviceId = selectedDeviceId.value
  if (deviceId === null) {
    return
  }
  loadingServices.value = true
  tailError.value = ''
  services.value = []
  selectedService.value = ''
  lines.value = []
  try {
    services.value = await listLogServices(deviceId)
    if (services.value.length > 0) {
      await selectService(serviceKey(services.value[0]))
    }
  } catch (e) {
    tailError.value = e instanceof Error ? e.message : '服务清单获取失败'
  } finally {
    loadingServices.value = false
  }
}

async function selectService(key: string): Promise<void> {
  selectedService.value = key
  await refresh()
}

async function refresh(): Promise<void> {
  const deviceId = selectedDeviceId.value
  const service = selectedServiceInfo.value
  if (deviceId === null || service === null) {
    return
  }
  const token = ++fetchToken
  loadingTail.value = true
  tailError.value = ''
  try {
    const result = await fetchLogTail(deviceId, service.name, service.kind as LogKind, selectedLines.value)
    if (token !== fetchToken) {
      return // 已有更新的查询发出，丢弃过期结果
    }
    lines.value = result
  } catch (e) {
    if (token !== fetchToken) {
      return
    }
    tailError.value = e instanceof Error ? e.message : '日志拉取失败'
  } finally {
    if (token === fetchToken) {
      loadingTail.value = false
    }
  }
}

onMounted(async () => {
  try {
    // 日志经设备上的 agent 拉取：下拉只列 device 型目标，服务目标（HTTP 探针）无 agent 无日志
    devices.value = (await listDevices()).filter((device) => device.type === 'device')
    if (devices.value.length > 0) {
      selectedDeviceId.value = devices.value[0].id
      await loadServices()
    }
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '设备列表加载失败'
  } finally {
    loadingDevices.value = false
  }
})

onBeforeUnmount(() => {
  fetchToken += 1 // 使在途查询失效
})
</script>

<template>
  <section class="logs">
    <div class="logs-header">
      <div>
        <h1 class="logs-title">日志查看</h1>
        <p class="logs-description">
          按设备与服务查看目标机最近日志（尾部 {{ selectedLines }} 行，只读按需拉取）；支持关键字与级别过滤。
        </p>
      </div>
    </div>

    <div class="logs-controls">
      <label class="control-field">
        <span class="control-label">设备</span>
        <select v-model.number="selectedDeviceId" class="control-select" :disabled="loadingDevices" @change="loadServices">
          <option v-for="device in devices" :key="device.id" :value="device.id">
            {{ device.name }}{{ device.online ? '' : '（离线）' }}
          </option>
        </select>
      </label>

      <label class="control-field control-service">
        <span class="control-label">服务</span>
        <select
          v-model="selectedService"
          class="control-select"
          :disabled="loadingServices || services.length === 0"
          @change="refresh"
        >
          <option v-if="loadingServices" value="">服务清单加载中…</option>
          <option v-else-if="services.length === 0" value="">（无可查看的服务）</option>
          <option v-for="service in services" :key="serviceKey(service)" :value="serviceKey(service)">
            {{ serviceLabel(service) }}
          </option>
        </select>
      </label>

      <label class="control-field">
        <span class="control-label">行数 N</span>
        <select v-model.number="selectedLines" class="control-select control-lines" @change="refresh">
          <option v-for="n in linesOptions" :key="n" :value="n">{{ n }}</option>
        </select>
      </label>

      <button type="button" class="refresh-button" :disabled="loadingTail || !selectedService" @click="refresh">
        {{ loadingTail ? '拉取中…' : '刷新' }}
      </button>
    </div>

    <div class="logs-filters">
      <label class="control-field control-keyword">
        <span class="control-label">关键字过滤</span>
        <input v-model="keyword" type="search" class="control-input" placeholder="输入关键字（对已拉取行过滤）" />
      </label>

      <div class="control-field">
        <span class="control-label">级别过滤</span>
        <div class="level-group" role="group" aria-label="日志级别">
          <button
            v-for="option in levelOptions"
            :key="option.value"
            type="button"
            class="level-button"
            :class="[option.value !== 'all' ? `level-${option.value}` : '', { active: selectedLevel === option.value }]"
            @click="selectedLevel = option.value"
          >
            {{ option.label }}
            <span v-if="option.value !== 'all'" class="level-count">{{ levelCounts[option.value] }}</span>
          </button>
        </div>
      </div>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>
    <p v-if="tailError" class="error-banner">{{ tailError }}</p>

    <div v-if="loadingDevices" class="empty-state">加载中…</div>
    <div v-else-if="devices.length === 0" class="empty-state">
      还没有设备。先到「设备管理」登记设备并接入 agent，再回到这里查看日志。
    </div>
    <template v-else>
      <p class="tail-note">
        已拉取 <strong>{{ lines.length }}</strong> 行，过滤后显示 <strong>{{ filteredLines.length }}</strong> 行
        <span v-if="loadingTail">　拉取中…</span>
      </p>
      <div class="log-view" role="log">
        <div v-if="filteredLines.length === 0" class="empty-state empty-lines">
          {{ lines.length === 0 ? '暂无日志（该服务可能还没有输出）。' : '没有匹配过滤条件的日志行。' }}
        </div>
        <div v-for="(line, index) in filteredLines" :key="index" class="log-line">
          <span class="log-ts">{{ formatTs(line.ts) }}</span>
          <span class="log-level" :class="`log-level-${line.level}`">{{ line.level || 'info' }}</span>
          <span class="log-message">{{ line.message }}</span>
        </div>
      </div>
    </template>
  </section>
</template>

<style scoped>
.logs-header {
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
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 16px;
  margin-bottom: 12px;
}

.logs-filters {
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

.control-select,
.control-input {
  min-width: 200px;
  padding: 8px 10px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: var(--color-surface);
  font-size: 0.875rem;
  color: var(--color-text);
}

.control-service .control-select {
  min-width: 320px;
}

.control-lines {
  min-width: 90px;
}

.control-keyword .control-input {
  min-width: 260px;
}

.refresh-button {
  padding: 8px 18px;
  border: 1px solid var(--color-primary);
  border-radius: 8px;
  background: var(--color-primary);
  color: #fff;
  font-size: 0.875rem;
  cursor: pointer;
}

.refresh-button:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.level-group {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.level-button {
  padding: 7px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.8rem;
  cursor: pointer;
}

.level-button.active {
  border-color: var(--color-primary);
  color: var(--color-primary);
  background: #eff6ff;
  font-weight: 600;
}

.level-count {
  margin-left: 4px;
  padding: 0 5px;
  border-radius: 999px;
  background: rgba(100, 116, 139, 0.14);
  font-size: 0.72rem;
}

.tail-note {
  margin: 0 0 8px;
  font-size: 0.8rem;
  color: var(--color-text-light);
}

.tail-note strong {
  color: var(--color-text);
}

.log-view {
  max-height: 60vh;
  overflow: auto;
  padding: 10px 12px;
  background: #0f172a;
  border: 1px solid var(--color-border);
  border-radius: 10px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.78rem;
  line-height: 1.55;
}

.log-line {
  display: flex;
  gap: 10px;
  align-items: baseline;
  color: #dbe4f0;
  word-break: break-all;
}

.log-line:hover {
  background: rgba(148, 163, 184, 0.08);
}

.log-ts {
  flex-shrink: 0;
  color: #7d8ca3;
}

.log-level {
  flex-shrink: 0;
  min-width: 42px;
  text-align: center;
  border-radius: 5px;
  padding: 0 4px;
  font-size: 0.72rem;
}

.log-level-error {
  background: rgba(239, 68, 68, 0.22);
  color: #fca5a5;
}

.log-level-warn {
  background: rgba(217, 119, 6, 0.22);
  color: #fcd34d;
}

.log-level-debug {
  background: rgba(147, 51, 234, 0.2);
  color: #d8b4fe;
}

.log-level-info {
  background: rgba(59, 130, 246, 0.18);
  color: #93c5fd;
}

.log-message {
  white-space: pre-wrap;
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

.empty-lines {
  padding: 24px 16px;
  background: transparent;
  border: none;
}

@media (max-width: 768px) {
  .control-select,
  .control-service .control-select,
  .control-keyword .control-input {
    min-width: 0;
    width: 100%;
  }

  .logs-controls,
  .logs-filters {
    gap: 10px;
  }
}
</style>
