<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { fetchSession } from '@/router'
import { listTargets, type Target } from '@/api/targets'
import { fetchDashboardLayout, saveDashboardLayout, type DashboardCard } from '@/api/dashboard'
import {
  listMetricKeys,
  fetchTargetOverview,
  fetchTargetSeries,
  type MetricKeyInfo,
  type MetricOverviewItem,
  type MetricSeries,
} from '@/api/metrics'
import { BUILTIN_CARD_DEFS, cardDef, createDefaultLayout, normalizeLayout } from '@/dashboard/cards'
import {
  compatibleCardTypes,
  isMetricCardType,
  keyInfoOf,
  parseMetricCardConfig,
  type MetricCardConfig,
  type MetricCardDegradedReason,
} from '@/dashboard/cardConfig'
import DashboardValueCard from '@/components/DashboardValueCard.vue'
import DashboardStatusCard from '@/components/DashboardStatusCard.vue'
import DashboardChartCard from '@/components/DashboardChartCard.vue'
import DashboardCardConfigForm from '@/components/DashboardCardConfigForm.vue'

const username = ref('')
fetchSession().then((session) => {
  username.value = session.username
})

const overview = ref({ total: '—', online: '—', alerts: '—' })
const targets = ref<Target[]>([])
let refreshTimer: number | undefined

async function refreshOverview() {
  try {
    const list: Target[] = await listTargets()
    targets.value = list
    overview.value.total = String(list.length)
    overview.value.online = String(list.filter((target) => target.online).length)
  } catch {
    // 首页概览加载失败不打断页面，保留占位
  }
}

const cards = ref<DashboardCard[]>([])
const editing = ref(false)
const draft = ref<DashboardCard[]>([])
const saveError = ref('')
const dragIndex = ref(-1)

// TOB-368 指标卡数据：来源按 target 聚合拉取，曲线按卡片各拉一条
const metricKeys = ref<MetricKeyInfo[]>([])
const overviewByTarget = ref<Record<number, MetricOverviewItem[]>>({})
const seriesByCard = ref<Record<string, MetricSeries | null>>({})
const metricLoading = ref(false)
let metricTimer: number | undefined

const visibleCards = computed(() => cards.value.filter((card) => card.visible))

const addableDefs = computed(() => {
  const present = new Set(draft.value.map((card) => card.type))
  // 指标卡可多实例（不同来源各一张）；概览卡单实例
  return BUILTIN_CARD_DEFS.filter((def) => def.multiple || !present.has(def.type))
})

function cardLabel(type: string): string {
  return cardDef(type)?.label ?? type
}

function cardHint(type: string): string {
  return cardDef(type)?.hint ?? ''
}

function overviewValue(type: string): string {
  if (type === 'overview-total-devices') {
    return overview.value.total
  }
  if (type === 'overview-online-devices') {
    return overview.value.online
  }
  return overview.value.alerts
}

function metricConfigOf(card: DashboardCard): MetricCardConfig | null {
  return parseMetricCardConfig(card.config)
}

// 来源失效降级：未配置 / 目标已删除 / 指标已注销 / 指标类型与卡片不匹配
function degradedReasonFor(card: DashboardCard): MetricCardDegradedReason {
  const config = metricConfigOf(card)
  if (!config) {
    return 'unconfigured'
  }
  if (!targets.value.some((target) => target.id === config.targetId)) {
    return 'target-missing'
  }
  const info = keyInfoOf(metricKeys.value, config.key)
  if (!info) {
    return 'key-missing'
  }
  if (isMetricCardType(card.type) && !compatibleCardTypes(info.valueType).includes(card.type)) {
    return 'type-mismatch'
  }
  return ''
}

function overviewItemOf(card: DashboardCard): MetricOverviewItem | null {
  const config = metricConfigOf(card)
  if (!config) {
    return null
  }
  return overviewByTarget.value[config.targetId]?.find((item) => item.key === config.key) ?? null
}

function chartSeriesOf(card: DashboardCard): MetricSeries | null {
  return seriesByCard.value[card.id] ?? null
}

function chartKeyInfoOf(card: DashboardCard): MetricKeyInfo | undefined {
  const config = metricConfigOf(card)
  return config ? keyInfoOf(metricKeys.value, config.key) : undefined
}

async function refreshRegistries() {
  try {
    metricKeys.value = await listMetricKeys()
  } catch {
    // 注册表失败不阻塞主页，指标卡按无数据占位
  }
}

async function refreshMetricCards() {
  const configured = cards.value
    .filter((card) => card.visible && isMetricCardType(card.type))
    .map((card) => ({ card, config: parseMetricCardConfig(card.config) }))
    .filter((entry): entry is { card: DashboardCard; config: MetricCardConfig } => entry.config !== null)
  if (configured.length === 0) {
    return
  }
  metricLoading.value = true
  try {
    const targetIds = [...new Set(configured.map((entry) => entry.config.targetId))]
    const overviewEntries = await Promise.all(
      targetIds.map(async (targetId) => {
        try {
          return [targetId, await fetchTargetOverview(targetId)] as const
        } catch {
          // 单目标接口失败按无数据处理；目标确已删除由 targets 校验降级
          return [targetId, null] as const
        }
      }),
    )
    const nextOverview: Record<number, MetricOverviewItem[]> = {}
    for (const [targetId, items] of overviewEntries) {
      if (items) {
        nextOverview[targetId] = items
      }
    }
    overviewByTarget.value = nextOverview

    const chartEntries = configured.filter((entry) => entry.card.type === 'metric-chart')
    const seriesEntries = await Promise.all(
      chartEntries.map(async ({ card, config }) => {
        const to = new Date()
        const from = new Date(to.getTime() - config.windowHours * 3_600_000)
        try {
          const result = await fetchTargetSeries(config.targetId, [config.key], from.toISOString(), to.toISOString())
          return [card.id, result.series.find((series) => series.key === config.key) ?? null] as const
        } catch {
          return [card.id, null] as const
        }
      }),
    )
    const nextSeries: Record<string, MetricSeries | null> = {}
    for (const [cardId, series] of seriesEntries) {
      nextSeries[cardId] = series
    }
    seriesByCard.value = nextSeries
  } finally {
    metricLoading.value = false
  }
}

async function loadLayout() {
  try {
    const layout = await fetchDashboardLayout()
    cards.value = normalizeLayout(layout.cards)
  } catch {
    // 布局接口不可用或无记录：回退默认布局，主页不出现空白面板
    cards.value = createDefaultLayout()
  }
}

function enterEdit() {
  draft.value = cards.value.map((card) => ({ ...card, config: { ...card.config } }))
  saveError.value = ''
  editing.value = true
}

function cancelEdit() {
  editing.value = false
  draft.value = []
  saveError.value = ''
}

async function persistLayout() {
  const normalized = draft.value.map((card, index) => ({ ...card, order: index }))
  try {
    await saveDashboardLayout(normalized)
    cards.value = normalized
    editing.value = false
    draft.value = []
    saveError.value = ''
    refreshMetricCards()
  } catch (error) {
    saveError.value = error instanceof Error ? error.message : '布局保存失败，请稍后重试'
  }
}

function addCard(type: string) {
  const def = cardDef(type)
  if (!def) {
    return
  }
  draft.value.push({
    id: `${type}-${Date.now()}`,
    type,
    visible: true,
    order: draft.value.length,
    config: {},
  })
}

function removeCard(index: number) {
  draft.value.splice(index, 1)
}

function toggleVisible(card: DashboardCard) {
  card.visible = !card.visible
}

function onDragStart(index: number) {
  dragIndex.value = index
}

function onDragOver(event: DragEvent) {
  if (dragIndex.value >= 0) {
    event.preventDefault()
  }
}

function onDrop(index: number) {
  const from = dragIndex.value
  dragIndex.value = -1
  if (from < 0 || from === index) {
    return
  }
  const [moved] = draft.value.splice(from, 1)
  draft.value.splice(index, 0, moved)
}

onMounted(() => {
  loadLayout().then(() => refreshMetricCards())
  refreshOverview()
  refreshRegistries()
  refreshTimer = window.setInterval(refreshOverview, 15000)
  metricTimer = window.setInterval(() => {
    refreshRegistries()
    refreshMetricCards()
  }, 30000)
})

onBeforeUnmount(() => {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
  }
  if (metricTimer) {
    window.clearInterval(metricTimer)
  }
})
</script>

<template>
  <section class="home">
    <div class="home-header">
      <div>
        <h1 class="home-title">欢迎，{{ username || '管理员' }}</h1>
        <p class="home-description">当前为一期骨架版本，终端、日志与告警功能将陆续上线。</p>
      </div>
      <button v-if="!editing" type="button" class="primary-button" @click="enterEdit">
        进入编辑
      </button>
    </div>

    <template v-if="editing">
      <div class="card-toolbar">
        <div class="card-add">
          <span v-if="addableDefs.length > 0" class="card-add-label">添加卡片：</span>
          <button
            v-for="def in addableDefs"
            :key="def.type"
            type="button"
            class="ghost-button"
            @click="addCard(def.type)"
          >
            添加「{{ def.label }}」
          </button>
        </div>
        <div class="card-actions">
          <button type="button" class="primary-button" @click="persistLayout">保存布局</button>
          <button type="button" class="ghost-button" @click="cancelEdit">取消</button>
        </div>
      </div>
      <p v-if="saveError" class="error-banner" role="alert">{{ saveError }}</p>
    </template>

    <div class="overview-grid">
      <div
        v-for="(card, index) in editing ? draft : visibleCards"
        :key="card.id"
        class="overview-card"
        :class="{ 'card-hidden': editing && !card.visible, 'card-dragging': dragIndex === index, 'card-chart': card.type === 'metric-chart' }"
        :data-card-type="card.type"
        :draggable="editing"
        @dragstart="onDragStart(index)"
        @dragover="onDragOver"
        @drop="onDrop(index)"
        @dragend="dragIndex = -1"
      >
        <div v-if="editing" class="card-edit-bar">
          <span v-if="card.visible" class="card-edit-hint">拖拽排序</span>
          <span v-else class="card-hidden-badge">已隐藏</span>
          <span class="card-edit-spacer"></span>
          <button type="button" class="ghost-button" @click="toggleVisible(card)">
            {{ card.visible ? '隐藏' : '显示' }}
          </button>
          <button type="button" class="danger-button" @click="removeCard(index)">删除</button>
        </div>

        <!-- 一期概览卡 -->
        <template v-if="!isMetricCardType(card.type)">
          <span class="overview-label">{{ cardLabel(card.type) }}</span>
          <span class="overview-value">{{ overviewValue(card.type) }}</span>
          <span class="overview-hint">{{ cardHint(card.type) }}</span>
        </template>

        <!-- 指标卡（TOB-368）：编辑态配置来源/类型/时间窗，查看态按类型渲染 -->
        <template v-else-if="editing">
          <span class="overview-label">{{ cardLabel(card.type) }}</span>
          <DashboardCardConfigForm :card="card" :targets="targets" :metric-keys="metricKeys" />
        </template>
        <DashboardValueCard
          v-else-if="card.type === 'metric-value'"
          :label="cardLabel(card.type)"
          :item="overviewItemOf(card)"
          :degraded-reason="degradedReasonFor(card)"
          :loading="metricLoading"
        />
        <DashboardStatusCard
          v-else-if="card.type === 'metric-status'"
          :label="cardLabel(card.type)"
          :item="overviewItemOf(card)"
          :degraded-reason="degradedReasonFor(card)"
          :loading="metricLoading"
        />
        <DashboardChartCard
          v-else
          :label="cardLabel(card.type)"
          :series="chartSeriesOf(card)"
          :key-info="chartKeyInfoOf(card)"
          :degraded-reason="degradedReasonFor(card)"
          :loading="metricLoading"
        />
      </div>
    </div>

    <p v-if="!editing && visibleCards.length === 0" class="card-empty">
      暂无卡片，点击「进入编辑」添加卡片。
    </p>
  </section>
</template>

<style scoped>
.home-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.home-title {
  margin: 0 0 8px;
  font-size: 1.25rem;
}

.home-description {
  margin: 0;
  color: var(--color-text-light);
  font-size: 0.9rem;
}

.primary-button {
  padding: 8px 16px;
  border: none;
  border-radius: 8px;
  background: var(--color-primary);
  color: #fff;
  font-size: 0.875rem;
  cursor: pointer;
  white-space: nowrap;
}

.ghost-button,
.danger-button {
  padding: 6px 12px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.8rem;
  cursor: pointer;
}

.ghost-button:hover {
  border-color: var(--color-primary);
  color: var(--color-primary);
}

.danger-button {
  color: var(--color-danger);
}

.danger-button:hover {
  border-color: var(--color-danger);
}

.card-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px;
  margin-bottom: 16px;
}

.card-add {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.card-add-label {
  color: var(--color-text-light);
  font-size: 0.85rem;
}

.card-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.error-banner {
  margin: 0 0 12px;
  padding: 10px 12px;
  border: 1px solid #fecaca;
  border-radius: 8px;
  background: #fef2f2;
  color: var(--color-danger);
  font-size: 0.85rem;
}

.overview-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 12px;
}

.overview-card {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

/* 曲线卡占满整行，保证序列可读 */
.card-chart {
  grid-column: 1 / -1;
}

.overview-card[draggable='true'] {
  cursor: grab;
}

.card-dragging {
  opacity: 0.5;
}

.card-hidden {
  opacity: 0.55;
  border-style: dashed;
}

.card-edit-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 4px;
}

.card-edit-hint {
  color: var(--color-text-light);
  font-size: 0.7rem;
}

.card-hidden-badge {
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  color: var(--color-text-light);
  font-size: 0.7rem;
}

.card-edit-spacer {
  flex: 1;
}

.overview-label {
  font-size: 0.85rem;
  color: var(--color-text-light);
}

.overview-value {
  font-size: 1.6rem;
  font-weight: 600;
}

.overview-hint {
  font-size: 0.75rem;
  color: var(--color-text-light);
}

.card-state {
  font-size: 0.95rem;
  color: var(--color-text-light);
}

.card-empty {
  padding: 48px 16px;
  border: 1px dashed var(--color-border);
  border-radius: 10px;
  text-align: center;
  color: var(--color-text-light);
}
</style>
