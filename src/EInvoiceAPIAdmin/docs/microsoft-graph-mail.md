# Microsoft Graph email setup

Microsoft Graph is an alternative delivery provider alongside the existing System.Net.Mail SMTP sender. It supports Microsoft 365 work/school and personal Outlook.com accounts with interactive Microsoft sign-in, including MFA. It does not enable password-based SMTP AUTH or bypass tenant policy.

## Register an application

1. In Microsoft Entra app registrations, create an application with supported account types **Accounts in any organizational directory and personal Microsoft accounts**.
2. Add a **Web** redirect URI matching the application's externally accessible HTTPS origin plus `/signin-microsoft-mail`. For example, for an application running at `https://localhost:7043`, register `https://localhost:7043/signin-microsoft-mail`. Use the actual HTTPS port from your launch settings. This implementation assumes hosting at the origin root, not a virtual subdirectory.
3. Add Microsoft Graph **delegated** permissions `Mail.Send` and `User.Read`. The application also requests `offline_access` so background alerts can refresh authorization. Do not substitute application permissions: personal Microsoft accounts do not support application-only mail sending.
4. Create a client secret. Enter its **value**, not its identifier, in the saved profile's Microsoft registration form. Each profile can use a different app registration. Tenant policy may require administrator consent even for delegated permissions.
5. Record the user-entered client/secret expiration date in the appropriate `CompEnv` record for existing expiration alerts. Do not add secret-expiration fields to mail profiles. Token expiry is managed internally and is distinct from the app secret's expiration.

## Configure each mail profile

1. Create or edit a mail profile, select **Microsoft Graph**, and save.
2. Open Edit and find **Microsoft app registration for this profile**.
3. Enter the **Application (client) ID** and **Client secret value**, then select **Save Microsoft registration**.
4. Select **Connect / reconnect Microsoft account** and complete sign-in and consent.

The secret input is always empty on page load. Leave it blank when saving to preserve an existing secret for the same application ID. Changing the ID requires a new secret. Changing either credential invalidates the old mailbox connection and any pending sign-in, so reconnect afterward. Saving an unchanged ID with an empty secret does not invalidate the connection.

Credentials are encrypted in a separate LiteDB `GraphAppRegistrations` collection, with protection scoped to the profile ID. They are not stored in the SMTP profile model or returned to the browser. **Remove Microsoft registration** removes the credentials and connection; **Disconnect** removes only the connection. Deleting a profile removes both. Switching a profile to SMTP disconnects its mailbox but retains its encrypted registration for later reuse.

Global `MicrosoftGraph:ClientId` / `MicrosoftGraph:ClientSecret` configuration is no longer used. Existing Graph profiles must have their credentials entered individually and be reconnected after this upgrade. Remove obsolete secrets from launch settings and server configuration; never commit them to source control. No application restart is needed after editing credentials once this code version is running. The OAuth handler uses per-request credentials without mutating shared options. The authority remains Microsoft's `common` v2 endpoint for both account types.

Use HTTPS. If HTTPS terminates at a reverse proxy, configure ASP.NET Core forwarded headers for trusted proxies so generated redirect URIs use the correct external HTTPS origin. Do not trust arbitrary forwarded headers. Configure an explicit allowed host list for deployment.

### Token protection and hosting

Access and refresh tokens are stored only in the separate LiteDB `GraphMailConnections` collection, encrypted with ASP.NET Core Data Protection. They are not exposed as profile model properties, form fields, JSON responses, or authentication-cookie tokens. Keep normal HTTP logging at a level that does not capture authorization headers, callback query strings, or token request/response bodies.

Before production use, provision a persistent Data Protection key ring for the application identity and protect it at rest with the platform's supported mechanism (for example, Windows DPAPI with a loaded service-account profile). ASP.NET Core's default key persistence depends on hosting configuration; verify it for IIS, services, or containers rather than assuming an ephemeral key ring survives a restart. Keep the application name `InvoiceWebApp` stable. Back up the key ring securely with the database, restricting access to the application identity. Missing or changed keys require reconnecting all Microsoft mailboxes.

The current LiteDB application and token-refresh lock are designed for a single running application instance. Do not deploy multiple instances against the same mailbox connection without a distributed token-cache/locking design.

Mail profiles are shared administrative resources, matching the existing application's authorization model. Every signed-in administrator can manage them; deploy behind appropriate administrative access controls. This feature does not change or harden the application's existing local login implementation.

## Connect and send

1. Create or edit an SMTP profile, select **Microsoft Graph (OAuth2)**, and save.
2. Open Edit, save this profile's Microsoft application ID and client secret, then select **Connect / reconnect Microsoft account**.
3. Sign in to the intended mailbox, complete MFA, and grant consent. Keep the initiating local application login active during the flow.
4. Open Edit again and verify the displayed connected mailbox before sending.
5. Enter a test recipient and select **Test Connection**.
6. Assign the saved profile to an alert rule, or make it the default, as with SMTP.

Graph uses `/me/sendMail`: it sends as the connected mailbox. SMTP host, port, encryption, username, password, From Email, and From Name fields are ignored for Graph. Sending as an arbitrary/shared mailbox is not implemented. Both HTML/plain-text bodies and To/CC recipients are supported. Sent messages are saved to Sent Items.

HTTP 202 means Microsoft accepted the message, not that delivery is guaranteed. Check the recipient mailbox and any nondelivery report. The application does not automatically retry Graph sends, because doing so after ambiguous failures could duplicate emails.

Existing profiles remain SMTP by default. SMTP continues to support None and STARTTLS, not implicit TLS or OAuth2. Graph is HTTPS and does not use SMTP encryption settings.

## Reconnection and troubleshooting

- **Registration not configured:** enter and save the application ID and client secret on this profile's Edit page. After Data Protection key loss, re-enter the credentials as well as reconnecting the mailbox.
- **Connection failed:** check supported account types, exact Web redirect URI, current secret value, delegated permissions, local login, and organizational consent/conditional-access policy. The UI deliberately does not echo OAuth response bodies or tokens.
- **Mailbox not available / forbidden:** the account needs an Exchange Online or Outlook.com mailbox and delegated `Mail.Send` consent. A Microsoft identity alone does not guarantee a usable mailbox.
- **Authorization could not be renewed:** check client secret rotation/expiration and tenant policy, then reconnect. Revoked consent, refresh-token expiry, or sign-in-frequency policies may require interaction; background sending is not guaranteed indefinitely.
- **Application ID changed / connection cannot be decrypted:** reconnect the mailbox.
- **Throttled:** wait before trying again. No automatic send retry is performed.
- **Disconnect:** removes locally stored tokens, but does not revoke Microsoft consent and cannot cancel an already in-flight message. Revoke application access in Microsoft account/tenant settings if required. Switching a saved profile to SMTP or deleting it also removes its local Graph connection.

## Validation

Run the offline checks from the repository root with `dotnet run --project tests/GraphMail.Regression/GraphMail.Regression.csproj --artifacts-path .artifacts/graph-validation --configuration Release`. This dependency-free console regression runner uses an in-memory database and simulated HTTP responses; it does not contact Microsoft or send email. It is not a Test Explorer test project.

Compilation and local regression checks can verify routing, legacy SMTP defaults, token protection and failure paths. Live authorization, Microsoft tenant policies, token renewal and delivery require an actual registered application and mailbox; those cannot be validated without operator configuration and consent.
