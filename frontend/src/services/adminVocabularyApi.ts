import type { AxiosRequestConfig } from 'axios'
import api from './api'
import type { Book } from '@/types'

/**
 * Typed reads for the administration vocabulary queries (`GET /admin/vocabulary`,
 * `GET /admin/vocabulary/{wordId}`, `GET /admin/vocabulary-books/{bookId}/content`).
 * They return disabled books and unassigned words, which the public
 * enabled-only endpoints never do. Read-only: no write helper belongs here.
 */

/** A book membership of a word. Disabled books are included with status false. */
export interface AdminBookRef {
  id: string
  bookName: string
  status: boolean
}

/** One meaning record; it belongs to exactly one book. */
export interface AdminMeaning {
  id: string
  vocabularyId: string
  bookId: string
  partOfSpeech?: string | null
  meaning: string
  example?: string | null
}

/**
 * The deduplicated word summary. A word with several meanings in one book
 * appears once, with that book once in `books`.
 */
export interface AdminWordSummary {
  id: string
  word: string
  phoneticUk?: string | null
  phoneticUs?: string | null
  books: AdminBookRef[]
}

/** A word summary with meanings: all books in the detail, one book in a book list. */
export interface AdminWordDetail extends AdminWordSummary {
  meanings: AdminMeaning[]
}

export interface AdminWordPage {
  items: AdminWordSummary[]
  totalCount: number
  totalPage: number
}

/** The whole-book content page: whole-book counts plus the keyword-matching page. */
export interface AdminBookContent {
  book: Book
  wordCount: number
  meaningCount: number
  items: AdminWordDetail[]
  totalCount: number
  totalPage: number
}

export interface AdminWordListQuery {
  keyword?: string
  page?: number
  size?: number
}

/** The whole library, deduplicated; `bookId` selects memberships, it does not trim them. */
export function searchAdminVocabulary(
  params: AdminWordListQuery & { bookId?: string },
  config?: AxiosRequestConfig
) {
  return api.get<AdminWordPage>('/admin/vocabulary', { params, ...config })
}

/** Every meaning of one word, across all books including disabled ones. */
export function getAdminVocabularyWord(wordId: string, config?: AxiosRequestConfig) {
  return api.get<AdminWordDetail>(`/admin/vocabulary/${wordId}`, config)
}

/** One book's word list. `wordCount`/`meaningCount` count the whole book, not the keyword match. */
export function getAdminBookContent(
  bookId: string,
  params: AdminWordListQuery,
  config?: AxiosRequestConfig
) {
  return api.get<AdminBookContent>(`/admin/vocabulary-books/${bookId}/content`, {
    params,
    ...config
  })
}
