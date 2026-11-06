# Security hardening and deployment runbook

## Status

The eight findings from [the original assessment](security-assessment.md) have code-level remediations. Solution builds, offline security/mail/alert regressions, and isolated HTTPS checks passed during remediation. This is not a certification of a production deployment.

**Deployment requires operator action.** Building or publishing does not migrate a deployment database. Before starting against legacy data, complete the offline migration or backup/key-rotation procedure below. Missing database secrets deliberately stop startup; there is no embedded fallback key or default login.

Existing users keep their passwords through automatic hashing on startup. This preserves access, but does not make previously disclosed passwords safe: arrange password changes for any potentially exposed accounts. Existing authentication cookies are no longer accepted.

## Required configuration

Use a deployment-managed secret source or process/service environment variables. Do not commit real values to appsettings, launch settings, scripts, project files, or source control. Environment variables are accessible to administrators and the hosting identity; protect the service configuration accordingly. Do not put secret values in command-line arguments or shell history.

| Configuration key | Environment variable | Purpose |
| --- | --- | --- |
| `Database:Path` | `Database__Path` | Absolute path of the existing or new database. Explicitly set this outside the web/content/publish directories. Its parent directory must exist. |
| `Database:Password` | `Database__Password` | New/current database key; at least 32 characters. Generate a cryptographically random deployment-specific secret, for example 32 random bytes encoded as Base64. Length validation does not ensure randomness. |
| `Database:PreviousPassword` | `Database__PreviousPassword` | Previous key, supplied only for offline rotation. Obtain it securely from the existing deployment. |
| `Database:RotateKey` | `Database__RotateKey` | Set to `true` only for the offline rotation run, then remove it. The process rotates the key and exits without serving requests. |
| `Bootstrap:Username` | `Bootstrap__Username` | First administrator username for an empty account database; maximum 100 characters. |
| `Bootstrap:Password` | `Bootstrap__Password` | First administrator password, 12–256 characters. Remove bootstrap settings after first successful initialization. |
| `HttpsPort` | `HttpsPort` | HTTPS redirect destination port. Default 443; development launch profile uses 5001. This does not create an HTTPS listener. |
| `AllowedHosts` | `AllowedHosts` | Configure the actual deployment hostnames; the repository default is `*`. |
| `ASPNETCORE_ENVIRONMENT` | `ASPNETCORE_ENVIRONMENT` | Use `Production` for deployment, not the development launch profile. |

The old `ConnectionStrings:DefaultConnection` setting is not used by the database service. If `Database:Path` is omitted, the compatibility default is `../config.db` relative to the executable directory. Publishing to a different directory can therefore point to a different database. Never rely on this default for production migration.

`Bootstrap` settings are needed only if no accounts exist after legacy migration. They are not a password override or recovery backdoor for an existing account. All authenticated users continue to be administrators; no new role hierarchy was introduced.

## SQLite or first-version LiteDB: separate-database migration

Use offline migration rather than Maintenance restore to upgrade legacy schemas. The former live SQLite importer is disabled: it could partially overwrite a running database. Maintenance restore remains a limited current-schema operation, not a full-file restore or legacy migration.

### Supported sources and validation

- SQLite is identified by its file header, not its extension. The supported application tables are `tb_Company`, `tb_CompEnv`, `tb_eInvoiceEnv`, `tb_Settings`, and `tb_Login`, with corresponding singular/current names also recognized. Unknown SQLite tables fail rather than being silently discarded. Encrypted SQLite/SQLCipher is not supported; obtain a separately decrypted copy using the original system first.
- LiteDB sources may be plaintext or encrypted with the original key. `tb_*` and first-version singular collections are mapped to current names. Other LiteDB collections, raw additional fields, and custom indexes are retained. Conflicting populated aliases/current collections fail; there is no automatic merge or ID reassignment.
- IDs, company/environment relationships, settings (including `DateTimeFormat`), `time_get`, tokens, and user-entered `CompEnv.ClientSecretExpiryDate` are preserved. Absent optional expiration dates remain unset. Invalid conversions, duplicate usernames, missing account credentials, orphan relationships, and unsupported schemas block publication.
- A recognized, nonempty account collection is required. Bootstrap credentials are not used to replace legacy accounts. Legacy passwords become hashes and are checked against the original password before publication; existing hashes are preserved.
- SQLite snapshots include committed WAL data through SQLite's backup API. LiteDB sources must be stopped and checkpointed; a remaining `*-log.db` journal causes a refusal. Do not delete a journal to bypass this check: recover/checkpoint with the original application first.

### Offline procedure and switch

1. Stop all old and new application instances. Preserve an independent backup of the original database, journals, encryption key, and Data Protection key ring. Record the current database path/key for rollback.
2. Create a restricted destination directory outside source, web, and publish directories. Allow space for both a full source snapshot and the new database. Snapshots inherit the directory's permissions; SQLite/plaintext sources and legacy password fields remain sensitive.
3. Set the following through deployment-managed configuration or Development user secrets. Do not put secret values in arguments, shell history, source control, or launch settings:

| Configuration key | Environment variable | Value |
| --- | --- | --- |
| `Database:Migration:Enabled` | `Database__Migration__Enabled` | `true` for the one-shot migration run |
| `Database:Migration:SourcePath` | `Database__Migration__SourcePath` | Absolute existing legacy database path |
| `Database:Migration:SourcePassword` | `Database__Migration__SourcePassword` | Original LiteDB key; omit for plaintext LiteDB/SQLite |
| `Database:Path` | `Database__Path` | Absolute **new, nonexistent** destination; never the source or current database |
| `Database:Password` | `Database__Password` | New random destination key, at least 32 characters |

4. Leave `Database:RotateKey` disabled. Run `dotnet EInvoiceAPIAdmin.dll` from the published application directory. Migration runs before the web server and background services are started, then exits. A failure has a nonzero exit code. Do not use an automatically restarting service for this operation.
5. Require the successful validation report. Before publishing the destination, migration reopens it with the new key and checks preserved record IDs/fields/counts, login passwords, custom LiteDB indexes, current model conversion, and references. Source snapshots and failed staged files are retained in a unique `.migration-*` directory beside the destination for investigation. No existing destination is overwritten, including on a repeated run.
6. Graph registration/token payloads are preserved but not decrypted or authenticated by this migration. Preserve the matching Data Protection key ring and application identity (`InvoiceWebApp`); loss of keys requires re-entry/reconnection. Validate mail configuration before enabling sending.
7. Remove all `Database:Migration:*` settings. Keep the successfully validated `Database:Path` and its `Database:Password`; restart the application to switch to it. The migration does not rewrite configuration files or user secrets. Use legacy login credentials and verify application behavior before reopening access.
8. If validation fails, do not remove the migration flag and start against the requested destination: normal startup can create missing databases. Restore the previous path/key configuration, or correct the source on a separate copy and retry. If rollback is needed after switching, stop the app and restore the original path/key with its compatible application version. Never downgrade the migrated database in place.

Run isolated coverage with `dotnet run --project tests/DatabaseMigration.Regression`. These tests create disposable SQLite/LiteDB fixtures and do not access configured application databases or external mail services.

## Existing database: safe rollout and key rotation

1. Restrict access and schedule downtime. Stop Visual Studio debugging, the IIS application pool/site or Kestrel service, and every other process using the database. The new connection is exclusive: do not run multiple workers/web-garden instances against the same file. Disable overlapping recycling or otherwise ensure the old worker exits before the replacement opens it.
2. Record the actual database path, deployment identity, existing key, and Data Protection key-ring location. Copy the original database and any associated journal/log files while all processes are stopped. Preserve a separate, access-restricted recovery copy outside the publish directory. Back up the Data Protection keys as well; the database key alone cannot decrypt Graph credentials/tokens.
3. Rehearse on a secured copy first. Configure `Database__Path` to that copy, `Database__Password` to a new random key, `Database__PreviousPassword` to the previous key, and `Database__RotateKey=true`. Obtain the previous embedded key from the old deployment through an approved secret-handling process; this runbook intentionally does not reproduce it.
4. From the new published application directory, run `dotnet EInvoiceAPIAdmin.dll` with those environment settings. Do not start through an automatically restarting service during rotation. Verify the process reports successful rotation and exits successfully.
5. Rotation checkpoints the original, makes a staging copy named `<database>.rotation`, rebuilds that copy with the new key, opens it with the new key and checks collection counts, then atomically replaces the original while retaining `<database>.pre-security-backup` under the previous key. It refuses to overwrite existing rotation/recovery artifacts. A failure before replacement leaves the original available; do not delete artifacts blindly after a failure.
6. After a successful rehearsal, repeat the controlled offline procedure on the intended deployment database. Keep the independent recovery backup until application-level verification completes. Key rotation does not prove every business record is semantically valid.
7. Remove `Database__PreviousPassword` and `Database__RotateKey`. Retain the new `Database__Password` and explicit `Database__Path` in the hosting service's secure configuration. Make sure the service identity can read/write the DB directory and has adequate free space for a rebuild.
8. Start the new version over HTTPS. Startup hashes legacy passwords and removes the plaintext fields from current and recognized legacy account collections. Existing usernames/passwords should work; old cookies should not. Do not restart the old executable against the migrated data: the old version does not understand the new credential representation.
9. Verify login, account management, companies/environments, alerts, mail profiles, and a backup/restore rehearsal. Only then reopen access. Retain the old key solely as required to recover old backups, under restricted access, rather than in the running app configuration.

Password migration removes plaintext from live documents; it is not a forensic secure erase of previously allocated database pages, storage snapshots, exports, logs, or backups. Treat old files and recovery copies as sensitive. Apply your retention/destruction policy after successful validation. If rollback is necessary, stop the app and restore the matching pre-migration data, key, and application version in an isolated environment; do not re-expose the vulnerable old application.

## New installation

Create an access-restricted database directory and configure its explicit path and random key. Supply a unique bootstrap administrator username and strong password through secure configuration, then start the app on HTTPS. After confirming the first account works, remove bootstrap settings from the environment/service configuration and restart. There is no automatically seeded fixed credential and `IsInitial` does not force a password-change screen.

## Direct IIS and Kestrel HTTPS

- **IIS:** install/configure the supported ASP.NET Core Hosting Bundle, set an HTTPS site binding with a valid server certificate, configure the application pool identity and database permissions, and confirm requests reach ASP.NET Core as HTTPS. Use the normal IIS hosting integration; do not trust arbitrary forwarded headers from clients. Test any HTTP binding redirects correctly and has the intended public hostname.
- **Direct Kestrel:** configure the actual HTTPS endpoint and server certificate through Kestrel configuration or the hosting service. `HttpsPort` only controls redirects. Keep certificate private keys/passwords out of the repository and restrict access to the hosting identity.
- Set the production host allowlist and network/firewall restrictions. Use a dedicated origin for this administrator app, not an origin shared with untrusted applications.
- Authentication, antiforgery, and session cookies require HTTPS. The authentication cookie is HttpOnly, SameSite=Lax, nonpersistent, and expires after 30 minutes without sliding renewal. Account deletion, password changes, username changes, and account restores revoke the affected sessions through account IDs/security stamps.
- HSTS is enabled outside Development, with a 180-day lifetime. ASP.NET Core excludes localhost/loopback by default, so localhost response checks do not prove public-host HSTS behavior. Verify it on the real HTTPS hostname. Do not deploy a development certificate.
- For local development, use the `https` launch profile: HTTPS on 5001 and HTTP redirect on 5000. Trust the developer certificate through the normal local development process if desired (`dotnet dev-certs https --trust`). Test-only certificate pinning does not establish browser/OS trust.

## Data Protection and Microsoft Graph

Preserve the existing Data Protection key ring and hosting identity when moving deployments. The application retains the `InvoiceWebApp` application name. Protect the key ring at rest and grant access only to the service identity. Verify its persistence across restarts under IIS/Kestrel; default behavior depends on the hosting environment. Losing the key ring can require re-entering Graph app registrations and reconnecting mailboxes.

Update each Microsoft app registration's allowed redirect URI to the deployed HTTPS origin followed by `/signin-microsoft-mail` (local development now uses port 5001). See [Microsoft Graph mail setup](microsoft-graph-mail.md). No live Microsoft sign-in or real mail delivery was performed during remediation.

## Account imports, exports, and backups

- Account pages and account JSON exports omit plaintext passwords, hashes, and security stamps. Exports are metadata, not credential backups.
- Account JSON imports require a unique `u_name` and a `Password` of 12–256 characters. Legacy `u_pass` input is accepted under the same length rules. Supplied IDs/hashes/stamps are not accepted as account credentials. Import size is limited to 1 MB. Store/transport import files securely and dispose of them afterward.
- Blank password edits retain the current hash. New passwords renew the security stamp. The last remaining administrator cannot be deleted through the account endpoint.
- Encrypted database downloads are logical backups of collections/documents and non-primary indexes using the current database key. Keep that key separately from the backup. The backup was tested for data and unique-index preservation, not for every possible LiteDB pragma/collation or huge database size.
- Maintenance restore accepts the current key by default or an explicitly supplied older backup password. Restored account credentials are normalized and get fresh security stamps within the restore transaction. SQLite login imports hash passwords before writing them.
- Maintenance's “all tables” restore covers its listed business/account/mail tables, not every database collection. Graph registration/token collections and audit/send logs are not restored by that UI. For full disaster recovery, stop the application and restore the complete encrypted DB and matching Data Protection key ring, then verify account sessions/Graph connections before reopening access.

## Validation record and remaining checks

Final workspace cleanup removed the isolated databases, logs, and assessment script; no localhost test server remains. One temporary binary, `.security-validation/app/LiteDB.dll`, was locked by the diagnostic PowerShell session during cleanup. Close that terminal session, then delete the remaining `.security-validation` directory before committing. It contains no test database or credentials. The working application database was not migrated or modified by these tests.

Completed during remediation, against disposable data:

- Solution build and Release publish.
- `dotnet run --project tests/Security.Regression/Security.Regression.csproj -c Release`: legacy password preservation/hashing, safe exports, password updates, stale-stamp rejection, restore stamp renewal, key rotation with recoverable old-key backup, logical backup data/index preservation.
- `dotnet run --project tests/GraphMail.Regression/GraphMail.Regression.csproj -c Release`: SMTP/Graph, manual alert tests, and batch-rule behavior.
- Live HTTPS harness: anonymous account denial, secure HttpOnly cookies, missing-token rejection, CSP nonce/header matching, confirmation markup, selected pages returning successfully, account create/delete with valid tokens, deleted-cookie rejection, login throttling, HTTP redirects, and a settings-only encrypted backup/restore round trip.

To rerun live tests, start a separately published instance with a newly created isolated database and random bootstrap credentials. Set `SECURITY_TEST_USERNAME` and `SECURITY_TEST_PASSWORD` for the harness only. Run `dotnet run --project tests/Security.Regression/Security.Regression.csproj -c Release -- live https://localhost:5001 http://localhost:5000`. The optional HTTP argument verifies redirection. Never target a real-data instance: the harness creates/deletes an account and restores Settings from its test backup. Use a fresh instance or wait for the one-minute limiter window between runs.

If the localhost development certificate is untrusted, the harness can accept one explicitly provided `SECURITY_TEST_CERTIFICATE_THUMBPRINT`; it still requires loopback, a matching hostname, and certificate validity. Do not use blanket certificate-validation bypasses. Clear test environment variables and delete test databases after stopping the instance.

Remaining operational verification:

- Run a browser walkthrough of TinyMCE editing, HTML preview, confirmation dialogs, certificate/token selection, and Graph connect/reconnect. HTTP rendering tests are not browser JavaScript/CSP integration tests.
- CSP uses per-request nonces, denies inline event attributes, framing, and objects; it still trusts jsDelivr and permits inline CSS for existing UI compatibility. CDN/editor dependency and supply-chain review was not included. HTML previews intentionally block scripts/forms and remote resources.
- Login throttling is five requests per remote IP per minute, in-process, including successful and invalid/tokenless attempts. It resets on process restart, can affect administrators behind a shared IP, and is not distributed protection or account-based lockout. Review ingress controls and monitoring for Internet exposure.
- Verify production TLS, HSTS, host filtering, filesystem ACLs, hosting identity/key-ring persistence, certificate renewal, and backup recovery. No production IIS/browser walkthrough, comprehensive dependency/CVE audit, load test, or exhaustive penetration test was performed.
