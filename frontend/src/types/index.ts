export interface Book {
  id: string
  bookName: string
  description?: string
  publisher?: string
  educationLevel?: string
  grade?: string
  category?: string
  displayOrder: number
  status: boolean
  iconUrl?: string
}

/** One unit of a book, as the administration units API returns it. */
export interface BookUnit {
  id: string
  bookId: string
  number: number
  title?: string | null
  /** Display snapshot of how many meanings are assigned to the unit. */
  meaningCount: number
}

export interface VocabularyMeaning {
  vocabularyId?: string
  bookId: string
  partOfSpeech?: string
  meaning: string
  example?: string
}

export interface BookListResponse {
  success: boolean
  data: { books: Book[] }
}

export interface BookPageData {
  items: Book[]
  totalCount: number
  totalPage: number
}

export interface StringListData {
  items: string[]
}

export interface BoolResponse {
  success: boolean
  data: { success: boolean }
}
