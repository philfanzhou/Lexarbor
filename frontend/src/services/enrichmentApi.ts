import api from './api'
import { ApiError } from './apiError'

export const states = ['Pending', 'Running', 'Waiting', 'Succeeded', 'Failed', 'Cancelled'] as const
export const steps = ['Prepare', 'Fields', 'Audio'] as const
export type TaskState = typeof states[number]
export interface EnrichmentQuery {
  wordId?: string; meaningId?: string; locale?: string; state?: string; step?: string
}
export interface EnrichmentPage<T> { items: T[]; totalCount: number; page: number; size: number }
export interface EnrichmentRound {
  number: number; state: TaskState; requestCount: number; consumedFailures: number; failureBudget: number
  waitReason: string | null; errorCode: string | null; nextAttemptAtUnixMs: number | null
  createdAtUnixMs: number; updatedAtUnixMs: number
}
export interface EnrichmentCleanup {
  waiting: number; running: number; observed: number; attempts: number
  nextAttemptAtUnixMs: number | null; errorCode: string | null
}
export interface EnrichmentTask {
  id: string; wordId: string; word: string | null; meaningId: string | null; meaning: string | null
  locale: string; step: typeof steps[number]; applicable: boolean; missing: boolean; inheritsDefault: boolean
  state: TaskState; waitReason: string | null; current: EnrichmentRound; cleanup: EnrichmentCleanup
}
export interface EnrichmentCall {
  id: string; round: number; service: string; result: string; errorCode: string | null
  startedAtUnixMs: number; finishedAtUnixMs: number | null
}
export interface EnrichmentWait {
  round: number; reason: string; errorCode: string | null; recordedAtUnixMs: number; nextAttemptAtUnixMs: number
}
export interface CleanupHistory {
  epoch: number; state: string; attempts: number; nextAttemptAtUnixMs: number
  errorCode: string | null; createdAtUnixMs: number; updatedAtUnixMs: number
}
export interface EnrichmentDetail {
  task: EnrichmentTask; rounds: EnrichmentPage<EnrichmentRound>; calls: EnrichmentPage<EnrichmentCall>
  waits: EnrichmentPage<EnrichmentWait>; cleanup: EnrichmentPage<CleanupHistory>
}
export interface EnrichmentSummary {
  applicableTargets: number; completedTargets: number; missingTargets: number; unqueuedTargets: number; persistedTasks: number
  targets: { state: TaskState; waitReason: string | null; count: number }[]
  services: { service: string; paused: boolean; blocked: boolean; consecutiveFailures: number; throttleSeconds: number; nextAttemptAtUnixMs: number | null }[]
}

// Copy the safe DTO only. Extra configuration, provider or credential properties
// never become page state, even if a malformed server response includes them.
type ObjectValue = Record<string, unknown>
function invalid(): never { throw new ApiError('无法读取有效的自治补齐状态。') }
function object(value: unknown): ObjectValue { return value && typeof value === 'object' && !Array.isArray(value) ? value as ObjectValue : invalid() }
function string(value: unknown): string { return typeof value === 'string' ? value : invalid() }
function number(value: unknown): number { return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : invalid() }
function bool(value: unknown): boolean { return typeof value === 'boolean' ? value : invalid() }
function nullable<T>(value: unknown, parse: (v: unknown) => T): T | null { return value === null ? null : parse(value) }
function choice<T extends string>(value: unknown, values: readonly T[]): T { return values.includes(value as T) ? value as T : invalid() }
function array<T>(value: unknown, parse: (v: unknown) => T): T[] { return Array.isArray(value) ? value.map(parse) : invalid() }
function page<T>(value: unknown, parse: (v: unknown) => T): EnrichmentPage<T> {
  const v = object(value)
  return { items: array(v.items, parse), totalCount: number(v.totalCount), page: number(v.page), size: number(v.size) }
}
function round(value: unknown): EnrichmentRound {
  const v = object(value)
  return { number: number(v.number), state: choice(v.state, states), requestCount: number(v.requestCount), consumedFailures: number(v.consumedFailures), failureBudget: number(v.failureBudget), waitReason: nullable(v.waitReason, string), errorCode: nullable(v.errorCode, string), nextAttemptAtUnixMs: nullable(v.nextAttemptAtUnixMs, number), createdAtUnixMs: number(v.createdAtUnixMs), updatedAtUnixMs: number(v.updatedAtUnixMs) }
}
function task(value: unknown): EnrichmentTask {
  const v = object(value), c = object(v.cleanup)
  return { id: string(v.id), wordId: string(v.wordId), word: nullable(v.word, string), meaningId: nullable(v.meaningId, string), meaning: nullable(v.meaning, string), locale: choice(v.locale, ['en-GB', 'en-US']), step: choice(v.step, steps), applicable: bool(v.applicable), missing: bool(v.missing), inheritsDefault: bool(v.inheritsDefault), state: choice(v.state, states), waitReason: nullable(v.waitReason, string), current: round(v.current), cleanup: { waiting: number(c.waiting), running: number(c.running), observed: number(c.observed), attempts: number(c.attempts), nextAttemptAtUnixMs: nullable(c.nextAttemptAtUnixMs, number), errorCode: nullable(c.errorCode, string) } }
}
function detail(value: unknown): EnrichmentDetail {
  const v = object(value)
  return {
    task: task(v.task), rounds: page(v.rounds, round),
    calls: page(v.calls, value => { const c = object(value); return { id: string(c.id), round: number(c.round), service: string(c.service), result: string(c.result), errorCode: nullable(c.errorCode, string), startedAtUnixMs: number(c.startedAtUnixMs), finishedAtUnixMs: nullable(c.finishedAtUnixMs, number) } }),
    waits: page(v.waits, value => { const w = object(value); return { round: number(w.round), reason: string(w.reason), errorCode: nullable(w.errorCode, string), recordedAtUnixMs: number(w.recordedAtUnixMs), nextAttemptAtUnixMs: number(w.nextAttemptAtUnixMs) } }),
    cleanup: page(v.cleanup, value => { const c = object(value); return { epoch: number(c.epoch), state: string(c.state), attempts: number(c.attempts), nextAttemptAtUnixMs: number(c.nextAttemptAtUnixMs), errorCode: nullable(c.errorCode, string), createdAtUnixMs: number(c.createdAtUnixMs), updatedAtUnixMs: number(c.updatedAtUnixMs) } })
  }
}
const url = '/admin/enrichment'
const config = (signal: AbortSignal, lxIsCurrent: () => boolean) => ({ signal, lxIsCurrent })
export async function readEnrichmentTasks(query: EnrichmentQuery, currentPage: number, signal: AbortSignal, current: () => boolean) {
  return page(await api.get<unknown>(`${url}/tasks`, { ...config(signal, current), params: { ...query, page: currentPage, size: 25 } }), task)
}
export async function readEnrichmentSummary(query: EnrichmentQuery, signal: AbortSignal, current: () => boolean): Promise<EnrichmentSummary> {
  const v = object(await api.get<unknown>(`${url}/summary`, { ...config(signal, current), params: query }))
  return { applicableTargets: number(v.applicableTargets), completedTargets: number(v.completedTargets), missingTargets: number(v.missingTargets), unqueuedTargets: number(v.unqueuedTargets), persistedTasks: number(v.persistedTasks), targets: array(v.targets, value => { const t = object(value); return { state: choice(t.state, states), waitReason: nullable(t.waitReason, string), count: number(t.count) } }), services: array(v.services, value => { const s = object(value); return { service: choice(s.service, ['text', 'tts']), paused: bool(s.paused), blocked: bool(s.blocked), consecutiveFailures: number(s.consecutiveFailures), throttleSeconds: number(s.throttleSeconds), nextAttemptAtUnixMs: nullable(s.nextAttemptAtUnixMs, number) } }) }
}
export async function readEnrichmentDetail(id: string, historyPage: number, signal: AbortSignal, current: () => boolean) {
  return detail(await api.get<unknown>(`${url}/tasks/${encodeURIComponent(id)}`, { ...config(signal, current), params: { historyPage, historySize: 10 } }))
}
export interface RetryAction { id: string; expectedRound: number; retryKey: string }
export async function retryEnrichment(action: RetryAction, signal: AbortSignal, current: () => boolean) {
  const v = object(await api.post<unknown>(`${url}/tasks/${encodeURIComponent(action.id)}/retry`, { expectedRound: action.expectedRound, retryKey: action.retryKey }, config(signal, current)))
  return { targetId: string(v.targetId), round: number(v.round), replayed: bool(v.replayed) }
}
