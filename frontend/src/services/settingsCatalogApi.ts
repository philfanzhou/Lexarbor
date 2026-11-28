import api from './api'
import { ApiError } from './apiError'
import { isRevision, putWithRevision } from './settingsRevision'

/**
 * The complete configuration surface behind `/#/settings`: one entry for every key Lexarbor
 * reads. A live key is published by its own module as soon as a save commits; a restart-only
 * key is persisted for the next start and the running process keeps its startup value; a
 * read-only key belongs to the deployment.
 */
export type SettingValue = string | number | boolean | null | string[] | number[]
type ValueKind = 'text' | 'url' | 'boolean' | 'triState' | 'integer' | 'number' | 'secret' | 'list' | 'integerList' | 'select'
export type SettingApply = 'live' | 'restart' | 'readOnly'
type SettingSource = 'default' | 'file' | 'environment' | 'commandLine'
export type LiveModule = 'rateLimits' | 'audioStorage' | 'enrichment'

export interface SettingField {
  key: string
  group: string
  section: string
  /** The server's English name; the page shows its own Chinese label and falls back to this. */
  name: string
  description: string | null
  kind: ValueKind
  apply: SettingApply
  editable: boolean
  source: SettingSource
  /** Why a read-only or overridden key cannot be changed here; null while it can. */
  reason: string | null
  /** The value in effect now. Never sent for a secret. */
  value: SettingValue
  /** The configured text, when it could not be read as the field's own type. */
  raw: string | null
  /** For a secret: whether one is set. */
  hasValue: boolean | null
  /** For a restart-only secret: whether the next start finds one. Never its value. */
  savedHasValue: boolean | null
  /** For a restart-only key: the value the next start will read. */
  savedValue: SettingValue
  defaultValue: SettingValue
  pendingRestart: boolean
  minimum: number | null
  maximum: number | null
  /** Whether a number may be empty, which leaves the key to its default. */
  nullable: boolean
  options: string[] | null
  /** The module that publishes a live key, and the key's name in that module's request body. */
  module: LiveModule | null
  wire: string | null
  /**
   * For a model connection: the key the persisted file still holds it under, when an upgrade
   * left it in the section an earlier release used; null whenever the file uses the shown key.
   */
  location: string | null
}
interface SettingGroup {
  id: string
  area: string
  name: string
  description: string | null
}
interface SettingSection {
  group: string
  id: string
  name: string
}
export interface SettingFact {
  id: string
  name: string
  value: string | null
  detail: string | null
}
export interface SettingsCatalog {
  revision: string
  storageAvailable: boolean
  fileExists: boolean
  externallyModified: boolean
  fields: SettingField[]
  groups: SettingGroup[]
  sections: SettingSection[]
  facts: SettingFact[]
  /** Leaf paths in the persisted file that the catalog does not describe; names only. */
  unknownFileKeys: string[]
}

/** One change of a restart-only key. `null` removes the key, so the next start reads the default. */
export type StartupChange = SettingValue | CredentialChange
export type CredentialChange = { action: 'keep' | 'clear' } | { action: 'replace'; value: string }

const kinds: ValueKind[] = ['text', 'url', 'boolean', 'triState', 'integer', 'number', 'secret', 'list', 'integerList', 'select']
const applies: SettingApply[] = ['live', 'restart', 'readOnly']
const sources: SettingSource[] = ['default', 'file', 'environment', 'commandLine']
const modules: LiveModule[] = ['rateLimits', 'audioStorage', 'enrichment']

function invalid(): never {
  throw new ApiError('无法读取有效的配置目录。')
}
function object(value: unknown): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return invalid()
  return value as Record<string, unknown>
}
function text(value: unknown): string
function text(value: unknown, nullable: true): string | null
function text(value: unknown, nullable = false): string | null {
  if (value === null && nullable) return null
  if (typeof value !== 'string') return invalid()
  return value
}
function optionalText(value: unknown): string | null {
  return value === null ? null : text(value)
}
function value(value: unknown): SettingValue {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return value
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (Array.isArray(value)) {
    if (value.every(item => typeof item === 'string')) return value as string[]
    if (value.every(item => typeof item === 'number' && Number.isFinite(item))) return value as number[]
  }
  return invalid()
}
function list(value: unknown): string[] | null {
  return value === null ? null : Array.isArray(value) && value.every(item => typeof item === 'string') ? value : invalid()
}
function bound(value: unknown): number | null {
  if (value === null) return null
  if (typeof value !== 'number' || !Number.isFinite(value)) return invalid()
  return value
}
function member<T>(candidates: readonly T[], item: unknown): T {
  if (typeof item !== 'string' || !candidates.includes(item as T)) return invalid()
  return item as T
}

function field(input: Record<string, unknown>): SettingField {
  const hasValue = input.hasValue
  const savedHasValue = input.savedHasValue
  if (typeof input.editable !== 'boolean' || typeof input.pendingRestart !== 'boolean'
    || (hasValue !== null && typeof hasValue !== 'boolean')
    || (savedHasValue !== null && savedHasValue !== undefined && typeof savedHasValue !== 'boolean')) return invalid()
  return {
    key: text(input.key), group: text(input.group), section: text(input.section), name: text(input.name),
    description: optionalText(input.description), kind: member(kinds, input.kind), apply: member(applies, input.apply),
    editable: input.editable, source: member(sources, input.source), reason: optionalText(input.reason),
    value: value(input.value), raw: optionalText(input.raw), hasValue,
    savedHasValue: savedHasValue === undefined ? null : savedHasValue,
    savedValue: value(input.savedValue), defaultValue: value(input.defaultValue),
    pendingRestart: input.pendingRestart, minimum: bound(input.minimum), maximum: bound(input.maximum), nullable: input.nullable === true,
    options: list(input.options), module: input.module === null ? null : member(modules, input.module), wire: optionalText(input.wire),
    location: optionalText(input.location)
  }
}

function parse(input: unknown): SettingsCatalog {
  const catalog = object(input)
  const { fields, groups, sections, facts, unknownFileKeys } = catalog
  if (!isRevision(catalog.revision)
    || typeof catalog.storageAvailable !== 'boolean' || typeof catalog.fileExists !== 'boolean'
    || typeof catalog.externallyModified !== 'boolean' || !Array.isArray(fields) || !Array.isArray(groups)
    || !Array.isArray(sections) || !Array.isArray(facts) || !Array.isArray(unknownFileKeys)) {
    return invalid()
  }
  return {
    revision: catalog.revision,
    storageAvailable: catalog.storageAvailable,
    fileExists: catalog.fileExists,
    externallyModified: catalog.externallyModified,
    fields: fields.map((item: unknown) => field(object(item))),
    groups: groups.map((item: unknown) => {
      const group = object(item)
      return { id: text(group.id), area: text(group.area), name: text(group.name), description: optionalText(group.description) }
    }),
    sections: sections.map((item: unknown) => {
      const section = object(item)
      return { group: text(section.group), id: text(section.id), name: text(section.name) }
    }),
    facts: facts.map((item: unknown) => {
      const fact = object(item)
      return { id: text(fact.id), name: text(fact.name), value: optionalText(fact.value), detail: optionalText(fact.detail) }
    }),
    unknownFileKeys: unknownFileKeys.map((item: unknown) => text(item))
  }
}

const url = '/admin/system/settings/catalog'
const startup = '/admin/system/settings/startup'

export async function readSettingsCatalog(signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await api.get<unknown>(url, { signal, lxIsCurrent: isCurrent }))
}

/** Persists restart-only keys for the next start and answers the catalog that follows the commit. */
export async function saveStartupSettings(revision: string, changes: Record<string, StartupChange>, signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await putWithRevision(startup, revision, changes, signal, isCurrent))
}
