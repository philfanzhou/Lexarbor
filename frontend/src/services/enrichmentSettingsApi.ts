import api from './api'
import { ApiError } from './apiError'
import type { AudioFieldState, CredentialChange } from './audioStorageSettingsApi'

// Names and bounds mirror the administrator settings contract, not supplier defaults.
export const valueFields = [
  ['reviewAssistant.endpoint', '审核服务地址', 'review', 'text'],
  ['reviewAssistant.model', '审核模型', 'review', 'text'],
  ['reviewAssistant.timeoutSeconds', '审核超时（秒）', 'review', 'integer', 1, 300],
  ['enrichment.fieldsEnabled', '启用默认字段补齐', 'text', 'boolean'],
  ['enrichment.audioEnabled', '启用音频生成', 'text', 'boolean'],
  ['enrichment.text.endpoint', '补齐文本服务地址', 'text', 'text'],
  ['enrichment.text.model', '补齐文本模型', 'text', 'text'],
  ['enrichment.text.timeoutSeconds', '补齐文本超时（秒）', 'text', 'integer', 1, 300],
  ['enrichment.tts.provider', '语音提供方', 'tts', 'provider'],
  ['enrichment.tts.endpoint', '语音服务地址', 'tts', 'text'],
  ['enrichment.tts.model', '语音模型', 'tts', 'text'],
  ['enrichment.tts.timeoutSeconds', '语音超时（秒）', 'tts', 'integer', 1, 300],
  ['enrichment.tts.uk.voice', 'UK 音色', 'uk', 'text'],
  ['enrichment.tts.uk.speed', 'UK 语速', 'uk', 'speed', 0.1, 4],
  ['enrichment.tts.us.voice', 'US 音色', 'us', 'text'],
  ['enrichment.tts.us.speed', 'US 语速', 'us', 'speed', 0.1, 4],
  ['enrichment.concurrency', '并发上限', 'policy', 'integer', 1, 8],
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
    const [name, , , kind] = field, item = get(value, name)
    const valid = kind === 'boolean' ? typeof item === 'boolean'
      : kind === 'integer' || kind === 'speed' ? kind === 'speed' && item === null
        || typeof item === 'number' && Number.isFinite(item) && item >= field[4]! && item <= field[5]! && (kind === 'speed' || Number.isInteger(item))
        : typeof item === 'string' && item.length <= 2048 && (kind !== 'provider' || ['MiniMaxNative', 'OpenAICompatible'].includes(item))
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
    const field = rawFields?.[name]
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
export async function saveEnrichmentSettings(revision: string, changes: EnrichmentChanges, signal: AbortSignal, isCurrent: () => boolean) {
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
  return parse(await api.put<unknown>(url, body, { headers: { 'If-Match': revision }, signal, lxIsCurrent: isCurrent }))
}
