<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { fetchSession } from '@/router'
import { listTargets, type Target } from '@/api/targets'
import { fetchAlertQueue } from '@/api/alerts'

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
    try {
      const queue = await fetchAlertQueue()
      overview.value.alerts = String(queue.count)
    } catch {
      // 队列数量失败不阻塞其他概览
    }
  } catch {
    // 首页概览加载失败不打断页面，保留占位
  }
}

onMounted(() => {
  refreshOverview()
  refreshTimer = window.setInterval(refreshOverview, 15000)
})

onBeforeUnmount(() => {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
  }
})

const overviewItems = [
  { label: '目标总数', hint: '去目标管理页查看', get value() { return overview.value.total } },
  { label: '在线目标', hint: '每 15 秒自动刷新', get value() { return overview.value.online } },
  { label: '待发告警', hint: '去告警规则页查看队列', get value() { return overview.value.alerts } },
]
</script>

<template>
  <section class="home">
    <h1 class="home-title">欢迎，{{ username || '管理员' }}</h1>
    <p class="home-description">当前为一期骨架版本，终端、日志与告警功能将陆续上线。</p>

    <div class="overview-grid">
      <div v-for="item in overviewItems" :key="item.label" class="overview-card">
        <span class="overview-label">{{ item.label }}</span>
        <span class="overview-value">{{ item.value }}</span>
        <span class="overview-hint">{{ item.hint }}</span>
      </div>
    </div>
  </section>
</template>

<style scoped>
.home-title {
  margin: 0 0 8px;
  font-size: 1.25rem;
}

.home-description {
  margin: 0 0 20px;
  color: var(--color-text-light);
  font-size: 0.9rem;
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
</style>
