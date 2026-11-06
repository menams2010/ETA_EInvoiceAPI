# ETA_EInvoiceAPI merge plan

Goal: one solution where **EInvoiceAPI** (signing and submission API) uses the **EInvoiceAPIAdmin** configuration database (`config.db`, LiteDB), with the Admin app (`Development` branch) as the owner of that database's schema. **IncomeTaxWebAPI** joins the solution unchanged.

## Phase 1: Repository merge (done)

- [x] Import with full history under `src/<Name>/`: IncomeTaxWebAPI (`master`), EInvoiceAPI (`master`), EInvoiceAPIAdmin (`Development`)
- [x] Root `ETA_EInvoiceAPI.sln` containing all three apps plus the Admin regression-test projects (in a `tests` solution folder)
- [x] Single root `.gitignore` and `.gitattributes` (all copies were identical)

## Phase 2: EInvoiceAPI uses the Admin config database (done)

### Starting point

| | EInvoiceAPI (`master`) | EInvoiceAPIAdmin (`Development`) |
|---|---|---|
| Collections | `Settings`, `Companies`, `CompEnvs`, `EInvoiceEnvs`, `Logins` | Same names (migrates old `tb_*` names into them) |
| Expiry field | `ClientSecretExpirationDate` (`DateOnly`, which LiteDB can't store) | `ClientSecretExpiryDate` (`DateTime`) |
| Password | Hard-coded in source | `Database:Password` from secure config, at least 32 characters |
| Path | `<api bin>/config.db` | `Database:Path`, else `<admin bin>/../config.db` |
| Connection | Shared | **Direct (exclusive)**, held open for the app's lifetime |
| Schema/seeding | Seeded settings, environments and a plain-text login | Owns schema, seeding, migrations and hashed accounts |
| LiteDB | 5.0.21 | 5.0.17 |

### Changes made

1. **EInvoiceAPI `TokenManager`:**
   - Reads `Database:Path` and `Database:Password`, the same keys as Admin. Startup fails with a clear message if the password is missing or shorter than 32 characters.
   - Opens `config.db` in **shared** mode, and no longer creates the file. A missing file is an error that points to `Database:Path`.
   - **No more schema creation or seeding.** In particular, it no longer inserts the hard-coded plain-text login. Admin owns the schema.
   - Reads the expiry from `ClientSecretExpiryDate` and still returns it as the `DateOnly` column `ClientSecretExpirationDate`, so `TokenHelper`'s "client secret expired" check now actually works with dates entered in Admin.
   - `SaveToken` updates only `str_token` and `time_get` on the stored document, so fields Admin adds to `CompEnv` are never wiped.
   - Public methods and `DataTable` shapes are unchanged, so `Invoice.cs`, `TokenHelper` and `CertificateCache` are untouched.
2. **EInvoiceAPI `Startup`:** calls `TokenManager.Configure(Configuration)`.
3. **EInvoiceAPIAdmin `DatabaseSecurity.Open()`:** the live connection is now **shared** instead of exclusive, so the API can use the file while Admin runs. Offline operations (backup copy, key rotation, legacy migration) still use exclusive access.
4. **LiteDB 5.0.17 in both apps.** Both apps must run the same engine version on the shared file. 5.0.21 breaks Admin's key rotation (`Rebuild` fails with "Invalid password"; caught by `Security.Regression`), so the API moved down rather than Admin up.
5. **Shared mode across IIS app pools** ([`src/Shared/LiteDbSharedMutex.cs`](../src/Shared/LiteDbSharedMutex.cs), linked into all three apps). LiteDB coordinates shared access with a named mutex, `Global\<sha1(path)>.Mutex`. On .NET (Core) it creates that mutex with default security, so only the app pool that opened `config.db` first could use it; the others failed with "Access to the path 'Global\…Mutex' is denied". Each app now creates the mutex itself before opening the file, with an ACL every app pool can open. This was found by the installer's end-to-end test on IIS.

### Verification

- `dotnet build` succeeds for EInvoiceAPI, EInvoiceAPIAdmin and all three test projects.
- Admin regression suites pass with the changes: `DatabaseMigration.Regression`, `Security.Regression`, `GraphMail.Regression`.
- **Two-process smoke test:** a process standing in for Admin uses the real `DatabaseSecurity`, `AppDbContext` and account bootstrap, holds the file open and writes continuously. At the same time a second process runs EInvoiceAPI's real `TokenManager`. 13 of 13 checks pass:
  - Company, environment and expiry data entered in Admin is read by the API.
  - Tokens and `CertCacheLastRefresh` written by the API are visible to Admin.
  - Fields only Admin knows about survive the API's `SaveToken`.
- Not verified: IncomeTaxWebAPI (it needs Windows/MSBuild), and the HTTP endpoints of either app against a real ETA environment or a USB signing token.

### Deployment notes

- Set **the same** `Database__Path` (absolute) and `Database__Password` for both app pools or services, using secure configuration and not `appsettings.json`.
- Existing production `config.db` files must first go through Admin's offline migration or key-rotation runbook (`src/EInvoiceAPIAdmin/docs/security-hardening.md`).
- **Existing databases are likely to have a conflict.** The old API seeded `Settings`, `EInvoiceEnvs` and `Logins`, while the old Admin wrote `tb_Settings`, `tb_eInvoiceEnv` and `tb_Login`. Admin's migration refuses to start when both an old and a current collection hold data. Rehearse on a copy and reconcile those collections before switching over.
- Update the Admin runbook's line "The new connection is exclusive" to say the connection is now shared with EInvoiceAPI.

## Phase 3: One .NET version for everything (done)

All projects target **net10.0** (LTS until November 2028). It's set once in `Directory.Build.props`, and `global.json` pins the .NET 10 SDK. .NET 7 is out of support, and .NET 9 support ends on 10 November 2026.

- **EInvoiceAPI** (net7.0 → net10.0):
  - Framework packages moved to 10.0.14.
  - Swashbuckle 6.1.1 → 10.3.1. 6.1.1 had a known vulnerability (GHSA-qrmm-w75w-3wpx); the code change was one `using`.
  - Removed `System.Runtime` and `System.Security.Principal.Windows`, which are built in now.
  - The IIS-only `IISServerOptions` setting now applies only on Windows.
- **EInvoiceAPIAdmin and its tests** (net9.0 → net10.0): `Microsoft.Data.Sqlite` → 10.0.14. No code changes.
- **IncomeTaxWebAPI** (.NET Framework 4.5.2, ASP.NET Web API 2 → ASP.NET Core on net10.0):
  - `Global.asax`, `WebApiConfig`, OWIN `Startup` and `Web.config` were replaced by `Program.cs`. `ApiKeyHandler` became `ApiKeyMiddleware`, with the same key and the same 401 body.
  - Controllers keep their routes and logic. Only the Web API 2 types changed: `ApiController` → `ControllerBase`, `HttpResponseMessage` → `IActionResult`.
  - An empty body still binds to `null`. A `null` response (SAP didn't answer 200) is now an explicit 500, which is what Web API 2 produced.
  - A missing `Accept-Language` header no longer throws on `ping` (the value was never used).
  - Packages: `System.Data.SQLite` → `System.Data.SQLite.Core` 1.0.119. RestSharp stays on 106.15.0, because 107+ changed the API these controllers use.
  - Removed unused packages: EntityFramework, the SQLite EF6/LINQ/SEE extensions, Eagle/Harpy, Owin and Web API.
- **Verified on .NET 10:**
  - `dotnet build ETA_EInvoiceAPI.sln` succeeds, and all package vulnerability warnings are gone.
  - The Admin regression suites pass (3/3), and the two-process shared-database smoke test passes (13/13).
  - EInvoiceAPI starts, rejects a short password, and serves Swagger with 10 paths.
  - IncomeTaxWebAPI was run and exercised with HTTP requests. API-key 401s, `ping` on all three controllers, a 404 for unknown routes, notifications saved to SQLite, and the 500 when SAP is unreachable all matched the old behaviour.
- **Not verified:** running under IIS on Windows, calls to the real SAP servers, and the USB signing token.

## Phase 4: Follow-ups (not started)

- [x] Removed Slack logging from EInvoiceAPI (`SlackLogger` and its settings). The webhook URL was also scrubbed from git history; revoke it in Slack.
- [ ] Align `Pkcs11Interop` (5.1.2 in the API vs 5.3.0 in Admin).
- [ ] Optionally extract the shared collection names and models into a small library both apps reference.
- [ ] Remove the stray root-level `Program.cs` files under `src/EInvoiceAPI` (empty) and `src/EInvoiceAPIAdmin` (old copy). Neither is compiled.
- [ ] CI on `windows-latest` to build the solution and run the Admin regression suites.
- [x] IncomeTaxWebAPI: the SAP URLs, Basic-auth credentials and timeout moved to Admin > SAP Endpoints, stored in `config.db`. The old credentials are still in git history, so change the SAP service user's password.
- [ ] IncomeTaxWebAPI: move the remaining hard-coded ETA API key into configuration.
- [ ] IncomeTaxWebAPI: move RestSharp to 112.x, the same as EInvoiceAPI. This needs small code changes.
- [ ] Optionally fix the existing `SYSLIB0014` and `SYSLIB0057` obsolete-API warnings in EInvoiceAPI (`ServicePointManager`, `X509Certificate2` constructor).
