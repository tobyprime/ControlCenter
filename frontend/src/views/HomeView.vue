<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { fetchSession } from '@/router'
import { listTargets, type Target } from '@/api/targets'
import { fetchDashboardLayout, saveDashboardLayout, type DashboardCard } from '@/api/dashboard'
import { BUILTIN_CARD_DEFS, cardDef, createDefaultLayout, normalizeLayout } from '@/dashboard/cards'

const username = ref('')
fetchSession().then((session) => {
  username.value = session.username
})

const overview = ref({ total: '—', online: '—', alerts: '—' })
let refreshTimer: number | undefined

async function refreshOverview() {
  try {
    const targets: Target[] = await listTargets()
    overview.value.total = String(targets.length)
    overview.value.online = String(targets.filter((target) => target.online).length)
  } catch {
    // 首页概览加载失败不打断页面，保留占位
  }
}

const cards = ref<DashboardCard[]>([])
const editing = ref(false)
const draft = ref<DashboardCard[]>([])
const saveError = ref('')
const dragIndex = ref(-1)

const visibleCards = computed(() => cards.value.filter((card) => card.visible))

const addableDefs = computed(() => {
  const present = new Set(draft.value.map((card) => card.type))
  return BUILTIN_CARD_DEFS.filter((def) => !present.has(def.type))
})

function cardLabel(type: string): string {
  return cardDef(type)?.label ?? type
}

function cardHint(type: string): string {
  return cardDef(type)?.hint ?? ''
}

function overviewValue(type: string): string {
  if (type === 'overview-devices-total') {
    return overview.value.total
  }
  if (type === 'overview-devices-online') {
    return overview.value.online
  }
  return overview.value.alerts
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
  loadLayout()
  refreshOverview()
  refreshTimer = window.setInterval(refreshOverview, 15000)
})

onBeforeUnmount(() => {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
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
        :class="{ 'card-hidden': editing && !card.visible, 'card-dragging': dragIndex === index }"
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
        <span class="overview-label">{{ cardLabel(card.type) }}</span>
        <span class="overview-value">{{ overviewValue(card.type) }}</span>
        <span class="overview-hint">{{ cardHint(card.type) }}</span>
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

.card-empty {
  padding: 48px 16px;
  border: 1px dashed var(--color-border);
  border-radius: 10px;
  text-align: center;
  color: var(--color-text-light);
}
</style>
