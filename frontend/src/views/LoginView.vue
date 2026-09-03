<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { login } from '@/api/auth'

const route = useRoute()
const router = useRouter()

const username = ref('')
const password = ref('')
const errorMessage = ref('')
const submitting = ref(false)

async function onSubmit() {
  if (submitting.value) {
    return
  }
  errorMessage.value = ''
  submitting.value = true
  try {
    await login(username.value.trim(), password.value)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
    await router.replace(redirect)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '登录失败，请稍后重试'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <form class="login-card" @submit.prevent="onSubmit">
      <h1 class="login-title">设备与环境统一管理面板</h1>
      <p class="login-subtitle">请登录以继续</p>

      <label class="field">
        <span class="field-label">用户名</span>
        <input
          v-model="username"
          type="text"
          name="username"
          autocomplete="username"
          placeholder="请输入用户名"
          required
        />
      </label>

      <label class="field">
        <span class="field-label">密码</span>
        <input
          v-model="password"
          type="password"
          name="password"
          autocomplete="current-password"
          placeholder="请输入密码"
          required
        />
      </label>

      <p v-if="errorMessage" class="login-error" role="alert">{{ errorMessage }}</p>

      <button type="submit" class="login-button" :disabled="submitting">
        {{ submitting ? '登录中…' : '登 录' }}
      </button>
    </form>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
  background: linear-gradient(160deg, #1e3a8a 0%, #2563eb 55%, #3b82f6 100%);
}

.login-card {
  width: 100%;
  max-width: 380px;
  background: var(--color-surface);
  border-radius: 12px;
  padding: 32px 28px;
  box-shadow: 0 16px 40px rgb(0 0 0 / 25%);
}

.login-title {
  margin: 0 0 8px;
  font-size: 1.25rem;
  text-align: center;
  color: var(--color-text);
}

.login-subtitle {
  margin: 0 0 24px;
  text-align: center;
  font-size: 0.9rem;
  color: var(--color-text-light);
}

.field {
  display: block;
  margin-bottom: 16px;
}

.field-label {
  display: block;
  margin-bottom: 6px;
  font-size: 0.875rem;
  color: var(--color-text);
}

.field input {
  width: 100%;
  padding: 10px 12px;
  border: 1px solid var(--color-border);
  border-radius: 8px;
  font-size: 1rem;
  outline: none;
  transition: border-color 0.15s ease;
}

.field input:focus {
  border-color: var(--color-primary);
}

.login-error {
  margin: 0 0 12px;
  padding: 8px 12px;
  border-radius: 8px;
  background: #fef2f2;
  color: var(--color-danger);
  font-size: 0.875rem;
}

.login-button {
  width: 100%;
  padding: 11px 0;
  border: none;
  border-radius: 8px;
  background: var(--color-primary);
  color: #fff;
  font-size: 1rem;
  cursor: pointer;
  transition: background-color 0.15s ease;
}

.login-button:hover:not(:disabled) {
  background: var(--color-primary-dark);
}

.login-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

@media (max-width: 480px) {
  .login-card {
    padding: 24px 20px;
    border-radius: 10px;
  }

  .login-title {
    font-size: 1.1rem;
  }
}
</style>
