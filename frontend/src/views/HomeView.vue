<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { fetchSession } from '@/router'
import { listCollectors, type Collector } from '@/api/collectors'
import { fetchActiveAlertCount, fetchAlertEvents, type AlertEvent } from '@/api/alerts'
import { fetchDashboardLayout, saveDashboardLayout, type DashboardCard } from '@/api/dashboard'
import {
  listMetricKeys,
  fetchTargetAvailableMetrics,
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
import DashboardControlCard from '@/components/DashboardControlCard.vue'
import DashboardControlCardConfigForm from '@/components/DashboardControlCardConfigForm.vue'

const username = ref('')
fetchSession().then((session) => {
  username.value = session.username
})

const overview = ref({ total: '—', online: '—', alerts: '—' })
const targets = ref<Collector[]>([])
let refreshTimer: number | undefined

async function refreshOverview() {
  try {
    const list: Collector[] = await listCollectors()
    targets.value = list
    targetsReady.value = true
    overview.value.total = String(list.length)
    overview.value.online = String(list.filter((target) => target.online).length)
  } catch {
    // 首页概览加载失败不打断页面，保留占位；targets 未就绪，指标卡不判「目标不存在」
  }
  try {
    overview.value.alerts = String((await fetchActiveAlertCount()).count)
  } catch {
    // 告警计数失败仅保留占位，不影响设备概览与指标卡刷新
  }
  void refreshRecentAlerts()
  // targets 就绪后再取指标摘要：早于本轮调用时 summary 会因 targets 为空而跳过
  void refreshMetricsSummary()
}

const cards = ref<DashboardCard[]>([])
const editing = ref(false)
const draft = ref<DashboardCard[]>([])
const saveError = ref('')
const dragIndex = ref(-1)

// TOB-408 F10：默认布局新增卡的取数。最近告警直取告警事件端点（15s 周期）；
// 指标摘要按来源拉概览端点取最新值（30s 周期），仅在卡片可见时请求
const recentAlerts = ref<AlertEvent[]>([])
const kindLabels: Record<string, string> = { trigger: '触发', recover: '恢复' }

const summaryByTarget = ref<Record<number, MetricOverviewItem[]>>({})
const summaryLoading = ref(false)

function hasVisibleCard(type: string): boolean {
  return cards.value.some((card) => card.type === type && card.visible)
}

async function refreshRecentAlerts() {
  if (!hasVisibleCard('recent-alerts')) {
    return
  }
  try {
    recentAlerts.value = (await fetchAlertEvents({ limit: 5 })).items
  } catch {
    // 拉取失败保留上一轮数据，下一刷新周期兜底
  }
}

interface SummaryRow {
  targetId: number
  key: string
  targetName: string
  displayName: string
  text: string
  timeUtc: string | null
}

function summaryTextOf(item: MetricOverviewItem): string {
  if (item.latestValueText !== null) {
    return item.latestValueText
  }
  if (item.latestValueNum !== null) {
    return item.unit ? `${item.latestValueNum} ${item.unit}` : String(item.latestValueNum)
  }
  return '—'
}

const summaryRows = computed<SummaryRow[]>(() => {
  const rows: SummaryRow[] = []
  for (const target of targets.value) {
    const items = [...(summaryByTarget.value[target.id] ?? [])]
      .filter((item) => item.latestValueNum !== null || item.latestValueText !== null)
      .sort((a, b) => (b.latestTimeUtc ?? '').localeCompare(a.latestTimeUtc ?? ''))
    for (const item of items) {
      rows.push({
        targetId: target.id,
        key: item.key,
        targetName: target.name,
        displayName: item.displayName,
        text: summaryTextOf(item),
        timeUtc: item.latestTimeUtc,
      })
    }
  }
  return rows.slice(0, 8)
})

async function refreshMetricsSummary() {
  if (!hasVisibleCard('metrics-summary') || targets.value.length === 0) {
    return
  }
  summaryLoading.value = true
  try {
    const entries = await Promise.all(
      targets.value.map(async (target) => {
        try {
          return [target.id, await fetchTargetOverview(target.id)] as const
        } catch {
          // 单来源失败按无数据处理，不阻塞其他来源
          return [target.id, null] as const
        }
      }),
    )
    const next: Record<number, MetricOverviewItem[]> = {}
    for (const [targetId, items] of entries) {
      if (items) {
        next[targetId] = items
      }
    }
    summaryByTarget.value = next
  } finally {
    summaryLoading.value = false
  }
}

// TOB-368 指标卡数据：来源按 target 聚合拉取，曲线按卡片各拉一条
const metricKeys = ref<MetricKeyInfo[]>([])
const overviewByTarget = ref<Record<number, MetricOverviewItem[]>>({})
const seriesByCard = ref<Record<string, MetricSeries | null>>({})
const metricLoading = ref(false)
let metricTimer: number | undefined

// 来源注册表首次加载完成前，空列表 ≠ 确认缺失：未就绪时不判降级，
// 卡片走加载态（审查 round 2 问题 2：避免首屏/接口失败期间误报「目标/指标已不存在」）
const targetsReady = ref(false)
const keysReady = ref(false)
const registriesLoading = computed(() => !targetsReady.value || !keysReady.value)

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

// 来源失效降级：未配置 / 目标已删除 / 指标已注销 / 指标类型与卡片不匹配。
// 目标/指标缺失仅在对应注册表确认加载成功后判定；未就绪返回 ''，由加载态接管
function degradedReasonFor(card: DashboardCard): MetricCardDegradedReason {
  const config = metricConfigOf(card)
  if (!config) {
    return 'unconfigured'
  }
  if (targetsReady.value && !targets.value.some((target) => target.id === config.targetId)) {
    return 'target-missing'
  }
  const info = keyInfoOf(metricKeys.value, config.key)
  if (keysReady.value) {
    if (!info) {
      return 'key-missing'
    }
    if (isMetricCardType(card.type) && !compatibleCardTypes(info.valueType).includes(card.type)) {
      return 'type-mismatch'
    }
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

// 查看态标题「来源名 · 指标名」（TOB-374 ③）；目标/指标任一未知时退回卡片类型文案。
// 卡片类型文案（数值卡等）只在编辑态展示，查看态不再重复
function metricCardTitle(card: DashboardCard): string {
  const config = metricConfigOf(card)
  if (!config) {
    return cardLabel(card.type)
  }
  const targetName = targets.value.find((target) => target.id === config.targetId)?.name ?? ''
  const keyName = keyInfoOf(metricKeys.value, config.key)?.displayName ?? ''
  return [targetName, keyName].filter(Boolean).join(' · ') || cardLabel(card.type)
}

async function refreshRegistries() {
  try {
    metricKeys.value = await listMetricKeys()
    keysReady.value = true
  } catch {
    // 注册表失败不阻塞主页，指标卡按加载态等待下个刷新周期
  }
}

// 按来源可用指标（TOB-374 ①）：目标就绪后预取一次并随指标卡周期刷新，
// 供卡片配置表单按所选来源过滤指标下拉；单目标失败按未知处理，表单回退全量口径
const availableByTarget = ref<Record<number, MetricKeyInfo[]>>({})

async function refreshAvailableMetrics() {
  const entries = await Promise.all(
    targets.value.map(async (target) => {
      try {
        return [target.id, await fetchTargetAvailableMetrics(target.id)] as const
      } catch {
        return [target.id, null] as const
      }
    }),
  )
  const next: Record<number, MetricKeyInfo[]> = {}
  for (const [targetId, infos] of entries) {
    if (infos) {
      next[targetId] = infos
    }
  }
  availableByTarget.value = next
}

watch(targetsReady, (ready) => {
  if (ready) {
    void refreshAvailableMetrics()
  }
})

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
  loadLayout().then(() => {
    refreshMetricCards()
    // F10 新增卡：布局加载完成后立即取一轮（refreshOverview 首轮可能早于布局就绪）
    refreshRecentAlerts()
    refreshMetricsSummary()
  })
  refreshOverview()
  refreshRegistries()
  refreshTimer = window.setInterval(refreshOverview, 15000)
  metricTimer = window.setInterval(() => {
    refreshRegistries()
    refreshAvailableMetrics()
    refreshMetricCards()
    refreshRecentAlerts()
    refreshMetricsSummary()
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
        <p class="home-description">采集器接入、日志查看、终端交互、告警通知均已上线；主页概览支持自定义卡片布局与指标卡。</p>
      </div>
      <button v-if="!editing" type="button" class="primary-button dp-btn dp-btn-primary" @click="enterEdit">
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
            class="ghost-button dp-btn dp-btn-ghost"
            @click="addCard(def.type)"
          >
            添加「{{ def.label }}」
          </button>
        </div>
        <div class="card-actions">
          <button type="button" class="primary-button dp-btn dp-btn-primary" @click="persistLayout">保存布局</button>
          <button type="button" class="ghost-button dp-btn dp-btn-ghost" @click="cancelEdit">取消</button>
        </div>
      </div>
      <p v-if="saveError" class="error-banner" role="alert">{{ saveError }}</p>
    </template>

    <div class="overview-grid">
      <div
        v-for="(card, index) in editing ? draft : visibleCards"
        :key="card.id"
        class="overview-card"
        :class="{ 'card-hidden': editing && !card.visible, 'card-dragging': dragIndex === index, 'card-chart': card.type === 'metric-chart', 'card-control': card.type === 'control-card', 'card-recent-alerts': card.type === 'recent-alerts', 'card-metrics-summary': card.type === 'metrics-summary' }"
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
          <button type="button" class="ghost-button dp-btn dp-btn-ghost dp-touch" @click="toggleVisible(card)">
            {{ card.visible ? '隐藏' : '显示' }}
          </button>
          <button type="button" class="danger-button dp-touch" @click="removeCard(index)">删除</button>
        </div>

        <!-- 控制卡（三期模块4）：编辑态组合控制器，查看态直接操作并即时回执 -->
        <template v-if="card.type === 'control-card'">
          <template v-if="editing">
            <span class="overview-label">{{ cardLabel(card.type) }}</span>
            <DashboardControlCardConfigForm :card="card" :targets="targets" />
          </template>
          <DashboardControlCard v-else :card="card" />
        </template>

        <!-- TOB-408 F10 最近告警卡：查看态列最近 5 条事件快照，编辑态仅展示类型文案 -->
        <template v-else-if="card.type === 'recent-alerts'">
          <span v-if="editing" class="overview-label">{{ cardLabel(card.type) }}</span>
          <template v-else>
            <span class="overview-label">{{ cardLabel(card.type) }}</span>
            <ul v-if="recentAlerts.length > 0" class="recent-alerts-list">
              <li v-for="event in recentAlerts" :key="event.id" class="recent-alert-item">
                <span class="recent-alert-kind" :class="event.kind === 'trigger' ? 'kind-trigger' : 'kind-recover'">
                  {{ kindLabels[event.kind] ?? event.kind }}
                </span>
                <span class="recent-alert-text">{{ event.targetName }} · {{ event.title }}：{{ event.content }}</span>
                <span class="recent-alert-time">{{ new Date(event.createdAtUtc).toLocaleString() }}</span>
              </li>
            </ul>
            <span v-else class="card-state">近期无告警事件</span>
          </template>
        </template>

        <!-- TOB-408 F10 指标摘要卡：按来源列指标最新值 -->
        <template v-else-if="card.type === 'metrics-summary'">
          <span v-if="editing" class="overview-label">{{ cardLabel(card.type) }}</span>
          <template v-else>
            <span class="overview-label">{{ cardLabel(card.type) }}</span>
            <div v-if="summaryRows.length > 0" class="summary-rows">
              <div v-for="row in summaryRows" :key="`${row.targetId}-${row.key}`" class="summary-row">
                <span class="summary-target">{{ row.targetName }}</span>
                <span class="summary-metric">{{ row.displayName }}</span>
                <span class="summary-value">{{ row.text }}</span>
              </div>
            </div>
            <span v-else-if="summaryLoading || registriesLoading" class="card-state">加载中…</span>
            <span v-else class="card-state">暂无指标数据</span>
          </template>
        </template>

        <!-- 一期概览卡 -->
        <template v-else-if="!isMetricCardType(card.type)">
          <span class="overview-label">{{ cardLabel(card.type) }}</span>
          <span class="overview-value">{{ overviewValue(card.type) }}</span>
          <span class="overview-hint">{{ cardHint(card.type) }}</span>
        </template>

        <!-- 指标卡（TOB-368）：编辑态配置来源/类型/时间窗，查看态按类型渲染 -->
        <template v-else-if="editing">
          <span class="overview-label">{{ cardLabel(card.type) }}</span>
          <DashboardCardConfigForm
            :card="card"
            :targets="targets"
            :metric-keys="metricKeys"
            :available-by-target="availableByTarget"
          />
        </template>
        <DashboardValueCard
          v-else-if="card.type === 'metric-value'"
          :label="metricCardTitle(card)"
          :item="overviewItemOf(card)"
          :degraded-reason="degradedReasonFor(card)"
          :loading="metricLoading || registriesLoading"
        />
        <DashboardStatusCard
          v-else-if="card.type === 'metric-status'"
          :label="metricCardTitle(card)"
          :item="overviewItemOf(card)"
          :degraded-reason="degradedReasonFor(card)"
          :loading="metricLoading || registriesLoading"
        />
        <DashboardChartCard
          v-else
          :label="metricCardTitle(card)"
          :series="chartSeriesOf(card)"
          :key-info="chartKeyInfoOf(card)"
          :degraded-reason="degradedReasonFor(card)"
          :loading="metricLoading || registriesLoading"
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

/* 控制卡占满整行：组合多台设备的控制器，操作与回执需要横向空间 */
.card-control {
  grid-column: 1 / -1;
}

/* F10 最近告警 / 指标摘要卡占满整行，列表行有横向空间 */
.card-recent-alerts,
.card-metrics-summary {
  grid-column: 1 / -1;
}

.recent-alerts-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.recent-alert-item {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 0.85rem;
  min-width: 0;
}

.recent-alert-kind {
  flex: none;
  padding: 1px 8px;
  border-radius: 999px;
  font-size: 0.75rem;
  white-space: nowrap;
}

.recent-alert-kind.kind-trigger {
  background: #fef2f2;
  color: #b91c1c;
}

.recent-alert-kind.kind-recover {
  background: #ecfdf5;
  color: #047857;
}

.recent-alert-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.recent-alert-time {
  flex: none;
  color: var(--color-text-light);
  font-size: 0.75rem;
  white-space: nowrap;
}

.summary-rows {
  display: flex;
  flex-direction: column;
}

.summary-row {
  display: grid;
  grid-template-columns: minmax(96px, max-content) 1fr max-content;
  gap: 12px;
  align-items: baseline;
  padding: 6px 0;
  border-bottom: 1px dashed var(--color-border);
  font-size: 0.85rem;
}

.summary-row:last-child {
  border-bottom: none;
}

.summary-target {
  color: var(--color-text-light);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.summary-metric {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.summary-value {
  font-weight: 600;
  white-space: nowrap;
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
