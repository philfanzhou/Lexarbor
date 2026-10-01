import {
  isAuthenticated,
  currentUser,
  logout,
  restoreSession
} from '@/services/authState'
import type { LogoutOutcome } from '@/services/authState'
import { readLogoutUrl } from '@/services/authApi'
import { ApiError, getApiError, getProblemDetails } from '@/services/apiError'
import type { ApiEntryError, ApiProblemDetails } from '@/services/apiError'

void isAuthenticated.value
void currentUser.value
const logoutResult: Promise<LogoutOutcome> = logout()
const restoreResult: Promise<void> = restoreSession()
const error: ApiError = getApiError(new Error('failure'))
void restoreResult
void error.status
const entryErrors: ApiEntryError[] | undefined = error.errors
const firstEntryIndex: number | undefined = entryErrors?.[0]?.index
void firstEntryIndex

// Failure bodies come in two shapes side by side: the endpoint-explicit
// envelope, and ServiceMantle Problem Details from the exception pipeline.
const problem: ApiProblemDetails | undefined = error.problem
void problem?.title
void problem?.errorCode
void problem?.correlationId
const parsedProblem: ApiProblemDetails | undefined = getProblemDetails({
  type: 'urn:servicemantle:error:vocabulary.conflict',
  title: 'The request conflicts with existing data.',
  status: 409,
  correlationId: 'synthetic-correlation-id',
  errorCode: 'vocabulary.conflict'
})
void parsedProblem?.title
const notAProblem: ApiProblemDetails | undefined = getProblemDetails({ success: false, message: 'envelope' })
void notAProblem

// The logout outcome carries only the optional verified provider URI; the
// local revocation is unconditional and has no representation here.
declare const outcome: LogoutOutcome
const logoutUrl: string | undefined = outcome.logoutUrl
void logoutUrl
void logoutResult

const parsedLogoutUrl: string | undefined = readLogoutUrl({ logoutUrl: 'https://idp.example/oauth2/logout' })
void parsedLogoutUrl
