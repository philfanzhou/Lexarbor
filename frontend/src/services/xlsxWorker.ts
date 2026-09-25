// Runs in its own module worker, so that neither this code nor its
// dependencies are in the page's first bundle, and a workbook that is slow to
// read never blocks the page (ADR-006). The page starts one worker per file
// and terminates it on a result, an error, or its timeout.
import { unzipSync } from 'fflate'
import readXlsxFile from 'read-excel-file/web-worker'
import type { XlsxWorkerResponse } from './vocabularyXlsx'

/** The most a workbook may declare its entries to take once unzipped. */
const MAX_UNZIPPED_BYTES = 32 * 1_048_576

const scope = self as unknown as {
  onmessage: ((event: MessageEvent<ArrayBuffer>) => void) | null
  postMessage(message: XlsxWorkerResponse): void
}

scope.onmessage = (event) => {
  readWorkbook(event.data).then(
    (response) => scope.postMessage(response),
    (error: unknown) => scope.postMessage({ error: `无法读取 Excel 文件：${reason(error)}` })
  )
}

async function readWorkbook(buffer: ArrayBuffer): Promise<XlsxWorkerResponse> {
  const refusal = checkEntrySizes(new Uint8Array(buffer))
  if (refusal !== undefined) {
    return { error: `无法读取 Excel 文件：${refusal}` }
  }

  const sheets = await readXlsxFile(buffer)
  const [first] = sheets
  if (!first) {
    return { error: '无法读取 Excel 文件：工作簿中没有工作表' }
  }

  return {
    rows: first.data.map((row) => row.map(cellText)),
    sheetName: first.sheet,
    sheetCount: sheets.length
  }
}

/**
 * Checks the sizes the zip's central directory declares, before anything is
 * unzipped: the filter sees every entry and keeps none, so this step reads the
 * directory only. The unzipper sizes its output by the declared size and cuts
 * off anything beyond it, so the declared total bounds the memory reading can
 * take; an entry whose compressed data is larger than deflate could make of its
 * declared size has a forged size, and is refused before it can cost the time
 * of inflating it.
 */
function checkEntrySizes(bytes: Uint8Array): string | undefined {
  let total = 0
  let forged: string | undefined
  try {
    unzipSync(bytes, {
      filter(file) {
        total += file.originalSize
        if (file.size > file.originalSize + file.originalSize / 1000 + 1024) {
          forged ??= file.name
        }
        return false
      }
    })
  } catch {
    return '不是有效的 .xlsx 文件（无法解析 zip 结构）'
  }

  if (forged !== undefined) {
    return `压缩包中「${forged}」登记的大小与内容不符，文件可能已损坏`
  }
  if (total > MAX_UNZIPPED_BYTES) {
    return '解压后的内容超过 32 MiB，请拆分后分批导入'
  }
  return undefined
}

/**
 * The text a cell imports as (ADR-006): what Excel shows for text, numbers
 * without their number format, booleans, and dates; blank for an empty cell, a
 * merged cell other than the top-left one, and an error value.
 */
function cellText(value: unknown): string {
  if (value === null || value === undefined) {
    return ''
  }
  if (typeof value === 'boolean') {
    return value ? 'TRUE' : 'FALSE'
  }
  if (value instanceof Date) {
    const date = value.toISOString().slice(0, 10)
    const time = value.toISOString().slice(11, 19)
    return time === '00:00:00' ? date : `${date}T${time}`
  }
  return String(value)
}

function reason(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
