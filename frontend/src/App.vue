<script setup lang="ts">
import { RouterLink, RouterView } from 'vue-router'
</script>

<template>
  <div class="vocabulary-admin">
    <header class="app-header">
      <div class="brand">词汇管理</div>
      <nav class="nav">
        <RouterLink to="/books">教材管理</RouterLink>
        <RouterLink to="/import">单词导入</RouterLink>
      </nav>
    </header>
    <main class="app-main">
      <RouterView />
    </main>
  </div>
</template>

<style scoped>
.vocabulary-admin {
  min-height: 100vh;
  background: #f5f7fa;
}
.app-header {
  display: flex;
  align-items: center;
  gap: 32px;
  height: 56px;
  padding: 0 24px;
  background: #fff;
  border-bottom: 1px solid #ebeef5;
}
.brand {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}
.nav {
  display: flex;
  gap: 24px;
}
.nav a {
  color: #606266;
  text-decoration: none;
  font-size: 14px;
}
.nav a.router-link-active {
  color: #409eff;
  font-weight: 600;
}
.app-main {
  max-width: 1200px;
  margin: 0 auto;
}
</style>
