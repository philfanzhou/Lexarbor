# Lexarbor administration frontend specification

## Stack and boundaries

- Vue 3.5, TypeScript, Vue Router, Element Plus, Axios, Vite.
- No additional state management or authentication dependency.
- The frontend is hosted by the Vocabulary backend and is same-origin with the administration API in production.
- The frontend calls relative `/admin/*` paths only and does not know the Identity address.
- The frontend never stores or reads an access token, refresh token, AppSecret, default administrator account, or password.

## Routes

| Path | Access | Page |
|------|----------|------|
| `/login` | Anonymous | Identity administrator username and password login |
| `/forbidden` | Anonymous | Non-administrator notice |
| `/books` | Administrator | Vocabulary book management |
| `/import` | Administrator | Word import |

The application uses hash history. On first entry to a protected page it calls `GET /admin/auth/session` to restore the cookie session; an unauthenticated response redirects to `/login` and a 403 redirects to `/forbidden`. While unauthenticated, neither the administration navigation nor any actionable page is rendered.

## Authentication API

| Method and path | Request and response |
|------------|-----------|
| `POST /admin/auth/login` | `{ username, password }`; on success returns non-sensitive session information only |
| `GET /admin/auth/session` | Returns the current administrator session |
| `POST /admin/auth/logout` | Deletes the server-side cookie; the frontend always clears its local state |

Axios uses `withCredentials=true`. Administration write requests send:

```text
X-Requested-With: XMLHttpRequest
```

No Authorization token, localStorage token, or sessionStorage token may be added.

## State and errors

Authentication state lives in a small in-project TypeScript module or composable that maintains:

```text
isAuthenticated
currentUser
login(username, password)
restoreSession()
logout()
clearSession()
```

A shared `ApiError` carries the public message and an optional HTTP status:

| Status | Frontend behaviour |
|------|----------|
| 400 | Show the parameter or form error |
| 401 | Clear the session and redirect to the login page |
| 403 | Redirect to the forbidden page |
| 404 | Show that the resource does not exist |
| 409 | Show the data conflict; for a book deletion, suggest disabling it instead |
| 422 | Show that the business precondition is not met |
| 500/502/503 | Show the generic service error |

Components use `catch (error: unknown)` with the shared conversion function, never `any`.

## Existing pages

- Book management keeps search, paging, create, edit, status toggle, and delete.
- The administration list contains both enabled and disabled books.
- Deleting a book that still has meanings answers 409, which prompts the administrator to disable it.
- Word import keeps the book, word, British phonetic, American phonetic, part of speech, definition, and example sentence fields.
- Both existing features must remain usable after a successful login.

## Build

```bash
npm ci
npm run test:types
npm run build
```

Vite writes to the frontend's own `dist/`, and the root Dockerfile copies that directory into the .NET Host's `wwwroot` publish content during the image build.
