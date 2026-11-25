import { computed, onBeforeUnmount, reactive, ref, shallowRef, watch } from 'vue'
import { getApiError } from './apiError'
import { sessionGeneration } from './authState'
import { audioTextFields, credentialFields, saveAudioStorage, type AudioStorageChanges } from './audioStorageSettingsApi'
import { saveEnrichmentSettings, type EnrichmentChanges } from './enrichmentSettingsApi'
import { readSettingsCatalog, saveStartupSettings, type LiveModule, type SettingField, type SettingValue, type SettingsCatalog, type StartupChange } from './settingsCatalogApi'
import { fieldLabel } from './settingsLabels'
import { saveRateLimits, type RateLimitChanges } from './systemSettingsApi'

const catalog = shallowRef<SettingsCatalog | null>(null)
/** The last catalog a settings page read. The navigation reads its pending counts from it. */
export const settingsCatalog = computed(() => catalog.value)

export interface SecretDraft {
  action: 'keep' | 'replace' | 'clear'
  value: string
}
export type NoticeTone = 'info' | 'success' | 'warning' | 'error'

type Module = LiveModule | 'startup'
interface Plan {
  module: Module
  keys: string[]
  send: (revision: string, signal: AbortSignal) => Promise<string>
}

const same = (left: SettingValue, right: SettingValue) => left === right
  || (Array.isArray(left) && Array.isArray(right) && left.length === right.length && left.every((item, index) => item === right[index]))
const copy = (value: SettingValue): SettingValue => Array.isArray(value) ? [...value] as SettingValue : value
// A restart-only key is edited as the value the next start will read. A list of numbers is
// typed as text, so its draft holds every entry as text, whatever the server sent.
const base = (field: SettingField): SettingValue => {
  const value = field.apply === 'restart' ? field.savedValue : field.value
  return field.kind === 'integerList' && Array.isArray(value) ? (value as unknown[]).map(String) as SettingValue : value
}

const commonMessages: Record<number, string> = {
  412: '设置版本已变化，请重新读取后核对草稿再保存，不能覆盖其他页面或管理员的更新。',
  413: '配置请求超过服务器大小限制，请缩短输入。',
  428: '缺少设置版本，请重新读取后保存。',
  503: '配置文件不可写或保存服务不可用，原配置保留。请检查文件权限后重试。'
}
// Fixed local notices: a server error body is never shown beside a credential form.
const moduleMessages: Record<Module, Record<number, string>> = {
  rateLimits: { 400: '配置或版本格式无效，请检查次数和窗口的取值范围。', 409: '开关被部署配置覆盖，请重新读取。' },
  audioStorage: {
    400: '配置无效，请检查启用时的完整字段和密钥。',
    409: '配置被部署覆盖，或已有音频、正在运行的操作阻止修改存储目的地（需要单独迁移）。'
  },
  enrichment: { 400: '配置或版本格式无效，请检查字段范围和服务地址。', 409: '字段被部署配置覆盖，请重新读取来源与只读原因。' },
  startup: { 400: '标出的配置项未通过检查，请修正后重新保存。', 409: '字段被部署配置覆盖，请重新读取。' }
}
const failureText = (module: Module, status: number) =>
  `保存失败（${status}）：${moduleMessages[module][status] ?? commonMessages[status] ?? '请求未被服务器接受，请重新读取并检查配置。'}`

/**
 * One settings page's session: the catalog it shows, the administrator's draft, and the save
 * that splits the draft between the module that publishes each live key and the file that
 * holds restart-only keys. Drafts and secret inputs live in memory only.
 *
 * A null `groupId` reads the catalog without a draft, for the pages that only browse it.
 */
export function useSettingsPage(groupId: string | null) {
  const status = ref<'loading' | 'ready' | 'unknown'>('loading')
  const busy = ref(false)
  const notice = ref('正在读取当前生效配置…')
  const tone = ref<NoticeTone>('info')
  const draft = reactive<Record<string, SettingValue>>({})
  const secrets = reactive<Record<string, SecretDraft>>({})
  // What the server refused, with the exact draft it refused: the notice stays only while
  // that draft is unchanged.
  const refused = reactive<Record<string, { value: string; message: string }>>({})
  const generation = sessionGeneration.value
  // A key whose module already committed in the current save; it is no longer a change.
  const sent = new Set<string>()
  let active = true
  let revision = ''
  let controller: AbortController | null = null
  const current = () => active && generation === sessionGeneration.value

  const fields = computed(() => (catalog.value?.fields ?? []).filter(field => field.group === groupId))
  const field = (key: string) => catalog.value?.fields.find(item => item.key === key)

  function differs(item: SettingField): boolean {
    const mine = draft[item.key]
    return mine !== undefined && !same(mine, base(item))
  }
  function secretChanges(item: SettingField): boolean {
    const secret = secrets[item.key]
    return !!secret && (secret.action === 'clear' || secret.value !== '')
  }

  // Counted against the last read whatever the page's state: a draft survives an unknown
  // state, and leaving with it still asks first.
  const changedFields = computed(() => {
    const value = catalog.value
    if (!value) return []
    return value.fields.filter(item => item.group === groupId && item.editable && !sent.has(item.key)
      && (item.kind === 'secret' ? secretChanges(item) : differs(item)))
  })
  const dirty = computed(() => changedFields.value.length > 0)
  const liveCount = computed(() => changedFields.value.filter(item => item.apply === 'live').length)
  const restartCount = computed(() => changedFields.value.length - liveCount.value)

  // The bounds the page can check itself, so an impossible value never leaves the browser,
  // and the server's own refusals for as long as their draft is unchanged.
  const invalid = computed(() => {
    const found: Record<string, string> = {}
    for (const [key, refusal] of Object.entries(refused)) {
      if (JSON.stringify(draft[key]) === refusal.value) found[key] = refusal.message
    }
    for (const item of changedFields.value) {
      const mine = draft[item.key]
      if (item.kind === 'integer' || item.kind === 'number') {
        if (mine === null) {
          if (!item.nullable) found[item.key] = '请输入数字。'
        } else if (typeof mine !== 'number' || !Number.isFinite(mine)) found[item.key] = '请输入数字。'
        else if (item.kind === 'integer' && !Number.isInteger(mine)) found[item.key] = '请输入整数。'
        else if ((item.minimum !== null && mine < item.minimum) || (item.maximum !== null && mine > item.maximum)) {
          found[item.key] = `取值范围为 ${item.minimum ?? '不限'} 到 ${item.maximum ?? '不限'}。`
        }
      } else if (item.kind === 'list' && Array.isArray(mine)) {
        if ((mine as unknown[]).some(entry => !String(entry).trim())) found[item.key] = '列表中有空项，请删除或填写。'
        else if (item.maximum !== null && mine.length > item.maximum) found[item.key] = `最多 ${item.maximum} 项。`
      } else if (item.kind === 'integerList' && Array.isArray(mine)) {
        // The entries are typed as text and sent as numbers; a list left empty removes the key.
        const entries = mine.map(String)
        if (entries.some(entry => !/^-?\d+$/.test(entry.trim()))) found[item.key] = '每项都要填整数。'
        else if (entries.some(entry => (item.minimum !== null && Number(entry) < item.minimum) || (item.maximum !== null && Number(entry) > item.maximum))) {
          found[item.key] = `每项的取值范围为 ${item.minimum ?? '不限'} 到 ${item.maximum ?? '不限'}。`
        }
      }
    }
    return found
  })
  const valid = computed(() => Object.keys(invalid.value).length === 0)

  function clearSecrets() {
    for (const item of fields.value) {
      if (item.kind === 'secret') secrets[item.key] = { action: 'keep', value: '' }
    }
  }

  // Whether the administrator moved a draft away from what the page showed before the read.
  // Compared with the previous answer, not the new one: a value changed elsewhere is the
  // server's to show unless the administrator has an edit of their own on top of it.
  function moved(now: SettingField, before: SettingField | undefined): boolean {
    if (!before || !now.editable) return false
    const mine = draft[now.key]
    return mine !== undefined && !same(mine, base(before))
  }

  // A draft the administrator has not moved follows the server; a moved one stays for review.
  function adopt(value: SettingsCatalog, resetDraft: boolean) {
    const previous = catalog.value
    sent.clear()
    for (const key of Object.keys(refused)) delete refused[key]
    for (const item of value.fields) {
      if (item.group !== groupId) continue
      if (item.kind === 'secret') {
        secrets[item.key] = { action: 'keep', value: '' }
      } else if (resetDraft || !previous || !moved(item, previous.fields.find(entry => entry.key === item.key))) {
        draft[item.key] = copy(base(item))
      }
    }
    catalog.value = value
    revision = value.revision
    status.value = 'ready'
  }

  function begin() {
    controller?.abort()
    controller = new AbortController()
    busy.value = true
    return controller.signal
  }

  async function load() {
    if (!current() || busy.value) return
    const signal = begin()
    clearSecrets()
    try {
      const value = await readSettingsCatalog(signal, current)
      if (!current()) return
      adopt(value, false)
      notice.value = '已读取当前生效配置。'
      tone.value = 'info'
    } catch {
      if (current()) {
        status.value = 'unknown'
        notice.value = '配置状态未知：读取失败，请重试。'
        tone.value = 'error'
      }
    } finally {
      if (current()) busy.value = false
    }
  }

  // Where each change goes. Live keys to the module that publishes them, in a fixed order;
  // restart-only keys to the file. Every request carries the revision the previous one answered.
  function plans(): Plan[] {
    const changed = changedFields.value
    const of = (module: LiveModule) => changed.filter(item => item.module === module)
    const keys = (items: SettingField[]) => items.map(item => item.key)
    const queue: Plan[] = []

    const limits = of('rateLimits')
    if (limits.length) {
      const body: RateLimitChanges = { adminLoginEnabled: false }
      for (const item of limits) body.adminLoginEnabled = draft[item.key] as boolean
      queue.push({ module: 'rateLimits', keys: keys(limits),
        send: async (ifMatch, signal) => (await saveRateLimits(ifMatch, body, signal, current)).revision })
    }

    // The audio module receives every editable storage field, so a first save without a
    // template keeps the retained credentials, and each credential states its own action.
    const storage = of('audioStorage')
    if (storage.length) {
      const byWire = (wire: string) => storage.find(item => item.wire === wire)
        ?? fields.value.find(item => item.module === 'audioStorage' && item.wire === wire)
      const body: AudioStorageChanges = {}
      const enabled = byWire('enabled')
      if (enabled?.editable && differs(enabled)) body.enabled = draft[enabled.key] as boolean
      for (const wire of audioTextFields) {
        const item = byWire(wire)
        if (item?.editable && differs(item)) body[wire] = String(draft[item.key])
      }
      for (const wire of credentialFields) {
        const item = byWire(wire)
        if (!item?.editable) continue
        const secret = secrets[item.key]
        body[wire] = secret.action === 'clear' ? { action: 'clear' }
          : secret.value !== '' ? { action: 'replace', value: secret.value } : { action: 'keep' }
      }
      queue.push({ module: 'audioStorage', keys: keys(storage),
        send: async (ifMatch, signal) => (await saveAudioStorage(ifMatch, body, signal, current)).revision })
    }

    const model = of('enrichment')
    if (model.length) {
      // The module's names are dotted paths; a key the server adds still travels under its own name.
      const body = {} as Record<string, unknown>
      for (const item of model) {
        const secret = secrets[item.key]
        body[item.wire!] = item.kind === 'secret'
          ? (secret.action === 'clear' ? { action: 'clear' } : { action: 'replace', value: secret.value })
          : draft[item.key]
      }
      queue.push({ module: 'enrichment', keys: keys(model),
        send: async (ifMatch, signal) => (await saveEnrichmentSettings(ifMatch, body as EnrichmentChanges, signal, current)).revision })
    }

    const startup = changed.filter(item => item.apply === 'restart')
    if (startup.length) {
      const body: Record<string, StartupChange> = {}
      for (const item of startup) {
        const secret = secrets[item.key]
        const value = draft[item.key]
        // A list of numbers is typed as text and sent as numbers; one with nothing left in it
        // removes the key, so the next start reads the default.
        body[item.key] = item.kind === 'secret'
          ? (secret.action === 'clear' ? { action: 'clear' } : { action: 'replace', value: secret.value })
          : item.kind === 'integerList' && Array.isArray(value)
            ? (value.length === 0 ? null : (value as string[]).map(Number)) : value
      }
      queue.push({ module: 'startup', keys: keys(startup),
        send: async (ifMatch, signal) => (await saveStartupSettings(ifMatch, body, signal, current)).revision })
    }
    return queue
  }

  async function reread(signal: AbortSignal): Promise<boolean> {
    try {
      const value = await readSettingsCatalog(signal, current)
      if (!current()) return false
      adopt(value, false)
      return true
    } catch {
      if (current()) status.value = 'unknown'
      return false
    }
  }

  async function save() {
    if (!current() || busy.value || status.value !== 'ready' || !dirty.value || !valid.value) return
    const queue = plans()
    if (!queue.length) return
    const live = liveCount.value
    const restart = restartCount.value
    const secretTouched = queue.some(plan => plan.keys.some(key => field(key)?.kind === 'secret'))
    const signal = begin()
    // A credential-bearing attempt is never kept as a retryable draft.
    clearSecrets()
    notice.value = '正在保存，等待服务器确认。'
    tone.value = 'info'
    let committed = 0
    let uncertain = false
    let failed: { module: Module; status: number; fields: readonly string[] | undefined } | null = null
    try {
      for (const plan of queue) {
        try {
          revision = await plan.send(revision, signal)
          if (!current()) return
          plan.keys.forEach(key => sent.add(key))
          committed += plan.keys.length
        } catch (error: unknown) {
          if (!current()) return
          const failure = getApiError(error)
          if (failure.status === undefined) {
            // No answer at all: the file may or may not be committed. Read back, never resend.
            uncertain = true
          } else {
            failed = { module: plan.module, status: failure.status, fields: failure.fields }
          }
          break
        }
      }
      if (uncertain) {
        status.value = 'unknown'
        notice.value = '保存结果未知，正在回读确认；不会自动重复提交。'
        if (await reread(signal) && current()) {
          notice.value = '保存响应未确认，已回读当前生效配置；请核对草稿，未生效的修改需要重新保存。'
          tone.value = 'warning'
        } else if (current()) {
          notice.value = '保存结果未知，回读失败；请重新读取当前状态。草稿已保留。'
          tone.value = 'error'
        }
      } else if (failed) {
        tone.value = 'error'
        notice.value = failureText(failed.module, failed.status)
        if (committed > 0) {
          const confirmed = await reread(signal)
          if (!current()) return
          notice.value = confirmed
            ? `前 ${committed} 项已保存并回读确认。${notice.value}`
            : `前 ${committed} 项已提交，但回读失败，当前状态未知。${notice.value}`
        }
        // After the read, which resets the page: what the server refused stays marked on its
        // own fields for as long as that draft is unchanged.
        const refusedNow = (failed.fields ?? []).filter(key => field(key))
        for (const key of refusedNow) {
          refused[key] = {
            value: JSON.stringify(draft[key]),
            message: failed.status === 409 ? '服务器报告此项被部署配置覆盖或暂时不能修改，请重新读取。' : '服务器未接受此值，请检查后重试。'
          }
        }
        if (refusedNow.length > 0) notice.value += ` 涉及：${refusedNow.map(key => fieldLabel(field(key)!)).join('、')}。`
        // The revision or the locks may have moved: reread explicitly, review, then save again.
        if ([409, 412, 428].includes(failed.status)) status.value = 'unknown'
      } else {
        notice.value = live && restart ? `已保存并回读确认：${live} 项立即生效，${restart} 项将在重启服务后生效。`
          : restart ? `已保存到配置文件，${restart} 项将在重启服务后生效。`
            : '已保存到配置文件并立即生效，已回读确认。'
        tone.value = 'success'
        if (!(await reread(signal)) && current()) {
          notice.value = '保存结果未知，回读失败；请重新读取当前状态。草稿已保留。'
          tone.value = 'error'
        }
      }
      if (secretTouched && current()) notice.value += ' 密钥输入已清空。'
    } finally {
      if (current()) busy.value = false
    }
  }

  function resetDraft() {
    if (!catalog.value || status.value !== 'ready' || busy.value) return
    adopt(catalog.value, true)
    notice.value = '已放弃未保存的修改。'
    tone.value = 'info'
  }

  function cancel() {
    active = false
    controller?.abort()
    sent.clear()
    for (const key of Object.keys(secrets)) delete secrets[key]
    for (const key of Object.keys(draft)) delete draft[key]
    for (const key of Object.keys(refused)) delete refused[key]
    status.value = 'loading'
  }
  function sessionEnded() {
    cancel()
    catalog.value = null
  }
  watch(sessionGeneration, sessionEnded, { flush: 'sync' })
  onBeforeUnmount(cancel)

  return {
    catalog, status, busy, notice, tone, draft, secrets, invalid, valid, fields, changedFields, dirty,
    liveCount, restartCount, field, load, save, resetDraft
  }
}

export type SettingsPage = ReturnType<typeof useSettingsPage>
