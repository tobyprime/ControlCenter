<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { clearSession, fetchSession } from '@/router'
import { logout } from '@/api/auth'

const route = useRoute()
const router = useRouter()
const username = ref('…')

fetchSession()
  .then((session) => {
    username.value = session.username
  })
  .catch(() => {
    router.replace({ name: 'login' })
  })

// 移动端抽屉导航（TOB-374 ②）：≤768px 侧栏变抽屉，汉堡按钮打开，遮罩或路由跳转关闭；桌面端常驻不受影响
const drawerOpen = ref(false)

watch(
  () => route.fullPath,
  () => {
    drawerOpen.value = false
  },
)

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
      <div class="topbar-brand">
        <button type="button" class="nav-toggle" aria-label="打开导航菜单" @click="drawerOpen = true">
          <span></span><span></span><span></span>
        </button>
        <span class="brand">设备与环境统一管理面板</span>
      </div>
      <div class="topbar-actions">
        <span class="username">{{ username }}</span>
        <button type="button" class="logout-button" @click="onLogout">退出登录</button>
      </div>
    </header>

    <div class="layout-body">
      <div class="sidebar-mask" :class="{ show: drawerOpen }" @click="drawerOpen = false"></div>
      <aside class="sidebar" :class="{ open: drawerOpen }">
        <nav class="nav">
          <RouterLink class="nav-item" to="/" @click="drawerOpen = false">首页</RouterLink>
          <RouterLink class="nav-item" to="/targets" :class="{ active: route.name === 'targets' || route.name === 'target-detail' }" @click="drawerOpen = false">目标管理</RouterLink>
          <RouterLink class="nav-item" to="/agents" :class="{ active: route.name === 'agents' }" @click="drawerOpen = false">Agent 管理</RouterLink>
          <RouterLink class="nav-item" to="/metrics" :class="{ active: route.name === 'metrics' }" @click="drawerOpen = false">指标曲线</RouterLink>
          <RouterLink class="nav-item" to="/terminal" :class="{ active: route.name === 'terminal' }" @click="drawerOpen = false">Web 终端</RouterLink>
          <RouterLink class="nav-item" to="/terminal/records" :class="{ active: route.name === 'terminal-records' }" @click="drawerOpen = false">终端留痕</RouterLink>
          <RouterLink class="nav-item" to="/logs" :class="{ active: route.name === 'logs' }" @click="drawerOpen = false">日志查看</RouterLink>
          <RouterLink class="nav-item" to="/alerts" :class="{ active: route.name === 'alerts' }" @click="drawerOpen = false">告警规则</RouterLink>
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

.topbar-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.brand {
  font-size: 1rem;
  font-weight: 600;
}

/* 汉堡按钮（TOB-374 ②）：仅移动端显示 */
.nav-toggle {
  display: none;
  flex-direction: column;
  justify-content: center;
  gap: 4px;
  width: 36px;
  height: 36px;
  padding: 8px;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
}

.nav-toggle span {
  display: block;
  width: 100%;
  height: 2px;
  border-radius: 2px;
  background: var(--color-text);
}

.sidebar-mask {
  display: none;
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
  .nav-toggle {
    display: inline-flex;
  }

  /* 侧栏变抽屉：默认收起，.open 滑出；遮罩点击关闭 */
  .sidebar {
    position: fixed;
    top: var(--topbar-height);
    bottom: 0;
    left: 0;
    z-index: 60;
    width: var(--sidebar-width);
    max-width: 80vw;
    overflow-y: auto;
    transform: translateX(-100%);
    transition: transform 0.2s ease;
  }

  .sidebar.open {
    transform: translateX(0);
  }

  .sidebar-mask {
    display: block;
    position: fixed;
    top: var(--topbar-height);
    right: 0;
    bottom: 0;
    left: 0;
    z-index: 55;
    background: rgba(15, 23, 42, 0.45);
    opacity: 0;
    pointer-events: none;
    transition: opacity 0.2s ease;
  }

  .sidebar-mask.show {
    opacity: 1;
    pointer-events: auto;
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
