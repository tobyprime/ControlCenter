<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import type { DashboardCard } from '@/api/dashboard'
import { listCollectors, type Collector } from '@/api/collectors'
import {
  controlTypeLabel,
  invokeController,
  listCollectorControllers,
  type ControllerDeclaration,
} from '@/api/controls'
import { parseControlCardConfig } from '@/dashboard/controlCardConfig'

// 主页控制卡（三期模块4）：按设备组合控制器，操作即时回执（成功/失败/超时/离线），
// 默认无二次确认；下发链路：主页 → 面板后端 → agent WS 通道（ctrl.invoke.request）
const props = defineProps<{
  card: DashboardCard
}>()

interface ControlResult {
  kind: 'success' | 'failure' | 'timeout'
  message: string
}

interface ControlRow {
  uid: string
  collectorId: number
  key: string
  targetName: string
  targetOnline: boolean
  declaration: ControllerDeclaration | null
  busy: boolean
  // 开关的本地方向（设备真实状态不回读，回执成功即翻转）
  toggleOn: boolean
  inputText: string
  sliderValue: number
  result: ControlResult | null
}

const rows = ref<ControlRow[]>([])
const configError = ref(false)
let targetsLoaded = false
const targets = ref<Collector[]>([])

watch(
  () => props.card,
  () => {
    void resolveRows()
  },
)

onMounted(() => {
  void resolveRows()
})

async function resolveRows() {
  const config = parseControlCardConfig(props.card.config)
  configError.value = config === null
  if (!config) {
    rows.value = []
    return
  }
  if (!targetsLoaded) {
    targetsLoaded = true
    try {
      targets.value = await listCollectors()
    } catch {
      // 设备清单失败不阻塞卡片：名称退化为「已删除设备」，在线态按未知处理
    }
  }
  const declarationsByCollector = new Map<number, ControllerDeclaration[] | null>()
  const next: ControlRow[] = []
  for (const ref of config.controllers) {
    if (!declarationsByCollector.has(ref.collectorId)) {
      declarationsByCollector.set(ref.collectorId, await loadDeclarations(ref.collectorId))
    }
    const declaration = declarationsByCollector.get(ref.collectorId)?.find((item) => item.key === ref.key) ?? null
    const target = targets.value.find((item) => item.id === ref.collectorId)
    next.push({
      uid: `${ref.collectorId}:${ref.key}`,
      collectorId: ref.collectorId,
      key: ref.key,
      targetName: target?.name ?? '已删除设备',
      targetOnline: target?.online ?? false,
      declaration,
      busy: false,
      toggleOn: false,
      inputText: '',
      sliderValue: initialSliderValue(declaration),
      result: null,
    })
  }
  rows.value = next
}

async function loadDeclarations(collectorId: number): Promise<ControllerDeclaration[] | null> {
  try {
    return (await listCollectorControllers(collectorId)).controllers
  } catch {
    return null
  }
}

async function invoke(row: ControlRow, params: unknown, onSuccess?: () => void) {
  row.busy = true
  row.result = null
  try {
    const outcome = await invokeController(row.collectorId, row.key, params)
    if (outcome.status === 'success') {
      row.result = { kind: 'success', message: outcome.message ?? '已执行' }
      onSuccess?.()
    } else {
      row.result = {
        kind: outcome.status === 'timeout' ? 'timeout' : 'failure',
        message: outcome.message ?? '控制下发失败',
      }
    }
  } catch (error) {
    // 失败语义：离线 409 / agent 报错 502 / 超时 504；错误体 error 字段为设备侧结论
    const status = (error as { status?: number }).status
    const message = error instanceof Error && error.message ? error.message : '控制下发失败'
    row.result = { kind: status === 504 ? 'timeout' : 'failure', message }
  } finally {
    row.busy = false
  }
}

function onToggleChange(row: ControlRow) {
  const next = !row.toggleOn
  void invoke(row, { state: next }, () => {
    row.toggleOn = next
  })
}

function submitInput(row: ControlRow) {
  if (row.busy || row.inputText === '') {
    return
  }
  void invoke(row, { text: row.inputText })
}

// ---------- 声明 schema 的防御式读取（声明不完整时退化为明确的「未声明」提示，不出运行时错误） ----------

interface ButtonItem {
  label: string
  value: string
}

function buttonItems(row: ControlRow): ButtonItem[] {
  const items = (row.declaration?.paramsSchema as Record<string, unknown> | null)?.items
  if (!Array.isArray(items)) {
    return []
  }
  return items.flatMap((item) => {
    if (typeof item !== 'object' || item === null) {
      return []
    }
    const label = (item as Record<string, unknown>).label
    const value = (item as Record<string, unknown>).value
    return typeof label === 'string' && typeof value === 'string' && label !== '' && value !== ''
      ? [{ label, value }]
      : []
  })
}

function inputTypeOf(row: ControlRow): 'text' | 'number' | 'password' {
  const inputType = (row.declaration?.paramsSchema as Record<string, unknown> | null)?.inputType
  return inputType === 'number' || inputType === 'password' ? inputType : 'text'
}

interface SliderBounds {
  min: number
  max: number
  step: number
}

function sliderBounds(row: ControlRow): SliderBounds {
  return sliderBoundsOf(row.declaration)
}

function sliderBoundsOf(declaration: ControllerDeclaration | null): SliderBounds {
  const schema = declaration?.paramsSchema as Record<string, unknown> | null
  const min = typeof schema?.min === 'number' ? schema.min : 0
  const max = typeof schema?.max === 'number' ? schema.max : 100
  const step = typeof schema?.step === 'number' && schema.step > 0 ? schema.step : 1
  return { min, max: max > min ? max : min + step, step }
}

function initialSliderValue(declaration: ControllerDeclaration | null): number {
  return sliderBoundsOf(declaration).min
}

function resultText(kind: ControlResult['kind']): string {
  if (kind === 'success') {
    return '成功'
  }
  return kind === 'timeout' ? '超时' : '失败'
}
</script>

<template>
  <div class="control-card" :data-card-type="'control-card'">
    <span v-if="configError" class="control-state">控制卡未配置，请在编辑模式下组合控制器。</span>
    <template v-else>
      <div v-for="row in rows" :key="row.uid" class="controller-row">
        <div class="controller-head">
          <span class="controller-label">{{ row.declaration?.label || row.key }}</span>
          <span class="controller-device">
            {{ row.targetName }} · {{ controlTypeLabel(row.declaration?.type ?? '') }}
          </span>
          <span v-if="!row.targetOnline" class="offline-badge">设备离线</span>
        </div>

        <span v-if="!row.declaration" class="control-state">设备未声明控制器「{{ row.key }}」</span>

        <!-- 按钮：声明 items 清单，点哪个发哪个 -->
        <div v-else-if="row.declaration.type === 'button'" class="controller-body">
          <span v-if="buttonItems(row).length === 0" class="control-state">该按钮未声明动作清单</span>
          <button
            v-for="item in buttonItems(row)"
            :key="item.value"
            type="button"
            class="control-button"
            :disabled="row.busy"
            @click="invoke(row, { value: item.value })"
          >
            {{ row.busy ? '下发中…' : item.label }}
          </button>
        </div>

        <!-- 开关：回执成功才翻转方向，失败保持原态 -->
        <label v-else-if="row.declaration.type === 'toggle'" class="controller-body toggle-body">
          <input
            type="checkbox"
            :checked="row.toggleOn"
            :disabled="row.busy"
            @change="onToggleChange(row)"
          />
          <span>{{ row.toggleOn ? '开' : '关' }}</span>
        </label>

        <!-- 输入框：Enter 或提交按钮下发文本 -->
        <div v-else-if="row.declaration.type === 'input'" class="controller-body input-body">
          <input
            v-model="row.inputText"
            :type="inputTypeOf(row)"
            :disabled="row.busy"
            placeholder="输入内容"
            @keydown.enter.prevent="submitInput(row)"
          />
          <button type="button" class="control-button" :disabled="row.busy || row.inputText === ''" @click="submitInput(row)">
            提交
          </button>
        </div>

        <!-- 滑块：拖动释放即下发（change 事件），范围/步长取自声明 -->
        <div v-else-if="row.declaration.type === 'slider'" class="controller-body slider-body">
          <input
            v-model.number="row.sliderValue"
            type="range"
            :min="sliderBounds(row).min"
            :max="sliderBounds(row).max"
            :step="sliderBounds(row).step"
            :disabled="row.busy"
            @change="invoke(row, { value: row.sliderValue })"
          />
          <span class="slider-value">{{ row.sliderValue }}</span>
        </div>

        <span v-else class="control-state">控制类型暂不支持：{{ row.declaration.type }}</span>

        <span v-if="row.result" class="control-result" :class="row.result.kind">
          {{ resultText(row.result.kind) }}：{{ row.result.message }}
        </span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.control-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.controller-row {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
}

.controller-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
}

.controller-label {
  font-weight: 600;
  font-size: 0.9rem;
}

.controller-device {
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.offline-badge {
  padding: 1px 6px;
  border-radius: 4px;
  background: #fef2f2;
  color: #dc2626;
  font-size: 0.7rem;
}

.controller-body {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.control-button {
  padding: 6px 14px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.8rem;
  cursor: pointer;
}

.control-button:hover:not(:disabled) {
  border-color: var(--color-primary);
  color: var(--color-primary);
}

.control-button:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.toggle-body {
  font-size: 0.8rem;
  color: var(--color-text-light);
}

.input-body input {
  flex: 1;
  min-width: 120px;
  padding: 6px 10px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  font-size: 0.8rem;
}

.input-body input:disabled {
  opacity: 0.55;
}

.slider-body input[type='range'] {
  flex: 1;
  min-width: 120px;
}

.slider-value {
  min-width: 36px;
  text-align: right;
  font-size: 0.8rem;
  color: var(--color-text-light);
  font-variant-numeric: tabular-nums;
}

.control-state {
  color: var(--color-text-light);
  font-size: 0.8rem;
}

.control-result {
  font-size: 0.78rem;
  padding: 6px 10px;
  border-radius: 6px;
}

.control-result.success {
  background: #ecfdf5;
  color: #047857;
}

.control-result.failure {
  background: #fef2f2;
  color: #dc2626;
}

.control-result.timeout {
  background: #fffbeb;
  color: #92400e;
}
</style>
