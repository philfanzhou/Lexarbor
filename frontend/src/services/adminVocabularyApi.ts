import type { AxiosRequestConfig } from 'axios'
import api from './api'
import type { Book } from '@/types'

/**
 * Typed reads for the administration vocabulary queries (`GET /admin/vocabulary`,
 * `GET /admin/vocabulary/{wordId}`, `GET /admin/vocabulary-books/{bookId}/content`,
 * `GET /admin/vocabulary-books/{bookId}/units/{unitId}/content`).
 * They return disabled books and unassigned words, which the public
 * enabled-only endpoints never do. Also the full-replacement writes the
 * administration UI edits and cleans with: the two complete-replacement PUTs
 * of the detail drawer (unlike the import merge, keeping a value means
 * sending it again and every field is required on each request) and the
 * cleanup pair of one preview plus one commit of the same scope, matching
 * the server's strict per-action field whitelist.
 */

/** A book membership of a word. Disabled books are included with status false. */
export interface AdminBookRef {
  id: string
  bookName: string
  status: boolean
}

/** A unit a meaning is assigned to, in unit-number order. `section` is `A` or `B` when the assignment sits in one of the unit's sections; `entryKind` is `word` or `phrase` when the entry is classified; absent in older reads. */
export interface AdminMeaningUnit {
  unitId: string
  number: number
  title?: string | null
  section?: string | null
  entryKind?: string | null
}

/** One meaning record; it belongs to exactly one book. */
export interface AdminMeaning {
  id: string
  vocabularyId: string
  bookId: string
  partOfSpeech?: string | null
  meaning: string
  example?: string | null
  /** The units this meaning is assigned to; absent in older reads. */
  units?: AdminMeaningUnit[]
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

/** The unit a unit-content response is scoped to. */
export interface AdminUnitRef {
  id: string
  bookId: string
  number: number
  title?: string | null
}

/** How many assignment places the unit holds per section, always counted over the whole unit. */
export interface AdminSectionCounts {
  sectionA: number
  sectionB: number
  noSection: number
}

/** How many assignment places the unit holds per entry kind, always counted over the whole unit. */
export interface AdminEntryKindCounts {
  word: number
  phrase: number
  none: number
}

/**
 * One unit's content page: same shape as the whole-book page scoped to the
 * unit — counts are unit-scoped whatever the keyword, and each item's meanings
 * are only those assigned to the unit, narrowed to the requested section's
 * places and entry kind's places when either was asked for, while
 * `sectionCounts` and `entryKindCounts` always report the whole unit. A
 * missing unit and a unit of another book both answer 404.
 */
export interface AdminUnitContent {
  book: Book
  unit: AdminUnitRef
  wordCount: number
  meaningCount: number
  sectionCounts: AdminSectionCounts
  entryKindCounts: AdminEntryKindCounts
  items: AdminWordDetail[]
  totalCount: number
  totalPage: number
}

export interface AdminWordListQuery {
  keyword?: string
  page?: number
  size?: number
}

/** The unit-content page's section filter: one section's places, or the unsectioned ones. */
export type AdminUnitSectionFilter = 'A' | 'B' | 'none'

/** The unit-content page's entry-kind filter: one kind's places, or the unclassified ones. */
export type AdminUnitEntryKindFilter = 'word' | 'phrase' | 'none'

/** One classified assignment, with an exact book/unit/meaning/section identity. */
export interface AdminPhrasePosition {
  bookId: string
  unitId: string
  meaningId: string
  section: 'A' | 'B' | null
  entryKind: 'phrase'
  number: number
  title: string | null
  wordId: string
  word: string
  phoneticUk: string | null
  phoneticUs: string | null
  partOfSpeech: string | null
  meaning: string
  example: string | null
}

export interface AdminPhrasePositionPage {
  items: AdminPhrasePosition[]
  totalCount: number
  totalPage: number
}

/** The complete identity of one assignment; null is sent explicitly. */
export interface AdminMeaningPositionKey {
  unitId: string
  section: 'A' | 'B' | null
  entryKind: 'word' | 'phrase' | null
}

export interface AdminMeaningPositionMove {
  from: AdminMeaningPositionKey
  to: AdminMeaningPositionKey
}

export function moveAdminMeaningPosition(bookId: string, meaningId: string, data: AdminMeaningPositionMove) {
  return api.put<{ success: boolean }>(`/admin/vocabulary-books/${bookId}/meanings/${meaningId}/positions`, data)
}

export function removeAdminMeaningPosition(bookId: string, meaningId: string, position: AdminMeaningPositionKey) {
  return api.delete<{ success: boolean }>(
    `/admin/vocabulary-books/${bookId}/meanings/${meaningId}/positions/${position.unitId}`,
    { params: { section: position.section ?? 'none', entryKind: position.entryKind ?? 'none' } }
  )
}

/** Counts and pages phrase positions, including those in disabled books. */
export function getAdminPhrasePositions(
  bookId: string,
  params: AdminWordListQuery & { unitId?: string; section?: AdminUnitSectionFilter },
  config?: AxiosRequestConfig
) {
  return api.get<AdminPhrasePositionPage>(`/admin/vocabulary-books/${bookId}/phrase-positions`, {
    params,
    ...config
  })
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

/** One unit's word list, in the same shape as the whole-book page. `section` and `entryKind` narrow the page and the counts to that dimension's places of the unit, and combine by intersection. */
export function getAdminUnitContent(
  bookId: string,
  unitId: string,
  params: AdminWordListQuery & { section?: AdminUnitSectionFilter; entryKind?: AdminUnitEntryKindFilter },
  config?: AxiosRequestConfig
) {
  return api.get<AdminUnitContent>(`/admin/vocabulary-books/${bookId}/units/${unitId}/content`, {
    params,
    ...config
  })
}

/**
 * The full replacement of the shared spelling and phonetics. The three fields
 * are sent on every request; a null phonetic clears it and keeping a value
 * means sending it again. The write affects every book referencing the word.
 */
export interface AdminWordEditPayload {
  word: string
  phoneticUk: string | null
  phoneticUs: string | null
}

/** The full replacement of one book-owned meaning; ownership cannot move. */
export interface AdminMeaningEditPayload {
  partOfSpeech: string | null
  meaning: string
  example: string | null
}

/** Replaces the shared word fields (`PUT /admin/vocabulary/{wordId}`). */
export function updateAdminVocabularyWord(
  wordId: string,
  payload: AdminWordEditPayload,
  config?: AxiosRequestConfig
) {
  return api.put<{ success: boolean }>(`/admin/vocabulary/${wordId}`, payload, config)
}

/** Replaces one meaning's three fields (`PUT /admin/vocabulary-books/{bookId}/words/{wordId}/meanings/{meaningId}`). */
export function updateAdminMeaning(
  bookId: string,
  wordId: string,
  meaningId: string,
  payload: AdminMeaningEditPayload,
  config?: AxiosRequestConfig
) {
  return api.put<{ success: boolean }>(
    `/admin/vocabulary-books/${bookId}/words/${wordId}/meanings/${meaningId}`,
    payload,
    config
  )
}

/** What a cleanup removes: one meaning, the selected words, or the whole book. */
export type AdminCleanupSelection =
  | { action: 'removeMeaning'; wordId: string; meaningId: string }
  | { action: 'removeWords'; wordIds: string[] }
  | { action: 'clear' }
  | { action: 'delete' }

/** A commit scope: the selection, with the exact book name a delete must confirm. */
export type AdminCleanupCommit =
  | Exclude<AdminCleanupSelection, { action: 'delete' }>
  | { action: 'delete'; confirmedBookName: string }

/** The read-only estimate a confirmation shows before anything is removed. */
export interface AdminCleanupPreview {
  bookId: string
  bookName: string
  action: AdminCleanupSelection['action']
  affectedWordCount: number
  meaningCount: number
  orphanWordCount: number
}

/** What the transaction actually deleted; counts come from the server, never the client. */
export interface AdminCleanupResult {
  bookId: string
  action: AdminCleanupSelection['action']
  affectedWordCount: number
  deletedMeaningCount: number
  deletedWordCount: number
  deletedBook: boolean
}

/** Estimates a scope without writing (`POST /admin/vocabulary-books/{bookId}/cleanup/preview`). */
export function previewAdminBookCleanup(
  bookId: string,
  selection: AdminCleanupSelection,
  config?: AxiosRequestConfig
) {
  return api.post<AdminCleanupPreview>(
    `/admin/vocabulary-books/${bookId}/cleanup/preview`,
    selection,
    config
  )
}

/** Commits a scope in one transaction (`POST /admin/vocabulary-books/{bookId}/cleanup`). */
export function commitAdminBookCleanup(
  bookId: string,
  commit: AdminCleanupCommit,
  config?: AxiosRequestConfig
) {
  return api.post<AdminCleanupResult>(`/admin/vocabulary-books/${bookId}/cleanup`, commit, config)
}
