import {
  authMethod,
  isAuthenticated,
  currentUser,
  ensureAuthMethodLoaded,
  isHostedAuthMethod,
  loadAuthMethod,
  login,
  logout,
  restoreSession
} from '@/services/authState'
import type { LogoutOutcome } from '@/services/authState'
import type { AuthMethod } from '@/services/authApi'
import { readAuthMethod, readLogoutUrl } from '@/services/authApi'
import { ApiError, getApiError } from '@/services/apiError'
import type { ApiEntryError } from '@/services/apiError'

void isAuthenticated.value
void currentUser.value
const loginResult: Promise<void> = login('admin', 'secret')
const logoutResult: Promise<LogoutOutcome> = logout()
const restoreResult: Promise<void> = restoreSession()
const error: ApiError = getApiError(new Error('failure'))
void loginResult
void restoreResult
void error.status
const entryErrors: ApiEntryError[] | undefined = error.errors
const firstEntryIndex: number | undefined = entryErrors?.[0]?.index
void firstEntryIndex

// The logout outcome carries only the optional verified provider URI; the
// local revocation is unconditional and has no representation here.
declare const outcome: LogoutOutcome
const logoutUrl: string | undefined = outcome.logoutUrl
void logoutUrl
void logoutResult

// The login mode is deployment state: unknown until one answer is accepted,
// and only the two contract values are ever cached.
const cachedMethod: AuthMethod | null = authMethod.value
void cachedMethod
const loadedMethod: Promise<AuthMethod> = loadAuthMethod()
void loadedMethod
const hostedCheck: Promise<boolean> = isHostedAuthMethod()
void hostedCheck
ensureAuthMethodLoaded()
const parsedMethod: AuthMethod = readAuthMethod({ method: 'hosted' })
void parsedMethod
const parsedLogoutUrl: string | undefined = readLogoutUrl({ logoutUrl: 'https://idp.example/oauth2/logout' })
void parsedLogoutUrl
