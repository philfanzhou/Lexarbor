import api from './api'

/**
 * The administrator review queue of meaning submissions
 * (`/admin/meaning-submissions`). Integration clients submit definitions that
 * no book has; approving one writes the reviewed definition into the
 * supplementary book the deployment configures.
 */

export type MeaningSubmissionStatus = 'pending' | 'approved' | 'rejected'

export interface MeaningSubmission {
  id: string
  word: string
  partOfSpeech?: string | null
  meaning: string
  example?: string | null
  status: MeaningSubmissionStatus
  resultBookId?: string | null
  resultWordId?: string | null
  resultMeaningId?: string | null
  resultWord?: string | null
  resultPartOfSpeech?: string | null
  resultMeaning?: string | null
  definitionKept: boolean
  createdAt: string
  reviewedAt?: string | null
}

export interface MeaningSubmissionItem {
  submission: MeaningSubmission
  /** The subject of the integration client that submitted it. */
  source: string
}

export interface MeaningSubmissionPage {
  configured: boolean
  bookId?: string | null
  assistantEnabled: boolean
  items: MeaningSubmissionItem[]
  totalCount: number
  totalPage: number
}

export interface MeaningSubmissionApproval {
  word: string
  partOfSpeech?: string
  meaning: string
  example?: string
  phoneticUk?: string
  phoneticUs?: string
}

/** Advice only: `verdict` is accept, revise or reject; the administrator decides. */
export interface MeaningSubmissionAdvice {
  verdict: 'accept' | 'revise' | 'reject'
  partOfSpeech?: string | null
  meaning?: string | null
  phoneticUk?: string | null
  phoneticUs?: string | null
  comment?: string | null
}

export function listMeaningSubmissions(status: MeaningSubmissionStatus, page: number, size: number) {
  return api.get<MeaningSubmissionPage>('/admin/meaning-submissions', { params: { status, page, size } })
}

export function approveMeaningSubmission(id: string, approval: MeaningSubmissionApproval) {
  return api.post<MeaningSubmission>(`/admin/meaning-submissions/${encodeURIComponent(id)}/approve`, approval)
}

export function rejectMeaningSubmission(id: string) {
  return api.post<MeaningSubmission>(`/admin/meaning-submissions/${encodeURIComponent(id)}/reject`)
}

export function adviseMeaningSubmission(id: string) {
  return api.post<MeaningSubmissionAdvice>(`/admin/meaning-submissions/${encodeURIComponent(id)}/advice`)
}
