import api from './api'
import type { Book } from '@/types'

export interface BookListData {
  books: Book[]
}

export const getBooks = () => api.get<BookListData>('/admin/vocabulary-books')

export const addBook = (data: Partial<Book>) =>
  api.post<{ id: string }>('/admin/vocabulary-books', data)

export const updateBook = (data: Book) =>
  api.put('/admin/vocabulary-books', data)

export const deleteBook = (id: string) =>
  api.delete(`/admin/vocabulary-books/${id}`)
