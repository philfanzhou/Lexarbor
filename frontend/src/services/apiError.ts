import axios from 'axios'

/** One invalid entry of a batch request, by its zero-based position in the request. */
export interface ApiEntryError {
  index: number
  message: string
}

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status?: number,
    public readonly errors?: ApiEntryError[]
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

interface ErrorEnvelope {
  message?: unknown
  errors?: unknown
}

/**
 * The per-entry errors of a failure envelope, or undefined unless every item
 * has the documented shape. Only the batch import route sends `errors`; a
 * partially readable list would let a page point at the wrong rows, so it is
 * treated as absent rather than trimmed to the readable items.
 */
export function getEntryErrors(value: unknown): ApiEntryError[] | undefined {
  if (!Array.isArray(value)) {
    return undefined
  }

  const errors: ApiEntryError[] = []
  for (const item of value as unknown[]) {
    if (typeof item !== 'object' || item === null) {
      return undefined
    }

    const { index, message } = item as { index?: unknown; message?: unknown }
    if (!Number.isInteger(index) || typeof message !== 'string') {
      return undefined
    }

    errors.push({ index: index as number, message })
  }

  return errors
}

export function getApiError(error: unknown): ApiError {
  if (error instanceof ApiError) {
    return error
  }

  if (axios.isAxiosError<ErrorEnvelope>(error)) {
    const responseMessage = error.response?.data?.message
    const message = typeof responseMessage === 'string' && responseMessage.trim()
      ? responseMessage
      : error.response
        ? 'Request failed'
        : 'Network error'

    return new ApiError(message, error.response?.status, getEntryErrors(error.response?.data?.errors))
  }

  return new ApiError('Network error')
}
