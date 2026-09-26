<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { getBooks, addBook, updateBook, deleteBook, getCategories, getEducationLevels } from '@/services/bookApi'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import type { Book } from '@/types'

const books = ref<Book[]>([])
const loading = ref(false)
const dialogVisible = ref(false)
const editingId = ref<string | null>(null)
const formRef = ref<FormInstance>()

const categories = ref<string[]>([])
const educationLevels = ref<string[]>([])

const keyword = ref('')
const page = ref(1)
const size = ref(20)
const totalCount = ref(0)
const pageSizes = [10, 20, 50, 100]

// For display only: which empty state to show, and whether the list failed.
const searchedKeyword = ref('')
const loadError = ref('')

const form = ref<Partial<Book>>({
  bookName: '',
  description: '',
  publisher: '',
  educationLevel: '',
  grade: '',
  category: '',
  displayOrder: 0,
  status: true,
  iconUrl: ''
})

const rules: FormRules = {
  bookName: [{ required: true, message: '请输入教材名称', trigger: 'blur' }]
}

async function loadBooks() {
  loading.value = true
  searchedKeyword.value = keyword.value
  try {
    const data = await getBooks({ keyword: keyword.value || undefined, page: page.value, size: size.value })
    books.value = data.items
    totalCount.value = data.totalCount
    loadError.value = ''
  } catch (error: unknown) {
    const message = getApiError(error).message
    ElMessage.error(message)
    loadError.value = message
  } finally {
    loading.value = false
  }
}

async function loadFilters() {
  try {
    const [catRes, levelRes] = await Promise.all([getCategories(), getEducationLevels()])
    categories.value = catRes.items
    educationLevels.value = levelRes.items
  } catch (error: unknown) {
    ElMessage.error(getApiError(error).message)
  }
}

function handleSearch() {
  page.value = 1
  loadBooks()
}

function handleSizeChange(newSize: number) {
  size.value = newSize
  page.value = 1
  loadBooks()
}

function handlePageChange(newPage: number) {
  page.value = newPage
  loadBooks()
}

function openCreate() {
  editingId.value = null
  form.value = {
    bookName: '', description: '', publisher: '',
    educationLevel: '', grade: '', category: '',
    displayOrder: 0, status: true, iconUrl: ''
  }
  dialogVisible.value = true
}

function openEdit(book: Book) {
  editingId.value = book.id
  form.value = { ...book }
  dialogVisible.value = true
}

async function handleSubmit() {
  await formRef.value?.validate()
  try {
    if (editingId.value) {
      await updateBook(form.value as Book)
      ElMessage.success('更新成功')
    } else {
      await addBook(form.value)
      ElMessage.success('新增成功')
    }
    dialogVisible.value = false
    loadBooks()
    loadFilters()
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === 404) {
      ElMessage.error('教材不存在或已被删除')
    } else if (apiError.status === 409) {
      ElMessage.error('教材数据已发生冲突，请刷新后重试')
    } else if (apiError.status === 422) {
      ElMessage.error('教材信息不符合业务规则')
    } else {
      ElMessage.error(apiError.message)
    }
  }
}

async function handleDelete(book: Book) {
  await ElMessageBox.confirm(`确认删除教材「${book.bookName}」？`, '确认删除', { type: 'warning' })
  try {
    await deleteBook(book.id)
    ElMessage.success('删除成功')
    loadBooks()
    loadFilters()
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === 404) {
      ElMessage.error('教材不存在或已被删除')
    } else if (apiError.status === 409) {
      ElMessage.error('教材已被词义引用，不能删除；请编辑并禁用该教材')
    } else {
      ElMessage.error(apiError.message)
    }
  }
}

onMounted(() => {
  loadBooks()
  loadFilters()
})
</script>

<template>
  <div class="books-view">
    <PageHeader title="教材管理" description="维护教材信息，启用或停用教材">
      <template #actions>
        <el-button type="primary" @click="openCreate">新增教材</el-button>
      </template>
    </PageHeader>

    <section class="books-panel">
      <div class="toolbar">
        <el-input
          v-model="keyword"
          class="toolbar__search"
          aria-label="搜索教材名称"
          placeholder="搜索教材名称"
          clearable
          @keyup.enter="handleSearch"
        />
        <el-button type="primary" @click="handleSearch">搜索</el-button>
      </div>

      <el-alert
        v-if="loadError"
        class="books-error"
        type="error"
        :title="loadError"
        :closable="false"
        show-icon
      >
        <el-button class="books-error__retry" size="small" @click="loadBooks">重试</el-button>
      </el-alert>

      <el-table v-loading="loading" :data="books" stripe border scrollbar-tabindex="0">
        <el-table-column prop="bookName" label="教材名称" min-width="180" />
        <el-table-column prop="category" label="分类" min-width="100" />
        <el-table-column prop="educationLevel" label="学段" min-width="110" />
        <el-table-column prop="grade" label="年级" min-width="100" />
        <el-table-column prop="displayOrder" label="排序" min-width="70" />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.status ? 'success' : 'info'">
              {{ row.status ? '启用' : '禁用' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="140" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-button link type="danger" @click="handleDelete(row)">删除</el-button>
          </template>
        </el-table-column>
        <template #empty>
          <el-empty
            v-if="!loadError"
            :image-size="80"
            :description="searchedKeyword ? '没有匹配的教材' : '还没有教材，点击「新增教材」创建'"
          />
        </template>
      </el-table>

      <div class="pagination-wrapper">
        <el-select
          v-model="size"
          class="page-size"
          aria-label="每页条数"
          @change="handleSizeChange"
        >
          <el-option v-for="option in pageSizes" :key="option" :label="`${option} 条/页`" :value="option" />
        </el-select>
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="size"
          :total="totalCount"
          layout="total, prev, pager, next, jumper"
          background
          @size-change="handleSizeChange"
          @current-change="handlePageChange"
        />
      </div>
    </section>

    <el-dialog
      v-model="dialogVisible"
      :title="editingId ? '编辑教材' : '新增教材'"
      width="min(560px, calc(100vw - 32px))"
    >
      <el-form ref="formRef" class="book-form" :model="form" :rules="rules" label-width="80px">
        <h3 class="book-form__group">基本信息</h3>
        <el-form-item label="名称" prop="bookName">
          <el-input v-model="form.bookName" placeholder="如：初中英语词汇" />
        </el-form-item>
        <el-form-item label="分类" prop="category">
          <el-select v-model="form.category" placeholder="请选择分类" clearable>
            <el-option v-for="cat in categories" :key="cat" :label="cat" :value="cat" />
          </el-select>
        </el-form-item>
        <el-form-item label="学段" prop="educationLevel">
          <el-select v-model="form.educationLevel" placeholder="请选择学段" clearable>
            <el-option v-for="level in educationLevels" :key="level" :label="level" :value="level" />
          </el-select>
        </el-form-item>
        <el-form-item label="年级" prop="grade">
          <el-input v-model="form.grade" placeholder="如：初一" />
        </el-form-item>

        <h3 class="book-form__group">其他</h3>
        <el-form-item label="出版社" prop="publisher">
          <el-input v-model="form.publisher" />
        </el-form-item>
        <el-form-item label="排序" prop="displayOrder">
          <el-input-number v-model="form.displayOrder" :min="0" />
        </el-form-item>
        <el-form-item label="状态" prop="status">
          <el-switch v-model="form.status" />
        </el-form-item>
        <el-form-item label="描述" prop="description">
          <el-input v-model="form.description" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" @click="handleSubmit">确定</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.books-panel {
  padding: var(--lx-space-4);
  border: 1px solid var(--lx-color-border-light);
  border-radius: var(--lx-radius-md);
  background: var(--lx-color-bg-surface);
  box-shadow: var(--lx-shadow-1);
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lx-space-3);
  margin-bottom: var(--lx-space-4);
}
.toolbar__search {
  width: min(320px, 100%);
}

.books-error {
  margin-bottom: var(--lx-space-4);
}
.books-error__retry {
  margin-top: var(--lx-space-2);
}

.pagination-wrapper {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: var(--lx-space-3);
  margin-top: var(--lx-space-4);
}
.page-size {
  width: 120px;
}

.book-form__group {
  margin: 0 0 var(--lx-space-3);
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-sm);
  font-weight: 600;
}
.book-form__group ~ .book-form__group {
  margin-top: var(--lx-space-5);
  padding-top: var(--lx-space-4);
  border-top: 1px solid var(--lx-color-border-light);
}
.book-form :deep(.el-select) {
  width: 100%;
}
</style>
