import { resolveVocabularyHeader, VOCABULARY_COLUMNS } from './vocabularyHeader'
import { toPreviewRow } from './vocabularyRow'
import type { VocabularyPreviewRow } from './vocabularyRow'

/** How long the page waits for a workbook before it terminates the worker. */
const WORKBOOK_TIMEOUT_MS = 15_000

/** What the worker answers: the first sheet's cells as text, or why it could not read them. */
export type XlsxWorkerResponse =
  | { rows: string[][]; sheetName: string; sheetCount: number; error?: undefined }
  | { error: string }

export interface VocabularyWorkbook {
  rows: VocabularyPreviewRow[]
  /** A file-level error; when it is set there are no rows. */
  error?: string
  /** Set when the workbook has more than one sheet, saying which one was read. */
  notice?: string
}

/**
 * Reads the first sheet of an `.xlsx` file in a dedicated worker and turns it
 * into preview rows. Never rejects: every failure, including the timeout, is a
 * file-level error. `signal` terminates the worker early, for a file that has
 * been replaced or removed.
 */
export function readVocabularyWorkbook(buffer: ArrayBuffer, signal: AbortSignal): Promise<VocabularyWorkbook> {
  return new Promise((resolve) => {
    // Written out in full so that the bundler finds it and builds the worker,
    // and what it imports, as a chunk of its own.
    const worker = new Worker(new URL('./xlsxWorker.ts', import.meta.url), { type: 'module' })

    const finish = (result: VocabularyWorkbook) => {
      clearTimeout(timer)
      signal.removeEventListener('abort', abort)
      worker.terminate()
      resolve(result)
    }
    const abort = () => finish({ rows: [] })
    const timer = setTimeout(
      () => finish({ rows: [], error: 'Excel 解析超时，文件可能已损坏或过大' }),
      WORKBOOK_TIMEOUT_MS
    )
    signal.addEventListener('abort', abort)

    worker.onmessage = (event: MessageEvent<XlsxWorkerResponse>) => {
      const response = event.data
      finish(response.error === undefined
        ? parseVocabularySheet(response.rows, response.sheetName, response.sheetCount)
        : { rows: [], error: response.error })
    }
    worker.onerror = (event) => {
      finish({ rows: [], error: `无法读取 Excel 文件：${event.message || '解析程序加载失败'}` })
    }

    if (signal.aborted) {
      abort()
      return
    }
    worker.postMessage(buffer, [buffer])
  })
}

/**
 * Turns a sheet's cells, already converted to text, into preview rows by the
 * header rules CSV uses. A row's position is its sheet row number: blank rows
 * are skipped but counted.
 */
export function parseVocabularySheet(cells: string[][], sheetName: string, sheetCount: number): VocabularyWorkbook {
  const records = cells
    .map((fields, index) => ({ row: index + 1, fields }))
    .filter((record) => record.fields.some((field) => field.trim() !== ''))
  const [header, ...data] = records
  if (!header) {
    return { rows: [] }
  }

  // Cells beyond the header's last name meet a blank header: ignored when they
  // are blank throughout, a file-level error when any has a value.
  const width = records.reduce((widest, record) => Math.max(widest, record.fields.length), 0)
  const names = Array.from({ length: width }, (_, index) => header.fields[index] ?? '')
  const resolved = resolveVocabularyHeader(names, data.map((record) => record.fields))
  if (resolved.error !== undefined) {
    return { rows: [], error: resolved.error }
  }

  const rows = data.map((record) => {
    const columns: string[] = VOCABULARY_COLUMNS.map(() => '')
    resolved.mapping.forEach((canonical, index) => {
      if (canonical >= 0) {
        columns[canonical] = (record.fields[index] ?? '').trim()
      }
    })
    return toPreviewRow(record.row, columns)
  })

  return {
    rows,
    notice: sheetCount > 1 ? `工作簿共有 ${sheetCount} 个工作表，只读取第一个「${sheetName}」` : undefined
  }
}
