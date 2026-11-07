import api from './api'
import { ApiError } from './apiError'
import type { AudioFieldState, CredentialChange } from './audioStorageSettingsApi'

// Names and bounds mirror the administrator settings contract, not supplier defaults.
export const valueFields = [
  ['reviewAssistant.autoReviewEnabled', '启用 AI 自动审核投稿（默认关闭）', 'review', 'boolean'],
  ['reviewAssistant.endpoint', '审核服务地址', 'review', 'text'],
  ['reviewAssistant.model', '审核模型', 'review', 'text'],
  ['reviewAssistant.timeoutSeconds', '审核超时（秒）', 'review', 'integer', 1, 300],
  ['enrichment.text.endpoint', '补齐文本服务地址', 'text', 'text'],
  ['enrichment.text.model', '补齐文本模型', 'text', 'text'],
  ['enrichment.text.timeoutSeconds', '补齐文本超时（秒）', 'text', 'integer', 1, 300],
  ['enrichment.tts.provider', '语音提供方', 'tts', 'provider'],
  ['enrichment.tts.endpoint', '语音服务地址', 'tts', 'text'],
  ['enrichment.tts.model', '语音模型', 'tts', 'text'],
  ['enrichment.tts.timeoutSeconds', '语音超时（秒）', 'tts', 'integer', 1, 300],
  ['enrichment.tts.uk.voice', 'UK 音色', 'uk', 'text'],
  ['enrichment.tts.uk.style', 'UK 风格描述', 'uk', 'style'],
  ['enrichment.tts.uk.emotion', 'UK 情绪', 'uk', 'emotion'],
  ['enrichment.tts.uk.pitch', 'UK 音高', 'uk', 'integer', -12, 12],
  ['enrichment.tts.uk.volume', 'UK 音量', 'uk', 'decimal', 0.1, 10],
  ['enrichment.tts.uk.speed', 'UK 语速', 'uk', 'speed', 0.1, 4],
  ['enrichment.tts.us.voice', 'US 音色', 'us', 'text'],
  ['enrichment.tts.us.style', 'US 风格描述', 'us', 'style'],
  ['enrichment.tts.us.emotion', 'US 情绪', 'us', 'emotion'],
  ['enrichment.tts.us.pitch', 'US 音高', 'us', 'integer', -12, 12],
  ['enrichment.tts.us.volume', 'US 音量', 'us', 'decimal', 0.1, 10],
  ['enrichment.tts.us.speed', 'US 语速', 'us', 'speed', 0.1, 4],
  ['enrichment.textConcurrency', '文本请求并发上限', 'policy', 'integer', 1, 2],
  ['enrichment.concurrency', '任务执行与语音并发上限', 'policy', 'integer', 1, 8],
  ['enrichment.failureBudget', '每步骤失败额度（含首次）', 'policy', 'integer', 1, 20],
  ['enrichment.serviceFailureThreshold', '服务连续失败暂停阈值', 'policy', 'integer', 1, 20],
  ['enrichment.servicePauseSeconds', '服务暂停初始时间（秒）', 'policy', 'integer', 1, 600],
  ['enrichment.maxServicePauseSeconds', '服务暂停最长时间（秒）', 'policy', 'integer', 1, 3600],
  ['enrichment.throttleInitialSeconds', '限流初始退避（秒）', 'policy', 'integer', 1, 120],
  ['enrichment.throttleMaxSeconds', '限流最长退避（秒）', 'policy', 'integer', 1, 3600],
  ['enrichment.discoveryBatchSize', '每次发现上限', 'policy', 'integer', 1, 1000],
  ['enrichment.scanIntervalSeconds', '扫描间隔（秒）', 'policy', 'integer', 1, 3600]
] as const
export const secretFields = [
  ['reviewAssistant.apiKey', '审核 API Key', 'review'],
  ['enrichment.text.apiKey', '补齐文本 API Key', 'text'],
  ['enrichment.tts.apiKey', '语音 API Key', 'tts']
] as const
export type ValueField = typeof valueFields[number][0]
export type SecretField = typeof secretFields[number][0]
export type SettingValue = string | number | boolean | null
export type SettingsDraft = Record<ValueField, SettingValue>
export interface EnrichmentSettings {
  revision: string
  configurationReady: boolean
  values: SettingsDraft
  hasApiKey: Record<SecretField, boolean>
  fields: Record<ValueField | SecretField, AudioFieldState>
}
export type EnrichmentChanges = Partial<Record<ValueField, SettingValue>> & Partial<Record<SecretField, CredentialChange>>

function get(value: unknown, name: string): unknown {
  for (const part of name.split('.')) {
    if (typeof value !== 'object' || value === null) return undefined
    value = (value as Record<string, unknown>)[part]
  }
  return value
}
function parse(value: unknown): EnrichmentSettings {
  const invalid = () => { throw new ApiError('无法读取有效的审核与补齐配置。') }
  const revision = get(value, 'revision'), configurationReady = get(value, 'configurationReady')
  if (typeof revision !== 'string' || !/^"[0-9a-f]{32}"$/.test(revision) || typeof configurationReady !== 'boolean') return invalid()
  const values = {} as SettingsDraft
  for (const field of valueFields) {
    const [name, , , kind] = field
    const raw = get(value, name)
    const item = name === 'enrichment.textConcurrency' && raw === undefined ? 1 : raw
    const valid = kind === 'boolean' ? typeof item === 'boolean'
      : kind === 'integer' || kind === 'speed' || kind === 'decimal' ? kind === 'speed' && item === null
        || typeof item === 'number' && Number.isFinite(item) && item >= field[4]! && item <= field[5]! && (kind !== 'integer' || Number.isInteger(item))
        : typeof item === 'string' && item.length <= (kind === 'style' ? 512 : 2048) && (kind !== 'emotion' || ['', 'happy', 'sad', 'angry', 'fearful', 'disgusted', 'surprised', 'calm'].includes(item)) && (kind !== 'provider' || ['MiniMaxNative', 'OpenAICompatible'].includes(item))
    if (!valid) return invalid()
    values[name] = item as SettingValue
  }
  const hasApiKey = {} as Record<SecretField, boolean>
  for (const [name] of secretFields) {
    const presence = get(value, name.replace(/apiKey$/, 'hasApiKey'))
    if (typeof presence !== 'boolean') return invalid()
    hasApiKey[name] = presence
  }
  const fields = {} as EnrichmentSettings['fields']
  const rawFields = get(value, 'fields') as Record<string, AudioFieldState> | undefined
  for (const [name] of [...valueFields, ...secretFields]) {
    const field = rawFields?.[name] ?? (name === 'enrichment.textConcurrency' && get(value, name) === undefined
      ? { source: 'default' as const, editable: false, reason: '当前服务器尚不支持文本并发设置。' } : undefined)
    if (!field || !['default', 'file', 'environment', 'commandLine'].includes(field.source)
      || typeof field.editable !== 'boolean' || (field.reason !== null && typeof field.reason !== 'string')) return invalid()
    fields[name] = { source: field.source, editable: field.editable, reason: field.reason }
  }
  // Copy only the safe response contract; unexpected apiKey/private properties are discarded.
  return { revision, configurationReady, values, hasApiKey, fields }
}
const url = '/admin/enrichment/settings'
export async function readEnrichmentSettings(signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await api.get<unknown>(url, { signal, lxIsCurrent: isCurrent }))
}
export function enrichmentChangesBody(changes: EnrichmentChanges) {
  const body: Record<string, unknown> = {}
  for (const [name, value] of Object.entries(changes)) {
    const path = name.split('.'), leaf = path.pop()!
    let parent = body
    for (const part of path) {
      parent[part] ??= {}
      parent = parent[part] as Record<string, unknown>
    }
    parent[leaf] = value
  }
  return body
}
export async function saveEnrichmentSettings(revision: string, changes: EnrichmentChanges, signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await api.put<unknown>(url, enrichmentChangesBody(changes), { headers: { 'If-Match': revision }, signal, lxIsCurrent: isCurrent }))
}
