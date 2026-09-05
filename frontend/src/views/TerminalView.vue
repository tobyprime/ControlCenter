<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { listCollectors as listDevices, type Collector as Device } from '@/api/collectors'
import { terminalWebSocketUrl } from '@/api/terminal'
import { listDeviceInteractionModes, type InteractionModeInfo } from '@/api/interactions'

type SessionState = 'idle' | 'connecting' | 'open' | 'closed' | 'error'

const route = useRoute()
const devices = ref<Device[]>([])
// 终端入口仅服务 device 目标（agent 回连通道）：service 目标无 shell 声明，不进可连接下拉（集成审查问题 1）
const connectableDevices = computed(() => devices.value.filter((device) => device.mode === 'push'))
const selectedDeviceId = ref<number | null>(null)
const sessionState = ref<SessionState>('idle')
const statusText = ref('选择一台在线设备，点击「打开终端」。')
const errorMessage = ref('')
const loading = ref(true)

// 交互入口按目标声明的模式渲染（约束 C）：仅目标声明 shell 时提供终端入口
const declaredModes = ref<InteractionModeInfo[]>([])
const hasShellMode = computed(() => declaredModes.value.some((mode) => mode.key === 'shell'))

const termHost = ref<HTMLElement | null>(null)
let term: Terminal | null = null
let fitAddon: FitAddon | null = null
let socket: WebSocket | null = null

function deviceName(id: number | null): string {
  return devices.value.find((d) => d.id === id)?.name ?? '未知设备'
}

async function refresh(showError = true) {
  try {
    devices.value = await listDevices()
    errorMessage.value = ''
  } catch (e) {
    if (showError) {
      errorMessage.value = e instanceof Error ? e.message : '设备列表加载失败'
    }
  } finally {
    loading.value = false
  }
}

async function loadDeclaredModes(deviceId: number | null) {
  if (deviceId === null) {
    declaredModes.value = []
    return
  }

  try {
    const modes = await listDeviceInteractionModes(deviceId)
    declaredModes.value = modes
    if (modes.length === 0) {
      if (sessionState.value === 'idle' || sessionState.value === 'closed' || sessionState.value === 'error') {
        setStatus('idle', `「${deviceName(deviceId)}」未声明可用的交互模式。`)
      }
    } else if (sessionState.value === 'idle') {
      setStatus('idle', '选择一台在线设备，点击「打开终端」。')
    }
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : '交互模式加载失败'
  }
}

watch(selectedDeviceId, (deviceId) => {
  void loadDeclaredModes(deviceId)
})

function setStatus(state: SessionState, text: string) {
  sessionState.value = state
  statusText.value = text
}

function fitTerminal() {
  if (term && fitAddon && termHost.value) {
    try {
      fitAddon.fit()
      // 尺寸变更同步到 PTY（term.resize），vim/htop 等 TUI 随窗口重排
      if (socket?.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }))
      }
    } catch {
      // 容器尚未完成布局时忽略
    }
  }
}

async function openTerminal() {
  const deviceId = selectedDeviceId.value
  if (deviceId === null || !hasShellMode.value || sessionState.value === 'connecting' || sessionState.value === 'open') {
    return
  }

  const device = devices.value.find((d) => d.id === deviceId)
  if (!device?.online) {
    errorMessage.value = '设备离线，无法打开终端'
    return
  }

  errorMessage.value = ''
  await cleanupTerminal()
  setStatus('connecting', `正在连接「${device.name}」…`)

  await nextTick()
  if (!termHost.value) {
    return
  }

  term = new Terminal({
    fontFamily: 'Menlo, Monaco, Consolas, "Courier New", monospace',
    fontSize: 13,
    cursorBlink: true,
    scrollback: 5000,
    theme: { background: '#0f172a' },
  })
  fitAddon = new FitAddon()
  term.loadAddon(fitAddon)
  term.open(termHost.value)
  fitTerminal()
  term.focus()

  const cols = term.cols
  const rows = term.rows
  socket = new WebSocket(terminalWebSocketUrl(deviceId, cols, rows))

  socket.onopen = () => {
    setStatus('connecting', `已连接面板，等待「${device.name}」响应…`)
  }

  socket.onmessage = (event) => {
    let message: { type?: string; data?: string; message?: string }
    try {
      message = JSON.parse(event.data as string)
    } catch {
      return
    }

    switch (message.type) {
      case 'opened':
        setStatus('open', `已连上「${device.name}」的 shell（${cols}×${rows}），输入 exit 或点击「关闭终端」结束。`)
        term?.focus()
        break
      case 'output':
        term?.write(message.data ?? '')
        break
      case 'closed':
        setStatus('closed', `「${device.name}」的终端会话已结束。`)
        void cleanupTerminal()
        break
      case 'error':
        setStatus('error', `终端打开失败：${message.message ?? '未知错误'}`)
        void cleanupTerminal()
        break
    }
  }

  socket.onclose = () => {
    if (sessionState.value !== 'closed' && sessionState.value !== 'error') {
      setStatus('closed', `「${device.name}」的终端会话已结束。`)
    }
    void cleanupTerminal()
  }

  socket.onerror = () => {
    if (sessionState.value === 'connecting') {
      setStatus('error', '终端连接失败：请确认设备在线后重试。')
    }
  }

  term.onData((data) => {
    if (socket?.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify({ type: 'input', data }))
    }
  })
}

async function closeTerminal() {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.close()
  }
  await cleanupTerminal()
  setStatus('closed', '终端已关闭。')
}

// 释放终端界面与连接（服务端会话由断开链路自动收尾）
async function cleanupTerminal() {
  socket?.close()
  socket = null
  term?.dispose()
  term = null
  fitAddon = null
  if (termHost.value) {
    termHost.value.innerHTML = ''
  }
}

function onWindowResize() {
  fitTerminal()
}

onMounted(async () => {
  window.addEventListener('resize', onWindowResize)
  await refresh()
  // 支持 /terminal?device=<id> 深链：目标详情页交互入口可直接跳入对应模式
  const requested = Number(route.query.device)
  if (Number.isFinite(requested) && connectableDevices.value.some((d) => d.id === requested)) {
    selectedDeviceId.value = requested
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', onWindowResize)
  void closeTerminal()
})
</script>

<template>
  <section class="terminal">
    <div class="terminal-header">
      <div>
        <h1 class="terminal-title">Web 终端</h1>
        <p class="terminal-description">经面板与 agent 回连通道直达目标设备 shell，目标设备无需开放任何入站端口。</p>
      </div>
      <div class="terminal-controls">
        <select v-model.number="selectedDeviceId" class="device-select" :disabled="sessionState === 'open'">
          <option v-for="device in connectableDevices" :key="device.id" :value="device.id" :disabled="!device.online">
            {{ device.name }}（{{ device.online ? '在线' : '离线' }}）
          </option>
        </select>
        <button
          v-if="sessionState !== 'open' && sessionState !== 'connecting' && hasShellMode"
          type="button"
          class="primary-button"
          :disabled="selectedDeviceId === null"
          @click="openTerminal"
        >
          打开终端
        </button>
        <button v-else-if="sessionState === 'open' || sessionState === 'connecting'" type="button" class="ghost-button" @click="closeTerminal">关闭终端</button>
      </div>
    </div>

    <p v-if="errorMessage" class="error-banner">{{ errorMessage }}</p>

    <div class="status-line" :class="sessionState">
      <span class="status-dot"></span>
      <span>{{ statusText }}</span>
    </div>

    <div v-show="termHost && (sessionState === 'connecting' || sessionState === 'open')" ref="termHost" class="term-host"></div>

    <div v-if="loading" class="empty-state">加载中…</div>
    <div v-else-if="connectableDevices.length === 0" class="empty-state">
      还没有可连接的设备。先在「设备管理」登记设备并接入 agent。
    </div>
    <div v-else-if="sessionState === 'idle' || sessionState === 'closed' || sessionState === 'error'" class="empty-state">
      {{ statusText }}
    </div>
  </section>
</template>

<style scoped>
.terminal-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.terminal-title {
  margin: 0 0 4px;
  font-size: 1.25rem;
}

.terminal-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.terminal-controls {
  display: flex;
  align-items: center;
  gap: 10px;
}

.device-select {
  padding: 8px 10px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  font-size: 0.875rem;
  background: #fff;
}

.primary-button {
  padding: 8px 16px;
  border: none;
  border-radius: 8px;
  background: var(--color-primary, #2563eb);
  color: #fff;
  font-size: 0.875rem;
  cursor: pointer;
  white-space: nowrap;
}

.primary-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.ghost-button {
  padding: 7px 14px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.875rem;
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

.status-line {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 9px 12px;
  border-radius: 8px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  font-size: 0.82rem;
  margin-bottom: 12px;
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #9ca3af;
}

.status-line.open .status-dot {
  background: #10b981;
}

.status-line.connecting .status-dot {
  background: #f59e0b;
}

.status-line.error .status-dot {
  background: #dc2626;
}

.term-host {
  height: 60vh;
  min-height: 360px;
  padding: 6px;
  border-radius: 10px;
  background: #0f172a;
}

.empty-state {
  padding: 48px 16px;
  border: 1px dashed var(--color-border);
  border-radius: 10px;
  text-align: center;
  color: var(--color-text-light);
  font-size: 0.9rem;
}

@media (max-width: 768px) {
  .terminal-header {
    flex-direction: column;
  }

  .terminal-controls {
    width: 100%;
    flex-wrap: wrap;
  }

  .device-select {
    flex: 1;
  }
}
</style>
