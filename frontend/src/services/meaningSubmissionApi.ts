import api from './api'
import type { AxiosRequestConfig } from 'axios'
import { ApiError } from './apiError'

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
  reviewComment?: string | null
  definitionKept: boolean
  createdAt: string
  reviewedAt?: string | null
}

export interface MeaningSubmissionItem {
  submission: MeaningSubmission
  /** The subject of the integration client that submitted it. */
  source: string
  reviewerKind?: 'admin' | 'ai' | null
}

export interface AutomaticReviewStatus {
  enabled: boolean
  paused: boolean
  pauseCode: string | null
  pendingCount: number
  lastFailureCode: string | null
  lastFailureAt: string | null
  nextAttemptAt: string | null
}

export interface MeaningSubmissionPage {
  automaticReview?: AutomaticReviewStatus | null
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
  reviewComment?: string
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

export function listMeaningSubmissions(status: MeaningSubmissionStatus, page: number, size: number, reviewerKind?: string, config?: AxiosRequestConfig) {
  return api.get<MeaningSubmissionPage>('/admin/meaning-submissions', { ...config, params: { status, page, size, reviewerKind } })
}

export function approveMeaningSubmission(id: string, approval: MeaningSubmissionApproval, config?: AxiosRequestConfig) {
  return api.post<MeaningSubmission>(`/admin/meaning-submissions/${encodeURIComponent(id)}/approve`, approval, config)
}

export function rejectMeaningSubmission(id: string, reviewComment?: string, config?: AxiosRequestConfig) {
  return api.post<MeaningSubmission>(`/admin/meaning-submissions/${encodeURIComponent(id)}/reject`, reviewComment ? { reviewComment } : undefined, config)
}

export function adviseMeaningSubmission(id: string, config?: AxiosRequestConfig) {
  return api.post<MeaningSubmissionAdvice>(`/admin/meaning-submissions/${encodeURIComponent(id)}/advice`, undefined, config)
}

export interface MeaningSubmissionReview {
  recordId: string; submissionId: string; source: string; reviewerKind: 'ai'
  original: { word: string; partOfSpeech: string | null; meaning: string; example: string | null }
  ai: { verdict: 'accept' | 'revise' | 'reject'; partOfSpeech: string | null; meaning: string | null
    phoneticUk: string | null; phoneticUs: string | null; comment: string | null; model: string; reviewedAt: string }
  applied: boolean; checked: boolean; checkedAt: string | null; resultWordId: string | null
}
export interface ReviewQuery { verdict?: string; checked?: boolean; applied?: boolean; recordId?: string }
export interface ReviewPage { items: MeaningSubmissionReview[]; totalCount: number; page: number; size: number }
// Copy only the public projection, so unexpected configuration/runtime fields never enter page state.
function parseReview(value: unknown): MeaningSubmissionReview {
  const invalid = (): never => { throw new ApiError('无法读取审核记录。') }
  const object = (v: unknown): Record<string, unknown> => v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : invalid()
  const text = (v: unknown): string => typeof v === 'string' ? v : invalid()
  const optional = (v: unknown): string | null => v === null ? null : text(v)
  const bool = (v: unknown): boolean => typeof v === 'boolean' ? v : invalid()
  const v = object(value), original = object(v.original), ai = object(v.ai)
  const verdict = text(ai.verdict)
  if (!['accept', 'revise', 'reject'].includes(verdict) || v.reviewerKind !== 'ai') invalid()
  return { recordId: text(v.recordId), submissionId: text(v.submissionId), source: text(v.source), reviewerKind: 'ai',
    original: { word: text(original.word), partOfSpeech: optional(original.partOfSpeech), meaning: text(original.meaning), example: optional(original.example) },
    ai: { verdict: verdict as MeaningSubmissionReview['ai']['verdict'], partOfSpeech: optional(ai.partOfSpeech), meaning: optional(ai.meaning), phoneticUk: optional(ai.phoneticUk), phoneticUs: optional(ai.phoneticUs), comment: optional(ai.comment), model: text(ai.model), reviewedAt: text(ai.reviewedAt) },
    applied: bool(v.applied), checked: bool(v.checked), checkedAt: optional(v.checkedAt), resultWordId: optional(v.resultWordId) }
}
export async function listMeaningSubmissionReviews(query: ReviewQuery, page: number, config: AxiosRequestConfig): Promise<ReviewPage> {
  const result = await api.get<ReviewPage>('/admin/meaning-submission-reviews', { ...config, params: { ...query, page, size: 20 } })
  if (!Array.isArray(result.items) || !Number.isSafeInteger(result.totalCount) || result.totalCount < 0) throw new ApiError('无法读取审核记录。')
  return { items: result.items.map(parseReview), totalCount: result.totalCount, page: result.page, size: result.size }
}
export function checkMeaningSubmissionReview(recordId: string, config: AxiosRequestConfig) {
  return api.post<{ recordId: string; checkedAt: string }>(`/admin/meaning-submission-reviews/${encodeURIComponent(recordId)}/checked`, undefined, config)
}
