# config.db versions and migration

`config.db` holds the shared configuration of EInvoiceAPI, EInvoiceAPIAdmin and ETANotifications: companies, ETA environments and credentials, settings, Admin accounts, alerts, SAP endpoints and the notifications API key. Its format has changed several times. **Admin > Migrate Database** accepts a file of any of these versions and brings it up to the current one.

## Versions

| Version | Used | Written by | Format and password | Contents |
|---|---|---|---|---|
| V1 | 2025-06 to 2025-08 | EInvoiceAPI | SQLite, no password | Tables `tb_Login`, `tb_eInvoiceEnv`, `tb_CompEnv`, `tb_Company`, `tb_Settings` and view `V_Data`. Admin passwords in plain text. |
| V2 | from 2025-08 | EInvoiceAPI | LiteDB 5 (5.0.21), built-in password | Collections `Settings`, `Companies`, `CompEnvs`, `EInvoiceEnvs`, `Logins`. Client secret expiry stored as `ClientSecretExpirationDate` (a serialized `DateOnly`). Plain-text passwords. |
| V3 | from 2025-09 | EInvoiceAPIAdmin | LiteDB 5 (5.0.17), built-in password | Same data, collections named `tb_Company`, `tb_CompEnv`, `tb_eInvoiceEnv`, `tb_Login`, `tb_Settings`. |
| V4 | 2026-01-18 | EInvoiceAPIAdmin | LiteDB 5, built-in password | Collections renamed to the V2 names; `ClientSecretExpiryDate`. |
| V5 | 2026-01-23 | EInvoiceAPIAdmin | LiteDB 5, built-in password | + `AlertRules`, `SmtpProfiles`, `EmailTemplates`, `AlertSendLogs`. `DBVersion` 2026-01-21. |
| V6 | 2026-02-08 | EInvoiceAPIAdmin | LiteDB 5, built-in password | + `AuditLogs`. `DBVersion` 2026-02-08. |
| V7 | from 2026-09 | EInvoiceAPIAdmin | LiteDB 5, own password (`Database:Password`) | Hashed Admin passwords (`PasswordHash`, `SecurityStamp`); Microsoft mail connections. |
| V8 | current | ETA_EInvoiceAPI | LiteDB 5.0.17, own password, shared mode | + `SapEndpoints`, `NotificationSettings`. `DBVersion` 2026-11-06. |

LiteDB 4 data files are accepted too; they are upgraded to LiteDB 5 first.

## Migrate Database page

1. In Admin, open **Migrate Database** and choose the old file.
   - You only need a password for a V7 or later file from **another** server. No password, the built-in password of V2 to V6, and this server's own password are tried automatically.
2. Review what was detected: version, format, how it was opened, and the record count of each table.
3. Choose one:
   - **Migrate and import.** The old file's data replaces the same data in the live database, in one transaction:
     - Collections the old file doesn't have, such as SAP endpoints and the notifications API key when migrating V1 to V7, are kept.
     - A backup of the live database is saved next to it first, as `config.before-migration-<date-time>.db`. It's encrypted with this server's password and can be restored in Maintenance.
     - Admin accounts become the old file's accounts, and everyone is signed out.
     - EInvoiceAPI and ETANotifications use the new data from their next request.
   - **Download converted file.** You get the old data as a current-version file, encrypted with this server's password, to restore later.

What the migration does for every version:

- Converts the file to LiteDB 5 with the current collection names, encrypted with this server's `Database:Password`.
- Hashes plain-text Admin passwords. The same passwords keep working.
- Converts V2's expiry dates and normalizes old values: IDs, numbers and booleans, plus `UseXMLTOJSON` stored as `0`/`1`.
- Checks every record and every company/environment reference, and stops without changing anything if something doesn't match.
- Adds the current indexes and default records, and sets `DBVersion` to the current schema version.

The uploaded file is kept only in a temporary folder next to `config.db`, and deleted afterwards. Uploads that are never imported are removed after two hours. Both the import and the download are recorded in the audit log.

**Microsoft mail connections** in a V7 or later file only work on a server with the same Data Protection keys. Reconnect them in SMTP Profiles if they fail.

## Tests

`src/EInvoiceAPIAdmin/tests/VersionMigration.Regression` builds a file of each version and migrates it into a live database. It uses two real files: V2 written by LiteDB 5.0.21 the way EInvoiceAPI did, and a LiteDB 4.1.4 data file. It also covers unencrypted files, a missing or wrong password, a file that isn't a database, and the download path.
