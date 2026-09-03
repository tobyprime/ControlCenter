<script setup lang="ts">
import { ref } from 'vue'
import { fetchSession } from '@/router'

const username = ref('')
fetchSession().then((session) => {
  username.value = session.username
})

const overviewItems = ref([
  { label: '设备总数', value: '—', hint: '设备管理功能建设中' },
  { label: '在线设备', value: '—', hint: '设备管理功能建设中' },
  { label: '活跃告警', value: '—', hint: '告警功能建设中' },
])
</script>

<template>
  <section class="home">
    <h1 class="home-title">欢迎，{{ username || '管理员' }}</h1>
    <p class="home-description">当前为一期骨架版本，设备、终端、日志与告警功能将陆续上线。</p>

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
