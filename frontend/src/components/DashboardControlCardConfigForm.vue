<script setup lang="ts">
import { onMounted, ref } from 'vue'
import type { DashboardCard } from '@/api/dashboard'
import type { Collector } from '@/api/collectors'
import {
  controlTypeLabel,
  listCollectorControllers,
  type ControllerDeclaration,
} from '@/api/controls'

// 控制卡配置表单（三期模块4）：按设备组合控制器（采集器 → 已声明控制器 两级联动）。
// 直接改写编辑态 draft 卡片；未选全（缺采集器或控制器）的行不写入 config，保存时被剔除
const props = defineProps<{
  card: DashboardCard
  targets: Collector[]
}>()

interface ConfigRow {
  collectorId: number // 0 = 未选择
  key: string
}

const rows = ref<ConfigRow[]>([])
const controllerOptions = ref<Record<number, ControllerDeclaration[]>>({})

onMounted(() => {
  // 逐字段读取原始 config：半配置行（只选了设备）也是合法编辑态，不走防御式解析往返
  const raw = (props.card.config as Record<string, unknown>).controllers
  if (Array.isArray(raw)) {
    rows.value = raw.flatMap((item) => {
      if (typeof item !== 'object' || item === null) {
        return []
      }
      const collectorId = (item as Record<string, unknown>).collectorId
      const key = (item as Record<string, unknown>).key
      if (typeof collectorId !== 'number' || !Number.isInteger(collectorId) || collectorId <= 0) {
        return []
      }
      return [{ collectorId, key: typeof key === 'string' ? key : '' }]
    })
  }
  for (const row of rows.value) {
    void ensureOptions(row.collectorId)
  }
})

function persist() {
  props.card.config = {
    controllers: rows.value
      .filter((row) => row.collectorId > 0 && row.key !== '')
      .map((row) => ({ collectorId: row.collectorId, key: row.key })),
  }
}

async function ensureOptions(collectorId: number) {
  if (collectorId <= 0 || controllerOptions.value[collectorId]) {
    return
  }
  controllerOptions.value[collectorId] = [] // 先占位防重复请求
  try {
    controllerOptions.value[collectorId] = (await listCollectorControllers(collectorId)).controllers
  } catch {
    // 拉取失败按空清单处理，用户可换设备或稍后重试
  }
}

function onCollectorChange(row: ConfigRow, event: Event) {
  const collectorId = Number((event.target as HTMLSelectElement).value)
  if (Number.isInteger(collectorId) && collectorId > 0) {
    row.collectorId = collectorId
    row.key = '' // 切换设备后原控制器未必仍存在：清空重选
    void ensureOptions(collectorId)
    persist()
  }
}

function onKeyChange(row: ConfigRow, event: Event) {
  row.key = (event.target as HTMLSelectElement).value
  persist()
}

function addRow() {
  rows.value.push({ collectorId: 0, key: '' })
}

function removeRow(index: number) {
  rows.value.splice(index, 1)
  persist()
}

function optionsOf(row: ConfigRow): ControllerDeclaration[] {
  return controllerOptions.value[row.collectorId] ?? []
}

function missingDeclaration(row: ConfigRow): boolean {
  return row.collectorId > 0 && optionsOf(row).length === 0
}
</script>

<template>
  <div class="control-config">
    <div v-for="(row, index) in rows" :key="index" class="controller-config-row">
      <select :value="row.collectorId || ''" @change="onCollectorChange(row, $event)">
        <option value="" disabled>请选择采集器</option>
        <option v-for="target in targets" :key="target.id" :value="target.id">{{ target.name }}</option>
      </select>
      <select :value="row.key" :disabled="row.collectorId <= 0" @change="onKeyChange(row, $event)">
        <option value="" disabled>请选择控制器</option>
        <option v-for="declaration in optionsOf(row)" :key="declaration.key" :value="declaration.key">
          {{ declaration.label }}（{{ controlTypeLabel(declaration.type) }}）
        </option>
      </select>
      <button type="button" class="remove-row" @click="removeRow(index)">移除</button>
    </div>
    <p v-if="rows.length === 0" class="config-hint">尚未组合控制器，点击「添加控制器」开始</p>
    <p v-if="rows.some((row) => missingDeclaration(row))" class="config-hint">
      部分设备未声明控制器或清单加载失败，请换设备或稍后重试
    </p>
    <button type="button" class="add-row" @click="addRow">添加控制器</button>
  </div>
</template>

<style scoped>
.control-config {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.controller-config-row {
  display: grid;
  grid-template-columns: 1fr 1fr auto;
  gap: 8px;
  align-items: center;
}

.controller-config-row select {
  min-width: 0;
  padding: 5px 8px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: var(--color-surface);
  color: var(--color-text);
  font-size: 0.8rem;
}

.controller-config-row select:disabled {
  opacity: 0.55;
}

.remove-row {
  padding: 5px 10px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: transparent;
  color: var(--color-danger);
  font-size: 0.75rem;
  cursor: pointer;
}

.remove-row:hover {
  border-color: var(--color-danger);
}

.config-hint {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.75rem;
}

.add-row {
  align-self: flex-start;
  padding: 5px 12px;
  border: 1px dashed var(--color-border);
  border-radius: 6px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.78rem;
  cursor: pointer;
}

.add-row:hover {
  border-color: var(--color-primary);
  color: var(--color-primary);
}
</style>
