# Local web application security assessment

> Historical pre-remediation baseline. The findings below describe the original tested behavior, not the current working tree. Code-level remediation and isolated regression checks have since been completed; see [Security hardening and deployment runbook](security-hardening.md) for validation results, required database-key migration, and remaining deployment/browser checks. The working database has not been migrated by the assistant.

## Scope and outcome

- Assessed: 2026-09-28 (local machine date), working tree on branch `Development`, including existing uncommitted changes.
- Target: `http://localhost:5000`, Development environment and URL from `Properties/launchSettings.json`.
- Method: live HTTP requests plus targeted source review; 33 recorded check groups, not a full penetration test.
- Isolation: separate Release publish in `.security-assessment/app`; a newly created `.security-assessment/config.db`. No existing database was copied or opened by the assessment instance. Synthetic accounts/settings only; no mail sends, Microsoft sign-ins, external-target probes, certificate changes, or load testing.
- Validation: solution build and isolated Release publish succeeded. SDK reported 10.0.401; installed ASP.NET Core 9 runtime reported 9.0.20; project targets net9.0.
- Application source was not modified. Credential values and cookie contents are deliberately omitted.

**Outcome: critical vulnerabilities confirmed. Do not expose this application to untrusted users until the authentication and credential-disclosure issues are remediated.** These results describe the tested instance, not proof of compromise or a production deployment certification.

## Findings

### SEC-01 — Critical: anonymous disclosure of account passwords

**Confirmed live.** An unauthenticated `GET /Login/Index` returned HTTP 200 and contained both the seeded username and its plaintext password. After creating a synthetic account, a fresh anonymous request also contained that account's randomly generated password. This is not limited to bootstrap credentials.

Sources:
- `EInvoiceAPIAdmin/Controllers/LoginController.cs:14-17`: `[AllowAnonymous]` on the account list.
- `EInvoiceAPIAdmin/Views/Login/Index.cshtml:31-32,55-59`: renders usernames and passwords in table cells and form values.
- `EInvoiceAPIAdmin/Controllers/HomeController.cs:33-34`: compares plaintext passwords.
- `EInvoiceAPIAdmin/Controllers/LoginController.cs:47-51`: authenticated export serializes complete login objects, including plaintext passwords; verified with synthetic data.

Impact: an unauthenticated visitor can retrieve working credentials and access protected administration functions. Password reuse could expand the impact beyond this app.

Remediation: require authorization for account administration; remove password values from all responses/exports; replace plaintext storage/comparison with a supported salted password hasher or ASP.NET Core Identity. Require credential resets and invalidate existing sessions. If previously reachable by untrusted users, treat stored passwords as potentially exposed and review access logs.

Retest: anonymous account-list requests must challenge/deny; passwords must never appear in HTML, JSON exports, or logs; only password hashes should be stored.

### SEC-02 — Critical: hard-coded fallback authentication bypasses the account database

**Confirmed live.** Posting the fallback credential pair found in source to `/Home/Login` returned HTTP 302 and an authentication cookie. The resulting session successfully accessed `/Settings/Export` (HTTP 200). Credential values are not reproduced here.

Source: `EInvoiceAPIAdmin/Controllers/HomeController.cs:36-40` creates an authenticated `admin` identity when the database lookup fails and the fixed pair matches. The seeded database password differs from this fallback, so the successful request exercised the fallback branch.

Impact: changing or deleting database users does not remove this alternative login path. Anyone who learns the embedded pair can authenticate.

Remediation: remove the fallback completely. Replace fixed initial credentials (`Data/AppDbContext.cs:127-130`) with a controlled one-time bootstrap process and force initial password setup. Invalidate sessions and assess exposure of credentials present in source/history.

Retest: the old fallback must fail regardless of database content; only valid active database accounts may authenticate.

### SEC-03 — High: missing server-side CSRF enforcement on account/settings mutations

**Confirmed at HTTP/server level.** Authenticated form POSTs to `/Login/Create` and `/Settings/Create`, without antiforgery tokens and with a foreign `Origin` header, returned HTTP 302 and persisted synthetic records. The account export independently confirmed creation. Tokenless delete requests also succeeded.

Sources:
- `EInvoiceAPIAdmin/Controllers/LoginController.cs:20-44,54-67`.
- `EInvoiceAPIAdmin/Controllers/SettingsController.cs:19-43,53-66`.
- `EInvoiceAPIAdmin/Controllers/HomeController.cs:29-31`: login POST also lacks validation.
- `EInvoiceAPIAdmin/Program.cs:12-14`: no global MVC antiforgery filter.
- The same missing-attribute pattern appears in `CompEnvController.cs`; those mutations were not exercised.

Important limitation: the HTTP client supplied cookies explicitly through its cookie container. It does not enforce browser SameSite rules. `SameSite=Lax` mitigates many cross-site POST scenarios; a complete browser CSRF exploit was not demonstrated. The missing validation is confirmed, not universal exploitability across browsers/origins. Login CSRF and same-site cross-origin cases still require consideration.

Remediation: apply global `AutoValidateAntiforgeryToken` for MVC unsafe methods, include tokens in forms/AJAX requests, and review every mutation endpoint. Do not rely solely on Origin/SameSite. Change logout to a token-protected POST; `GET /Home/Logout` was confirmed to end the session, and the login GET also invokes sign-out in source.

Retest: missing/invalid-token mutations return 400 and leave data unchanged; valid-token workflows continue to work.

### SEC-04 — High: deleted accounts retain authenticated access

**Confirmed live.** A synthetic user logged in and accessed `/Settings/Export` (200). A separate authenticated session deleted that user. Account export confirmed its removal, but the deleted user's existing cookie still accessed `/Settings/Export` (200).

Sources:
- `EInvoiceAPIAdmin/Controllers/HomeController.cs:44-57`: name-only claims and persistent cookie.
- `EInvoiceAPIAdmin/Controllers/LoginController.cs:40-44`: deletion has no revocation mechanism.
- `EInvoiceAPIAdmin/Program.cs:53-58`: no principal/security-stamp validation.

Impact: deleting an account does not promptly revoke its sessions. The separate 30-minute ASP.NET session idle timeout does not define authentication-cookie validity.

Remediation: include an immutable account ID and security version/stamp, validate account existence/status on cookie validation, and invalidate sessions on deletion/password changes. Set explicit authentication lifetime policies.

Retest: the previously issued cookie must be rejected after account deletion or security-version change.

### SEC-05 — High in exposed HTTP deployments: unencrypted transport and non-Secure authentication cookie

**Confirmed on the launch-profile instance.** Login and protected content were available over HTTP without HTTPS redirection. The authentication cookie was persistent, `HttpOnly`, `SameSite=Lax`, and lacked `Secure`.

Sources: `EInvoiceAPIAdmin/Properties/launchSettings.json:8-10`; `EInvoiceAPIAdmin/Program.cs:53-58,113-121`.

Impact: if deployed similarly beyond localhost, network observers could capture credentials or session cookies. This test does not establish the configuration of an external TLS-terminating proxy.

Remediation: require HTTPS at the deployment boundary, configure trusted forwarded headers when appropriate, use secure-only authentication cookies, and enable HSTS for production HTTPS responses. Preserve local development usability through an HTTPS development profile.

Retest: HTTP redirects to HTTPS; authentication cookies always have Secure; production HTTPS responses have the intended HSTS policy.

### SEC-06 — High, source-confirmed: fixed database encryption password

**Source-confirmed; not a live database-extraction test.** The database encryption password is embedded in `EInvoiceAPIAdmin/Program.cs:18` and reused for restore in `Controllers/MaintenanceController.cs:95`. Values are deliberately not reproduced.

Impact: possession of both a database/backup and application source/binaries undermines the confidentiality provided by that fixed encryption key. Static-file probes did not expose the database, so this is not evidence of remote database download.

Remediation: use deployment-managed secrets and restricted filesystem/backup permissions; plan a tested re-encryption migration and key rotation rather than merely changing the connection string and making existing data unreadable. Hash account passwords independently of database encryption.

### SEC-07 — Medium: missing defensive response headers

**Confirmed live.** `/Home/Index` lacked CSP, X-Frame-Options, X-Content-Type-Options, and Referrer-Policy. The anonymous account page also lacked frame protection and CSP. Responses disclosed `Server: Kestrel` (informational).

Impact: missing frame restrictions permit clickjacking conditions; missing CSP and other headers remove defense in depth. No browser-based clickjacking exploit was executed. CSP absence alone is not proof of XSS.

Remediation: introduce an application-compatible CSP, including `frame-ancestors`; apply `nosniff`, a suitable referrer policy, and frame restrictions where needed. Test existing inline scripts/styles before enforcing CSP.

### SEC-08 — Medium, limited evidence: no application login throttling observed

Eight sequential invalid logins each returned HTTP 200 with no 429 response. Source inspection found no rate-limiter/lockout configuration in `Program.cs` or `HomeController.cs`.

Limitations: eight attempts do not establish behavior at all thresholds, and an external gateway may impose its own controls. No brute-force campaign was performed.

Remediation: introduce bounded account/IP-aware throttling, monitoring, and suitable lockout/recovery policies without enabling trivial denial of service against known usernames.

## Controls that passed the selected checks

- Anonymous access to 12 selected protected pages/exports redirected to `/Home/Login` (302).
- Anonymous `/Login/Create` redirected to login instead of creating an account.
- Authenticated tokenless POSTs to `/AlertRules/BatchEdit`, `/SmtpProfiles/Delete`, and `/Maintenance/Backup` returned 400.
- Requests for `/config.db`, `/appsettings.json`, `/.git/config`, `/Program.cs`, and two parent-path variants returned 404. HTTP-client normalization means these are limited path probes, not comprehensive traversal testing.
- A synthetic script marker persisted in a setting was HTML-encoded in `/Settings/Index`; the raw script was absent. This checks that rendering path only, not all XSS sinks or browser execution.
- Authentication cookie included HttpOnly and SameSite=Lax.
- Anonymous account-page response used `Cache-Control: no-store, no-cache`; this does not compensate for exposing passwords in the response.
- Logout cleared the active client session as expected, although GET is the wrong method for that mutation.

## Priority order

1. Restrict access immediately if the app is publicly or broadly internally reachable.
2. Remove anonymous credential disclosure and fallback authentication; reset potentially exposed passwords and invalidate sessions.
3. Implement hashed passwords and secure bootstrap; rotate the embedded database key using a tested migration.
4. Add consistent antiforgery validation and account/session revocation.
5. Require HTTPS/secure cookies; add response headers and login throttling.
6. Repeat live checks and add automated regression coverage for each fixed finding.

No patches were applied because the selected scope was live testing, not automatic remediation.

## Evidence, cleanup, and limitations

Redacted evidence: `docs/security-assessment-results.json`. Each group records status codes and boolean observations, not credentials, cookie values, full response bodies, or existing application records.

Synthetic account and setting records were deleted during testing. The isolated server was stopped, port 5000 was verified to have no remaining listener, and the temporary publish/database/probe directory was removed. Only the report and redacted evidence were retained; pre-existing workspace changes were preserved. No production/debug process needs restarting for these documentation-only changes.

Not covered: a full dependency/CVE audit, production TLS/proxy configuration, browser-driven CSRF/clickjacking verification, comprehensive XSS/injection/fuzzing, Graph consent/token flows against Microsoft, SMTP delivery, database restore/import resilience, certificate administration, or an exhaustive authorization review. All authenticated accounts appear to share administrative access in the inspected code; role requirements were not inferred as a separate vulnerability without a defined access model.
