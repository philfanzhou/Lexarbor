<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { getBooks, addBook, updateBook, deleteBook } from '@/services/bookApi'
import type { Book } from '@/types'

const books = ref<Book[]>([])
const loading = ref(false)
const dialogVisible = ref(false)
const editingId = ref<string | null>(null)
const formRef = ref<FormInstance>()

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
  try {
    const data = await getBooks()
    books.value = data.books
  } catch (e: any) {
    ElMessage.error(e.message)
  } finally {
    loading.value = false
  }
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
  } catch (e: any) {
    ElMessage.error(e.message)
  }
}

async function handleDelete(book: Book) {
  await ElMessageBox.confirm(`确认删除教材「${book.bookName}」？`, '确认删除', { type: 'warning' })
  try {
    await deleteBook(book.id)
    ElMessage.success('删除成功')
    loadBooks()
  } catch (e: any) {
    ElMessage.error(e.message)
  }
}

onMounted(loadBooks)
</script>

<template>
  <div class="books-view">
    <div class="toolbar">
      <el-button type="primary" @click="openCreate">新增教材</el-button>
    </div>

    <el-table v-loading="loading" :data="books" stripe border>
      <el-table-column prop="bookName" label="教材名称" />
      <el-table-column prop="category" label="分类" width="100" />
      <el-table-column prop="educationLevel" label="学段" width="80" />
      <el-table-column prop="grade" label="年级" width="80" />
      <el-table-column prop="displayOrder" label="排序" width="60" />
      <el-table-column label="状态" width="80">
        <template #default="{ row }">
          <el-tag :type="row.status ? 'success' : 'info'">
            {{ row.status ? '启用' : '禁用' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="160" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link type="danger" @click="handleDelete(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialogVisible" :title="editingId ? '编辑教材' : '新增教材'" width="500px">
      <el-form ref="formRef" :model="form" :rules="rules" label-width="80px">
        <el-form-item label="名称" prop="bookName">
          <el-input v-model="form.bookName" placeholder="如：初中英语词汇" />
        </el-form-item>
        <el-form-item label="分类" prop="category">
          <el-input v-model="form.category" placeholder="如：初中英语" />
        </el-form-item>
        <el-form-item label="学段" prop="educationLevel">
          <el-input v-model="form.educationLevel" placeholder="如：初中" />
        </el-form-item>
        <el-form-item label="年级" prop="grade">
          <el-input v-model="form.grade" placeholder="如：初一" />
        </el-form-item>
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
.books-view { padding: 16px; }
.toolbar { margin-bottom: 16px; }
</style>
