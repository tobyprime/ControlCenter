<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { clearSession, fetchSession } from '@/router'
import { logout } from '@/api/auth'

const router = useRouter()
const username = ref('…')

fetchSession()
  .then((session) => {
    username.value = session.username
  })
  .catch(() => {
    router.replace({ name: 'login' })
  })

async function onLogout() {
  try {
    await logout()
  } finally {
    clearSession()
    await router.replace({ name: 'login' })
  }
}
</script>

<template>
  <div class="layout">
    <header class="topbar">
      <span class="brand">设备与环境统一管理面板</span>
      <div class="topbar-actions">
        <span class="username">{{ username }}</span>
        <button type="button" class="logout-button" @click="onLogout">退出登录</button>
      </div>
    </header>

    <div class="layout-body">
      <aside class="sidebar">
        <nav class="nav">
          <RouterLink class="nav-item active" to="/">首页</RouterLink>
          <span class="nav-item disabled">设备管理（建设中）</span>
          <span class="nav-item disabled">Web 终端（建设中）</span>
          <span class="nav-item disabled">日志查看（建设中）</span>
          <span class="nav-item disabled">告警配置（建设中）</span>
        </nav>
      </aside>

      <main class="content">
        <RouterView />
      </main>
    </div>
  </div>
</template>

<style scoped>
.layout {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
}

.topbar {
  height: var(--topbar-height);
  padding: 0 20px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  background: var(--color-surface);
  border-bottom: 1px solid var(--color-border);
}

.brand {
  font-size: 1rem;
  font-weight: 600;
}

.topbar-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.username {
  color: var(--color-text-light);
  font-size: 0.9rem;
}

.logout-button {
  padding: 6px 14px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: transparent;
  color: var(--color-text);
  font-size: 0.875rem;
  cursor: pointer;
}

.logout-button:hover {
  border-color: var(--color-danger);
  color: var(--color-danger);
}

.layout-body {
  flex: 1;
  display: flex;
  min-width: 0;
}

.sidebar {
  width: var(--sidebar-width);
  flex-shrink: 0;
  background: var(--color-surface);
  border-right: 1px solid var(--color-border);
  padding: 12px 8px;
}

.nav {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.nav-item {
  padding: 10px 12px;
  border-radius: 8px;
  font-size: 0.9rem;
  color: var(--color-text);
}

.nav-item.active {
  background: #eff6ff;
  color: var(--color-primary);
  font-weight: 600;
}

.nav-item.disabled {
  color: var(--color-text-light);
  cursor: not-allowed;
}

.content {
  flex: 1;
  min-width: 0;
  padding: 20px;
}

@media (max-width: 768px) {
  .sidebar {
    display: none;
  }

  .topbar {
    padding: 0 12px;
  }

  .brand {
    font-size: 0.9rem;
  }

  .content {
    padding: 12px;
  }
}
</style>
