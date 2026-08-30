# EmailScraper

EmailScraper is a .NET application for creating a local, structured and searchable archive of email messages.

It retrieves messages from Gmail and Microsoft 365/Outlook, preserves the original email content, extracts attachments and metadata, reconstructs conversations into threads, generates human-readable PDF representations, and maintains a SQLite database and search index for querying the resulting archive.

The application supports both full archive creation and incremental synchronization.

> **Current version:** 0.3.0
>
> **Status:** Early development

## Features

EmailScraper currently supports:

- Full and incremental Gmail extraction scoped to configured addresses
- Full and incremental Microsoft 365 / Outlook extraction through Microsoft Graph
- Provider-neutral email source configuration for multiple mailboxes
- Incremental synchronization using Gmail history and provider checkpoints
- Original message preservation as EML files
- MIME parsing
- Attachment extraction
- Message metadata stored in SQLite
- Reconstruction of email conversations using message headers
- Individual message PDF generation
- Complete thread PDF generation
- Full-text/search indexing
- Archive validation and integrity checks
- Stable `(SourceKey, ProviderMessageId)` identities and RFC Message-ID preservation
- Separate raw Graph, raw MIME and canonical MIME message identities
- Portable relative paths for archived EML files and content-addressed attachments
- Per-message attachment filenames and explicit MIME part roles
- Exact Unicode subjects plus a separate search-normalized subject
- Incremental updates without re-downloading existing messages unnecessarily
- Deterministic branching thread reconstruction with revision history
- Revision-aware thread PDF regeneration
- Independently rebuilt perspective archives for configured participant addresses
- Provider-state tracking for drafts and scheduled messages
- Automatic exclusion of unsent messages from perspective archives
- Explicit identification of partial threads with unavailable ancestors

## Archive Structure

A generated archive contains the database, original messages, extracted attachments and generated PDF documents.

Typical structure:

```text
archive/
├── archive.db
├── messages/
├── attachments/
└── pdf/
    ├── messages/
    └── threads/
```

### `archive.db`

SQLite database containing parsed message metadata, reconstructed thread information, attachment metadata and search data.

### `messages/`

Original email messages stored as `.eml` files.

These files preserve the original MIME message and headers and should be considered the authoritative raw representation of an archived email.

### `attachments/`

Attachments extracted from MIME messages. Blob files are addressed by SHA-256 with a deterministic MIME-based extension, while the original filename belongs to each `MessageAttachments` occurrence. `Attachments.RelativePath` is the portable path used when resolving the archive on another operating system; legacy `FilePath` values may remain as historical provenance.

### `pdf/messages/`

Human-readable PDF representation of every archived message, including messages that do not belong to a logical thread. Existing individual-message PDFs are not regenerated because the source message is immutable.

### `pdf/threads/`

PDF documents combining messages belonging to active reconstructed conversation threads. Files use the stable local thread ID as their name, for example `000123.pdf`.

Thread PDFs are regenerated only when the corresponding thread revision changes. Stale thread PDFs are removed. All PDF files are derived artifacts and can be regenerated from the archive.

## Processing Pipeline

The archive is produced in several stages:

```text
Configured email sources
  ├── Gmail
  └── Microsoft 365 / Outlook
  │
  ▼
Message acquisition
  │
  ▼
EML preservation
  │
  ▼
MIME parsing
  │
  ├──► Metadata
  └──► Attachments
  │
  ▼
SQLite database
  │
  ▼
Branching thread reconstruction
  │
  ├──► Organization and display names
  ├──► Individual message PDFs
  ├──► Revision-aware thread PDFs
  └──► Full-text search index
  │
  ▼
Draft/scheduled-state reconciliation
  │
  ▼
Configured perspective archives
  ├──► Exact participant filtering
  ├──► Unsent-message exclusion
  └──► Independent EML, database, attachment, PDF and search artifacts
```

At startup, the application chooses the synchronization mode automatically for each source. A missing source checkpoint triggers a full synchronization; otherwise, an incremental synchronization runs. When messages are downloaded, parsing, thread reconstruction, organization, PDF generation and search indexing run automatically in that order. When nothing is downloaded, those complete-archive derived stages are skipped. Gmail draft/scheduled and Microsoft 365 outbox state are reconciled on every run. Configured perspective archives are then evaluated and rebuilt only when their retained evidence or address set has changed.

## Message Identity

EmailScraper preserves several forms of message identity.

### Provider message ID

`Messages.ProviderMessageId` stores the source provider's stable message identifier. For the Gmail provider, its value is the Gmail API message ID.

`Messages.ProviderThreadId` stores the provider's conversation identifier when available. It is useful metadata but does not uniquely identify one reconstructed logical branch.

### RFC Message-ID

The original RFC `Message-ID` header is preserved when available. `Messages.MessageIdRaw` stores the raw MIME header value, `MimeMessageIdCanonical` stores the parsed value only when it is valid, and `GraphInternetMessageIdRaw` preserves Microsoft Graph's independent identifier when supplied. These values are never silently substituted for one another.

The RFC `Message-ID` is the preferred identity for correlating the same evidence between independently created or differently filtered databases. `ProviderMessageId` can also be used when the archives share the same provider context.

Internal SQLite row IDs are implementation details and should not be considered stable identifiers across independently created databases.

## Thread Reconstruction

Threads are reconstructed from standard email relationship headers, including:

- `Message-ID`
- `In-Reply-To`
- `References`

The reconstructed structure is a reply graph. Each active logical thread is normally a root-to-leaf path containing at least two locally available messages. Branches may occur at any depth, and shared ancestors may therefore belong to multiple logical threads.

A genuinely standalone message does not create a thread or thread PDF, but it still receives an individual-message PDF and a search document. A locally standalone reply that declares unavailable ancestors through `In-Reply-To` or `References` does create a partial thread; `IsPartial` and `MissingAncestorCount` make that incompleteness explicit.

Each reconstructed thread has a unique `ThreadKey` derived from the leaf message's declared RFC lineage (`References`, `In-Reply-To` and its own `Message-ID`). A missing intermediate message can remain represented in this declared lineage even though no local `Messages` row is manufactured for it.

`Threads.Id` is a stable local identity. A linear extension retains it; after a split, one deterministic continuation retains it and additional branches receive new local IDs. Numeric thread IDs are not intended to match between independently created databases.

Current membership and parent relationships are stored in `MessageThreads`. A message may belong to multiple threads. `ThreadRevisions` and `ThreadRevisionMessages` preserve immutable historical memberships, while `ThreadRelations` records `Split` and `SupersededBy` relationships. `Threads.State` distinguishes active and superseded threads.

`Threads.RevisionHash` identifies the current ordered membership and parent structure. `PdfRevisionHash` records the revision represented by the current thread PDF.

## Full Extraction

A full extraction builds an archive from the available messages for every configured source.

Conceptually:

```text
Acquire messages
    ↓
Store EML
    ↓
Parse messages
    ↓
Extract attachments
    ↓
Build threads
    ↓
Generate PDFs
    ↓
Build search index
```

A complete rebuild should be capable of recreating the derived archive from the source messages.

## Incremental Synchronization

After an initial archive has been created, EmailScraper can query Gmail history or Microsoft Graph using the stored source checkpoint.

New messages are downloaded and added to the existing archive.

Existing messages are not intentionally duplicated.

When new messages are downloaded, the application automatically parses the archive, rebuilds threads, updates organization metadata, generates required PDFs and rebuilds the search index.

When no messages are downloaded, these derived stages are skipped.

Stable message identifiers allow successive archive versions to be compared even when internal database identifiers differ.

## Duplicate and Forwarded Messages

EmailScraper preserves distinct source messages rather than attempting to collapse messages solely because their content appears identical.

For example, an original message and a subsequently forwarded copy are separate email records and may have different provider message IDs and RFC Message-IDs.

This preserves the source mailbox accurately while allowing higher-level analysis to identify related or substantively duplicated content separately.

## Search

Parsed message content is indexed in SQLite for local searching.

Searchable information can include message subjects, participants and message content.

Search functionality is currently under development and will continue to improve.

## Archive Validation

EmailScraper includes validation routines intended to detect inconsistencies such as:

- missing EML files
- empty or invalid EML files
- missing attachments
- orphaned attachment records
- duplicate identifiers
- invalid message/thread relationships
- database inconsistencies

Validation is intended to make archive-generation problems visible rather than silently producing an incomplete archive.

## Technology

EmailScraper is currently built using:

- .NET 10
- C#
- SQLite
- Gmail API
- Microsoft Graph API
- MIME parsing
- QuestPDF

Additional libraries are used for message parsing, HTML processing and related archive operations.

## Configuration

The application requires the appropriate authorization for each configured source: a Google OAuth desktop-client file for Gmail, or a Microsoft Entra application client ID for Microsoft 365/Outlook.

Configuration includes the complete archive paths and one or more email sources. Each source has its own provider identity, mailbox, filter and token settings:

```json
{
  "ArchivePath": "./archive",
  "DatabasePath": "./archive/archive.db",
  "EmailSources": [
    {
      "Id": "personal-gmail",
      "Name": "Personal Gmail",
      "Provider": "Gmail",
      "MailboxAddress": "person@gmail.com",
      "FilterAddresses": ["person@gmail.com"],
      "CredentialsPath": "credentials.json",
      "TokenPath": "token/personal-gmail/gmail"
    },
    {
      "Id": "work-outlook",
      "Name": "Work Outlook",
      "Provider": "Microsoft365",
      "MailboxAddress": "person@contoso.com",
      "FilterAddresses": ["person@contoso.com"],
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "TenantId": "common",
      "TokenPath": "token/work-outlook/microsoft365.json"
    }
  ],
  "PerspectiveArchives": [
    {
      "Name": "Moriarty",
      "ArchivePath": "./moriarty-archive",
      "EmailAddresses": [
        "opponent@example.com",
        "opponent.alias@example.com"
      ]
    }
  ]
}
```

The top-level `ArchivePath` and `DatabasePath` identify the complete source archive. `EmailSources` may contain Gmail and Microsoft 365/Outlook sources; `Id` must be stable and unique. `MailboxAddress` is checked against the authenticated account. `FilterAddresses` controls acquisition and relevance checks against exact sender and recipient mailboxes. An empty filter means all messages visible to that provider are eligible.

For Gmail, `CredentialsPath` points to the downloaded OAuth desktop-client JSON. `TokenPath` selects the local refresh-token cache; if omitted, it defaults to `token/<source-id>/gmail`. For Microsoft 365, `ClientId` is the Entra application (client) ID, `TenantId` is normally `common` for a mixed/personal sign-in or your tenant ID for a single organization, and `TokenPath` defaults to `token/<source-id>/microsoft365.json`.

`PerspectiveArchives` is optional. `Name` is a descriptive label used in console reporting and the perspective manifest; it does not control filtering. Each profile's `ArchivePath` identifies its derived output, while its `EmailAddresses` define visibility. A profile contains only messages where one of those addresses appears as an exact mailbox in `From`, `To`, `Cc` or visible `Bcc`. Multiple addresses in one profile are treated as aliases of the same perspective. On every run, Gmail's native `in:drafts` and `in:scheduled` searches are reconciled into the provider-neutral `Messages.IsUnsent` field. Unsent messages remain in the complete source archive, with their state recorded, but are excluded from every perspective archive.

Perspective output must be separate from, and not nested inside, the complete source archive. Retained EML files are copied into a staging archive; attachments, threads, revisions, PDFs and search data are rebuilt exclusively from those retained messages. The completed staging archive replaces the previous perspective only after successful generation. A manifest fingerprint prevents unnecessary rebuilding when neither the retained evidence nor address set changed.

Secrets and authentication credentials should not be committed to source control.

## Gmail Setup

EmailScraper supports Gmail through the Gmail API using OAuth 2.0 delegated access.

The application uses a **Desktop OAuth client** and requests read-only access to Gmail.

### 1. Create a Google Cloud project

Open the Google Cloud Console and create a new project, or select an existing project you want to use for EmailScraper.

The project is only used to register the application with Google and enable access to the Gmail API.

### 2. Enable the Gmail API

In the selected Google Cloud project:

1. Open **APIs & Services**.
2. Find **Gmail API**.
3. Enable it for the project.

The Gmail API must be enabled before OAuth credentials can be used to access Gmail.

### 3. Configure the OAuth consent screen

Open:

**Google Auth Platform → Branding**

If the Google Auth Platform has not yet been configured, select **Get Started**.

Configure at least:

- **App name:** `EmailScraper`
- **User support email:** your email address
- **Contact email:** your email address

For the audience:

- Choose **Internal** if the application is used only within a Google Workspace organization and that option is available.
- Otherwise choose **External**.

For a personal Gmail account, **External** will normally be used.

If the application remains in **Testing** mode, add the Gmail account that will be archived under:

**Google Auth Platform → Audience → Test users**

Google only allows configured test users to authorize an External application while it is in testing mode.

### 4. Configure Gmail permissions

EmailScraper currently requires read-only Gmail access.

Under:

**Google Auth Platform → Data Access**

add the Gmail scope:

```text
https://www.googleapis.com/auth/gmail.readonly
```

### 5. Create the desktop OAuth client

Create an OAuth client with application type **Desktop app**, download its JSON credentials and place it at the configured `CredentialsPath` (by default `credentials.json` in the runtime directory). The first run opens the Google authorization flow and stores a refresh-token cache at `TokenPath`. The application requests only `https://www.googleapis.com/auth/gmail.readonly`; it does not send, delete or modify messages. See the [Gmail API .NET quickstart](https://developers.google.com/gmail/api/quickstart/dotnet) for the current Google Console screens.

## Microsoft 365 / Outlook Setup

The Microsoft 365 provider reads the authenticated mailbox through Microsoft Graph. It uses delegated device-code authentication, stores the access/refresh-token cache locally, and requests these scopes:

```text
offline_access
Mail.Read
User.Read
```

No client secret is required for this local console application. Keep the client ID and token cache private, and never commit the cache to source control.

### 1. Register the application in Microsoft Entra ID

1. Open the [Microsoft Entra admin center](https://entra.microsoft.com/).
2. Go to **Entra ID → App registrations → New registration**.
3. Give the application a name such as `EmailScraper`.
4. Select the account type matching the mailbox: **single tenant** for one organization, or a multitenant/personal-account option when required.
5. Select **Register** and copy the **Application (client) ID** into `EmailSources[].ClientId`.

The provider uses device-code flow, so no web redirect URI or client secret is needed. In **Authentication → Advanced settings**, enable **Allow public client flows**. This is the Entra setting that permits a native/console application to use device authorization.

### 2. Add Microsoft Graph permissions

Under **API permissions → Add a permission → Microsoft Graph → Delegated permissions**, add:

- `Mail.Read`
- `User.Read`
- `offline_access` (usually granted as an OpenID/OAuth scope)

Grant administrator consent if the tenant requires it. The first Microsoft 365 run prints a device sign-in URL and code; open the URL in a browser, authenticate as the configured mailbox, and approve the requested permissions. The token is then cached at `TokenPath` and reused on later runs.

### 3. Configure the source

```json
{
  "Id": "outlook",
  "Name": "Outlook mailbox",
  "Provider": "Microsoft365",
  "MailboxAddress": "person@contoso.com",
  "FilterAddresses": ["person@contoso.com"],
  "ClientId": "your-application-client-id",
  "TenantId": "your-tenant-id-or-common",
  "TokenPath": "token/outlook/microsoft365.json"
}
```

`Provider` also accepts `Outlook` or `Office365`. `TenantId` may be `common`, `organizations`, `consumers`, or a concrete directory (tenant) ID, depending on the account type selected during registration. The provider downloads raw message bytes from Graph, preserves them as EML, records the Graph conversation ID and continues through the same MIME, threading, PDF, search and validation pipeline as Gmail. See Microsoft's [app registration guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app), [desktop app configuration](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration) and [device-code flow documentation](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code) for portal details.

## Usage

The current application is console based.

Run the application:

```bash
dotnet run --project src/EmailScraper
```

Run the test suite from the repository root:

```bash
dotnet test
```

The synchronization and archive-processing pipeline is automatic. After it finishes, the console remains open and repeatedly offers:

- `[S]` Search the archive
- `[C]` Validate archive integrity
- `[Enter]` Exit

The first run performs a full synchronization. Later runs perform an incremental synchronization from the stored provider checkpoint. If Gmail reports that the stored history position is no longer available, the provider falls back to a full synchronization.

## Data Safety

Original EML messages are the most important archive artifacts.

Generated PDFs, reconstructed threads, indexes and other derived information should be considered reproducible data.

When manipulating an existing archive, maintaining a backup is recommended while the project remains in early development.

The application should not be considered a substitute for the original mail provider or a formally certified archival system.

## Current Limitations

EmailScraper is under active development.

Known architectural limitations include:

- Gmail and Microsoft 365/Outlook are supported mail sources.
- Thread reconstruction can evolve as additional edge cases are discovered.
- Internal numeric database IDs are not guaranteed to remain identical across independent full rebuilds.
- `SearchDocuments` stores at most one thread ID per message; use `MessageThreads` when all memberships are required.
- Search behavior and normalization are still evolving.
- Derived processing currently runs only when synchronization downloads messages; external archive changes do not automatically trigger a rebuild.
- Additive schema migrations are applied when the database starts; keep a backup before upgrading an archive while the project remains pre-1.0.

## Planned Development

Future development is expected to include:

### Multiple mailbox profiles

Allow multiple email accounts to be archived independently, with output organized by mailbox/profile.

For example:

```text
archives/
├── user1@example.com/
│   └── ...
└── user2@example.com/
    └── ...
```

### Additional email providers

Additional acquisition providers can be implemented against the existing provider interface and common archive model. The current architecture already supports:

```text
               ┌── Gmail
               │
Email Source ──┼── Outlook / Microsoft 365
               │
               └── Other providers
                        │
                        ▼
                 Common message model
                        │
                        ▼
                 Archive pipeline
```

The parsing, threading, PDF, indexing and validation layers should not need to know which provider supplied a message.

## Development Status

Version `0.3.0` adds the provider-neutral source model and Microsoft 365/Outlook acquisition through Microsoft Graph, while retaining Gmail extraction and the existing perspective/archive pipeline. It also strengthens attachment provenance, portable paths, MIME-role classification and provider/MIME identity preservation.

The project currently prioritizes correctness, reproducibility and validation over API stability or polished user experience.

Database schemas, archive layout and internal APIs may change while the application remains pre-1.0.

## License

A license has not yet been specified.
