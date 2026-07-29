import axios from 'axios'

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status?: number
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

interface ErrorEnvelope {
  message?: unknown
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

    return new ApiError(message, error.response?.status)
  }

  return new ApiError('Network error')
}
