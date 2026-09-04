import { createRouter, createWebHistory } from 'vue-router'
import { me, type SessionInfo } from '@/api/auth'

let session: SessionInfo | null = null
let sessionPromise: Promise<SessionInfo> | null = null

export async function fetchSession(): Promise<SessionInfo> {
  if (session) {
    return session
  }
  sessionPromise ??= me()
  try {
    session = await sessionPromise
  } catch {
    session = null
  }
  sessionPromise = null
  return session ?? Promise.reject(new Error('未登录'))
}

export function clearSession(): void {
  session = null
}

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
      meta: { public: true },
    },
    {
      path: '/',
      component: () => import('@/layouts/MainLayout.vue'),
      children: [
        {
          path: '',
          name: 'home',
          component: () => import('@/views/HomeView.vue'),
          meta: { title: '首页' },
        },
        {
          path: 'targets',
          name: 'targets',
          component: () => import('@/views/TargetsView.vue'),
          meta: { title: '目标管理' },
        },
        {
          path: 'targets/:id',
          name: 'target-detail',
          component: () => import('@/views/TargetDetailView.vue'),
          meta: { title: '目标详情' },
        },
        {
          path: 'metrics',
          name: 'metrics',
          component: () => import('@/views/MetricsView.vue'),
          meta: { title: '指标曲线' },
        },
        {
          path: 'terminal',
          name: 'terminal',
          component: () => import('@/views/TerminalView.vue'),
          meta: { title: 'Web 终端' },
        },
        {
          path: 'terminal/records',
          name: 'terminal-records',
          component: () => import('@/views/TerminalRecordsView.vue'),
          meta: { title: '终端留痕' },
        },
        {
          path: 'logs',
          name: 'logs',
          component: () => import('@/views/LogsView.vue'),
          meta: { title: '日志查看' },
        },
        {
          path: 'alerts',
          name: 'alerts',
          component: () => import('@/views/AlertsView.vue'),
          meta: { title: '告警规则' },
        },
      ],
    },
    {
      path: '/:pathMatch(.*)*',
      redirect: '/',
    },
  ],
})

router.beforeEach(async (to) => {
  if (to.meta.public) {
    return true
  }
  try {
    await fetchSession()
    return true
  } catch {
    return { name: 'login', query: { redirect: to.fullPath } }
  }
})

export default router
