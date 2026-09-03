<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { listDevices, type Device } from '@/api/devices'
import {
  deleteDeviceThreshold,
  fetchAlertQueue,
  fetchAlertSettings,
  fetchAlertThresholds,
  metricLabel,
  METRIC_OPTIONS,
  saveAlertSettings,
  saveDeviceThreshold,
  saveGlobalThreshold,
  type AlertQueue,
  type AlertThresholds,
  type NapcatSettings,
} from '@/api/alerts'

const napcat = ref<NapcatSettings | null>(null)
const napcatBaseUrl = ref('')
const napcatToken = ref('')
const napcatTargetType = ref('private')
const napcatTargetId = ref('')
const savingNapcat = ref(false)
const napcatMessage = ref('')
const napcatError = ref('')

const globalThresholds = ref<Record<string, number>>({})
const overrides = ref<AlertThresholds['overrides']>([])
const savingGlobal = ref(false)
const globalMessage = ref('')
const thresholdError = ref('')
const overrideDeviceId = ref<number | null>(null)
const overrideMetric = ref('cpu')
const overrideValue = ref<number | null>(null)
const overrideError = ref('')

const devices = ref<Device[]>([])
const queue = ref<AlertQueue | null>(null)
const queueError = ref('')
const loadingQueue = ref(false)

function requireNumber(value: number | null): number | null {
  if (value === null || Number.isNaN(value)) {
    return null
  }
  return value
}

async function loadSettings(): Promise<void> {
  napcat.value = (await fetchAlertSettings()).napcat
  napcatBaseUrl.value = napcat.value.baseUrl ?? ''
  napcatTargetType.value = napcat.value.targetType || 'private'
  napcatTargetId.value = napcat.value.targetId ?? ''
  napcatToken.value = ''
}

async function loadThresholds(): Promise<void> {
  const payload = await fetchAlertThresholds()
  globalThresholds.value = payload.global
  overrides.value = payload.overrides
}

async function loadQueue(): Promise<void> {
  loadingQueue.value = true
  queueError.value = ''
  try {
    queue.value = await fetchAlertQueue()
  } catch (e) {
    queueError.value = e instanceof Error ? e.message : '待发队列获取失败'
  } finally {
    loadingQueue.value = false
  }
}

async function onSaveNapcat(): Promise<void> {
  savingNapcat.value = true
  napcatMessage.value = ''
  napcatError.value = ''
  try {
    const input: Record<string, string> = { baseUrl: napcatBaseUrl.value }
    if (napcatToken.value) {
      input.token = napcatToken.value
    }
    if (napcatTargetId.value) {
      input.targetType = napcatTargetType.value
      input.targetId = napcatTargetId.value
    }
    await saveAlertSettings(input)
    await loadSettings()
    napcatMessage.value = '已保存，napcat 配置即时生效'
  } catch (e) {
    napcatError.value = e instanceof Error ? e.message : '保存失败'
  } finally {
    savingNapcat.value = false
  }
}

async function onSaveGlobal(metric: string): Promise<void> {
  const value = requireNumber(globalThresholds.value[metric] ?? null)
  if (value === null) {
    return
  }
  savingGlobal.value = true
  globalMessage.value = ''
  thresholdError.value = ''
  try {
    await saveGlobalThreshold(metric, value)
    await loadThresholds()
    globalMessage.value = `已保存：${metricLabel(metric)} 全局阈值 ${value}%`
  } catch (e) {
    thresholdError.value = e instanceof Error ? e.message : '保存失败'
  } finally {
    savingGlobal.value = false
  }
}

async function onAddOverride(): Promise<void> {
  const value = requireNumber(overrideValue.value)
  if (overrideDeviceId.value === null || value === null) {
    overrideError.value = '请选择设备并填写阈值'
    return
  }
  overrideError.value = ''
  try {
    await saveDeviceThreshold(overrideDeviceId.value, overrideMetric.value, value)
    overrideValue.value = null
    await loadThresholds()
  } catch (e) {
    overrideError.value = e instanceof Error ? e.message : '保存失败'
  }
}

async function onDeleteOverride(deviceId: number, metric: string): Promise<void> {
  try {
    await deleteDeviceThreshold(deviceId, metric)
    await loadThresholds()
  } catch (e) {
    overrideError.value = e instanceof Error ? e.message : '删除失败'
  }
}

function deviceName(deviceId: number): string {
  return devices.value.find((device) => device.id === deviceId)?.name ?? `设备 ${deviceId}`
}

onMounted(async () => {
  try {
    await Promise.all([loadSettings(), loadThresholds(), loadQueue()])
    devices.value = await listDevices()
  } catch (e) {
    thresholdError.value = e instanceof Error ? e.message : '告警配置加载失败'
  }
})
</script>

<template>
  <section class="alerts">
    <div class="alerts-header">
      <h1 class="alerts-title">告警配置</h1>
      <p class="alerts-description">
        设备离线与指标越限告警经 napcat（OneBot v11 HTTP）分发到 QQ；napcat 不可用时告警进入本地待发队列，恢复后自动补发、无丢失。
      </p>
    </div>

    <section class="card">
      <h2 class="card-title">消息渠道（QQ · napcat）</h2>
      <div class="form-grid">
        <label class="control-field">
          <span class="control-label">OneBot HTTP 地址</span>
          <input v-model="napcatBaseUrl" type="url" class="control-input" placeholder="如 http://127.0.0.1:3000" />
        </label>
        <label class="control-field">
          <span class="control-label">access token{{ napcat?.tokenSet ? '（已设置，留空保持不变）' : '' }}</span>
          <input v-model="napcatToken" type="password" class="control-input" placeholder="napcat access_token" />
        </label>
        <label class="control-field">
          <span class="control-label">通知目标类型</span>
          <select v-model="napcatTargetType" class="control-select">
            <option value="private">私聊（QQ 号）</option>
            <option value="group">群聊（群号）</option>
          </select>
        </label>
        <label class="control-field">
          <span class="control-label">通知目标 ID</span>
          <input v-model="napcatTargetId" type="text" class="control-input" placeholder="QQ 号或群号（数字）" />
        </label>
      </div>
      <div class="card-actions">
        <button type="button" class="primary-button" :disabled="savingNapcat" @click="onSaveNapcat">
          {{ savingNapcat ? '保存中…' : '保存渠道配置' }}
        </button>
        <span v-if="napcatMessage" class="ok-note">{{ napcatMessage }}</span>
        <span v-if="napcatError" class="error-note">{{ napcatError }}</span>
      </div>
    </section>

    <section class="card">
      <h2 class="card-title">全局默认阈值（%）</h2>
      <div class="form-grid">
        <label v-for="option in METRIC_OPTIONS" :key="option.value" class="control-field">
          <span class="control-label">{{ option.label }}</span>
          <input
            v-model.number="globalThresholds[option.value]"
            type="number"
            min="1"
            max="100"
            step="0.5"
            class="control-input control-number"
          />
        </label>
      </div>
      <div class="card-actions">
        <div class="inline-buttons">
          <button
            v-for="option in METRIC_OPTIONS"
            :key="option.value"
            type="button"
            class="primary-button"
            :disabled="savingGlobal"
            @click="onSaveGlobal(option.value)"
          >
            保存{{ option.label }}
          </button>
        </div>
        <span v-if="globalMessage" class="ok-note">{{ globalMessage }}</span>
        <span v-if="thresholdError" class="error-note">{{ thresholdError }}</span>
      </div>
      <p class="card-note">持续越限超过 60 秒才告警；同一越限事件恢复前只发送一次（防刷屏）。</p>
    </section>

    <section class="card">
      <h2 class="card-title">按设备覆盖阈值</h2>
      <div class="form-grid">
        <label class="control-field">
          <span class="control-label">设备</span>
          <select v-model.number="overrideDeviceId" class="control-select">
            <option :value="null" disabled>选择设备</option>
            <option v-for="device in devices" :key="device.id" :value="device.id">{{ device.name }}</option>
          </select>
        </label>
        <label class="control-field">
          <span class="control-label">指标</span>
          <select v-model="overrideMetric" class="control-select">
            <option v-for="option in METRIC_OPTIONS" :key="option.value" :value="option.value">{{ option.label }}</option>
          </select>
        </label>
        <label class="control-field">
          <span class="control-label">阈值（%）</span>
          <input v-model.number="overrideValue" type="number" min="1" max="100" step="0.5" class="control-input control-number" />
        </label>
        <div class="control-field">
          <span class="control-label">&nbsp;</span>
          <button type="button" class="primary-button" @click="onAddOverride">添加 / 更新覆盖</button>
        </div>
      </div>
      <span v-if="overrideError" class="error-note">{{ overrideError }}</span>
      <table v-if="overrides.length > 0" class="override-table">
        <thead>
          <tr>
            <th>设备</th>
            <th>指标</th>
            <th>覆盖阈值</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="entry in overrides" :key="`${entry.deviceId}-${entry.metric}`">
            <td>{{ entry.deviceName }}</td>
            <td>{{ metricLabel(entry.metric) }}</td>
            <td>{{ entry.value }}%</td>
            <td>
              <button type="button" class="link-button" @click="onDeleteOverride(entry.deviceId, entry.metric)">删除</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="card-note">暂无覆盖：所有设备按全局默认阈值告警。</p>
    </section>

    <section class="card">
      <h2 class="card-title">
        待发队列
        <span class="queue-count">{{ queue?.count ?? 0 }} 条</span>
        <button type="button" class="link-button" :disabled="loadingQueue" @click="loadQueue">
          {{ loadingQueue ? '刷新中…' : '刷新' }}
        </button>
      </h2>
      <span v-if="queueError" class="error-note">{{ queueError }}</span>
      <table v-if="queue && queue.items.length > 0" class="override-table">
        <thead>
          <tr>
            <th>时间</th>
            <th>渠道</th>
            <th>内容</th>
            <th>已尝试</th>
            <th>最近错误</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in queue.items" :key="item.id">
            <td>{{ new Date(item.createdAtUtc).toLocaleString() }}</td>
            <td>{{ item.channel }}</td>
            <td class="queue-content">{{ item.title }}：{{ item.content }}</td>
            <td>{{ item.attempts }}</td>
            <td class="queue-error">{{ item.lastError ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
      <p v-else class="card-note">队列空闲：napcat 正常或暂无待补发的告警。</p>
    </section>
  </section>
</template>

<style scoped>
.alerts-header {
  margin-bottom: 16px;
}

.alerts-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.alerts-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.card {
  margin-bottom: 16px;
  padding: 16px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
}

.card-title {
  margin: 0 0 12px;
  font-size: 1rem;
  display: flex;
  align-items: center;
  gap: 10px;
}

.form-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
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

.control-input,
.control-select {
  min-width: 220px;
  padding: 8px 10px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: var(--color-surface);
  font-size: 0.875rem;
  color: var(--color-text);
}

.control-number {
  min-width: 120px;
}

.primary-button {
  padding: 8px 16px;
  border: 1px solid var(--color-primary);
  border-radius: 8px;
  background: var(--color-primary);
  color: #fff;
  font-size: 0.875rem;
  cursor: pointer;
}

.primary-button:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.inline-buttons {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.card-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
}

.card-note {
  margin: 8px 0 0;
  font-size: 0.78rem;
  color: var(--color-text-light);
}

.ok-note {
  font-size: 0.8rem;
  color: #16a34a;
}

.error-note {
  font-size: 0.8rem;
  color: var(--color-danger);
}

.override-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.override-table th,
.override-table td {
  padding: 8px 10px;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
}

.override-table th {
  color: var(--color-text-light);
  font-weight: 500;
  font-size: 0.78rem;
}

.queue-count {
  padding: 2px 10px;
  border-radius: 999px;
  background: rgba(100, 116, 139, 0.14);
  font-size: 0.75rem;
  font-weight: 400;
}

.queue-content {
  max-width: 420px;
  word-break: break-all;
}

.queue-error {
  color: var(--color-danger);
  max-width: 240px;
  word-break: break-all;
}

.link-button {
  padding: 0;
  border: none;
  background: transparent;
  color: var(--color-primary);
  font-size: 0.8rem;
  cursor: pointer;
}

.link-button:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

@media (max-width: 768px) {
  .control-input,
  .control-select {
    min-width: 0;
    width: 100%;
  }

  .form-grid {
    gap: 10px;
  }
}
</style>
