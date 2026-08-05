import api from './api'
import type { Book, BookPageData, StringListData } from '@/types'

export interface BookListData {
  books: Book[]
}

export const getBooks = (params?: { keyword?: string; page?: number; size?: number }) =>
  api.get<BookPageData>('/admin/vocabulary-books', { params })

export const addBook = (data: Partial<Book>) =>
  api.post<{ id: string }>('/admin/vocabulary-books', data)

export const updateBook = (data: Book) =>
  api.put('/admin/vocabulary-books', data)

export const deleteBook = (id: string) =>
  api.delete(`/admin/vocabulary-books/${id}`)

export const getCategories = () =>
  api.get<StringListData>('/admin/vocabulary-books/categories')

export const getEducationLevels = () =>
  api.get<StringListData>('/admin/vocabulary-books/education-levels')
