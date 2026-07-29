<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { login } from '@/services/authState'
import { getApiError } from '@/services/apiError'

const route = useRoute()
const router = useRouter()
const formRef = ref<FormInstance>()
const loading = ref(false)
const form = ref({
  username: '',
  password: ''
})

const rules: FormRules = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }]
}

function getRedirectTarget() {
  const redirect = route.query.redirect
  return typeof redirect === 'string' && redirect.startsWith('/') && redirect !== '/login'
    ? redirect
    : '/books'
}

async function handleLogin() {
  await formRef.value?.validate()
  loading.value = true
  try {
    await login(form.value.username.trim(), form.value.password)
    form.value.password = ''
    await router.replace(getRedirectTarget())
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === 401) {
      ElMessage.error('用户名或密码错误')
    } else if (apiError.status === 403) {
      ElMessage.error('当前账户没有管理员权限')
    } else {
      ElMessage.error(apiError.message)
    }
  } finally {
    form.value.password = ''
    loading.value = false
  }
}
</script>

<template>
  <div class="auth-page">
    <el-card class="auth-card" shadow="never">
      <template #header>
        <div class="auth-card__heading">
          <h1>词汇管理</h1>
          <p>请使用 Identity 管理员账户登录</p>
        </div>
      </template>
      <el-form
        ref="formRef"
        :model="form"
        :rules="rules"
        label-position="top"
        @keyup.enter="handleLogin"
      >
        <el-form-item label="用户名" prop="username">
          <el-input
            v-model="form.username"
            autocomplete="username"
            placeholder="请输入用户名"
          />
        </el-form-item>
        <el-form-item label="密码" prop="password">
          <el-input
            v-model="form.password"
            type="password"
            autocomplete="current-password"
            placeholder="请输入密码"
            show-password
          />
        </el-form-item>
        <el-button
          class="auth-card__action"
          type="primary"
          :loading="loading"
          @click="handleLogin"
        >
          登录
        </el-button>
      </el-form>
    </el-card>
  </div>
</template>
