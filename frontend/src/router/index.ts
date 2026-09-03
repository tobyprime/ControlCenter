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
          path: 'devices',
          name: 'devices',
          component: () => import('@/views/DevicesView.vue'),
          meta: { title: '设备管理' },
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
