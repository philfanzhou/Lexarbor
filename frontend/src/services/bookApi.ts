import type { AxiosRequestConfig } from 'axios'
import api from './api'
import type { Book, BookPageData, BookUnit, StringListData } from '@/types'

export interface BookListData {
  books: Book[]
}

export interface BookUnitListData {
  units: BookUnit[]
}

/**
 * The body of both unit writes. A replace, not a merge: both fields are sent
 * every time, with `null` as the explicit form of an absent title, matching
 * the server's strict request shape.
 */
export interface BookUnitWrite {
  number: number
  title: string | null
}

export const getBooks = (
  params?: { keyword?: string; page?: number; size?: number },
  config?: AxiosRequestConfig
) => api.get<BookPageData>('/admin/vocabulary-books', { params, ...config })

/**
 * Every enabled book, unpaged, for a picker rather than a table.
 *
 * The paged administration search is the wrong source for that. Called with no
 * paging parameters, which is how a picker wants to call it, it does not return
 * everything -- it returns the default first page of twenty books.
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

/**
 * One book's units in unit-number order, each with its assignment count.
 * Disabled books are managed the same as enabled ones; a missing book answers
 * 404.
 */
export const getBookUnits = (bookId: string, config?: AxiosRequestConfig) =>
  api.get<BookUnitListData>(`/admin/vocabulary-books/${bookId}/units`, config)

/** Creates a unit; a duplicate number in the same book answers 409. */
export const addBookUnit = (bookId: string, data: BookUnitWrite, config?: AxiosRequestConfig) =>
  api.post<BookUnit>(`/admin/vocabulary-books/${bookId}/units`, data, config)

/** Replaces a unit's number and title; keeping its own number is not a conflict. */
export const updateBookUnit = (
  bookId: string,
  unitId: string,
  data: BookUnitWrite,
  config?: AxiosRequestConfig
) => api.put<BookUnit>(`/admin/vocabulary-books/${bookId}/units/${unitId}`, data, config)

/** Removes the unit and its assignments; meanings and words survive. */
export const deleteBookUnit = (bookId: string, unitId: string, config?: AxiosRequestConfig) =>
  api.delete<{ success: boolean }>(`/admin/vocabulary-books/${bookId}/units/${unitId}`, config)

export const getCategories = () =>
  api.get<StringListData>('/admin/vocabulary-books/categories')

export const getEducationLevels = () =>
  api.get<StringListData>('/admin/vocabulary-books/education-levels')
