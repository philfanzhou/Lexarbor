import api from './api'
import type { Book, BookPageData, StringListData } from '@/types'

export interface BookListData {
  books: Book[]
}

export const getBooks = (params?: { keyword?: string; page?: number; size?: number }) =>
  api.get<BookPageData>('/admin/vocabulary-books', { params })

/**
 * Every enabled book, unpaged, for a picker rather than a table.
 *
 * The paged administration search is the wrong source for that. Called with no
 * paging parameters, which is how a picker wants to call it, it does not return
 * everything -- it returns a 400, because those parameters were required.
 * Supplying a page instead would have traded that for a silently short list:
 * twenty books, no page control, nothing on screen to say more exist.
 *
 * This endpoint also returns only enabled books, which is the set a word can
 * actually be imported into -- a disabled book was offered and then refused
 * with a 422 the administrator had no way to predict.
 */
export const getActiveBooks = () =>
  api.get<BookListData>('/api/vocabulary-books/all')

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
