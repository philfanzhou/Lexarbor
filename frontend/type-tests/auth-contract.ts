import { isAuthenticated, currentUser, login, logout, restoreSession } from '@/services/authState'
import { ApiError, getApiError } from '@/services/apiError'

void isAuthenticated.value
void currentUser.value
const loginResult: Promise<void> = login('admin', 'secret')
const logoutResult: Promise<void> = logout()
const restoreResult: Promise<void> = restoreSession()
const error: ApiError = getApiError(new Error('failure'))
void loginResult
void logoutResult
void restoreResult
void error.status
