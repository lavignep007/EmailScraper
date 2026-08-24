# EmailScraper

EmailScraper is a .NET application for creating a local, structured and searchable archive of email messages.

It retrieves messages from Gmail, preserves the original email content, extracts attachments and metadata, reconstructs conversations into threads, generates human-readable PDF representations, and maintains a SQLite database and search index for querying the resulting archive.

The application supports both full archive creation and incremental synchronization.

> **Current version:** 0.1.0  
> **Status:** Early development

## Features

EmailScraper currently supports:

- Full Gmail mailbox extraction
- Incremental synchronization using Gmail history
- Original message preservation as EML files
- MIME parsing
- Attachment extraction
- Message metadata stored in SQLite
- Reconstruction of email conversations using message headers
- Individual message PDF generation
- Complete thread PDF generation
- Full-text/search indexing
- Archive validation and integrity checks
- Stable provider message IDs and RFC Message-ID preservation
- Incremental updates without re-downloading existing messages unnecessarily
- Deterministic branching thread reconstruction with revision history
- Revision-aware thread PDF regeneration

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

Attachments extracted from MIME messages.

### `pdf/messages/`

Human-readable PDF representation of every archived message, including messages that do not belong to a logical thread. Existing individual-message PDFs are not regenerated because the source message is immutable.

### `pdf/threads/`

PDF documents combining messages belonging to active reconstructed conversation threads. Files use the stable local thread ID as their name, for example `000123.pdf`.

Thread PDFs are regenerated only when the corresponding thread revision changes. Stale thread PDFs are removed. All PDF files are derived artifacts and can be regenerated from the archive.

## Processing Pipeline

The archive is produced in several stages:

```text
Gmail
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
```

At startup, the application chooses the synchronization mode automatically. A missing synchronization checkpoint triggers a full synchronization; otherwise, an incremental synchronization runs. When messages are downloaded, parsing, thread reconstruction, organization, PDF generation and search indexing run automatically in that order. When nothing is downloaded, the derived pipeline is skipped.

## Message Identity

EmailScraper preserves several forms of message identity.

### Provider message ID

`Messages.ProviderMessageId` stores the source provider's stable message identifier. For the Gmail provider, its value is the Gmail API message ID.

`Messages.ProviderThreadId` stores the provider's conversation identifier when available. It is useful metadata but does not uniquely identify one reconstructed logical branch.

### RFC Message-ID

The original RFC `Message-ID` header is preserved when available.

The RFC `Message-ID` is the preferred identity for correlating the same evidence between independently created or differently filtered databases. `ProviderMessageId` can also be used when the archives share the same provider context.

Internal SQLite row IDs are implementation details and should not be considered stable identifiers across independently created databases.

## Thread Reconstruction

Threads are reconstructed from standard email relationship headers, including:

- `Message-ID`
- `In-Reply-To`
- `References`

The reconstructed structure is a reply graph. Each active logical thread is a root-to-leaf path containing at least two locally available messages. Branches may occur at any depth, and shared ancestors may therefore belong to multiple logical threads.

A genuinely standalone message does not create a thread or thread PDF, but it still receives an individual-message PDF and a search document.

Each reconstructed thread has a unique `ThreadKey` derived from the leaf message's declared RFC lineage (`References`, `In-Reply-To` and its own `Message-ID`). A missing intermediate message can remain represented in this declared lineage even though no local `Messages` row is manufactured for it.

`Threads.Id` is a stable local identity. A linear extension retains it; after a split, one deterministic continuation retains it and additional branches receive new local IDs. Numeric thread IDs are not intended to match between independently created databases.

Current membership and parent relationships are stored in `MessageThreads`. A message may belong to multiple threads. `ThreadRevisions` and `ThreadRevisionMessages` preserve immutable historical memberships, while `ThreadRelations` records `Split` and `SupersededBy` relationships. `Threads.State` distinguishes active and superseded threads.

`Threads.RevisionHash` identifies the current ordered membership and parent structure. `PdfRevisionHash` records the revision represented by the current thread PDF.

## Full Extraction

A full extraction builds an archive from the available Gmail messages.

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

After an initial archive has been created, EmailScraper can query Gmail for changes occurring after the last known Gmail history position.

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
- MIME parsing
- QuestPDF

Additional libraries are used for message parsing, HTML processing and related archive operations.

## Configuration

The application requires Gmail API credentials and appropriate authorization to access the mailbox being archived.

Configuration includes the provider, archive output location, database location and addresses used to select relevant messages:

```json
{
  "EmailProvider": "Gmail",
  "ArchivePath": "./archive",
  "DatabasePath": "./archive/archive.db",
  "EmailAddresses": [
    "person@example.com"
  ]
}
```

`EmailAddresses` controls the Gmail query and the subsequent relevance check against sender and recipient headers.

Secrets and authentication credentials should not be committed to source control.

## Gmail Setup

EmailScraper currently supports Gmail through the Gmail API using OAuth 2.0.

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

Create an OAuth client with application type **Desktop app**, download its JSON credentials and make them available to the application as `credentials.json` in its runtime output directory. The first run opens the Google authorization flow and stores the resulting token locally.

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

- Gmail is currently the primary supported mail source.
- Thread reconstruction can evolve as additional edge cases are discovered.
- Internal numeric database IDs are not guaranteed to remain identical across independent full rebuilds.
- `SearchDocuments` stores at most one thread ID per message; use `MessageThreads` when all memberships are required.
- Search behavior and normalization are still evolving.
- Derived processing currently runs only when synchronization downloads messages; external archive changes do not automatically trigger a rebuild.
- The current schema is intended for fresh databases and does not provide migrations from earlier development schemas.

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

Implement additional acquisition providers against the existing provider interface and common archive model.

A future architecture may resemble:

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

### Perspective archives

Create derived, independently rebuilt archives representing the messages demonstrably visible to configured participant addresses. These projections must retain only visible messages and their referenced attachments while preserving RFC identity and partial-thread evidence.

## Development Status

Version `0.1.x` represents the first functional archival implementation.

The project currently prioritizes correctness, reproducibility and validation over API stability or polished user experience.

Database schemas, archive layout and internal APIs may change while the application remains pre-1.0.

## License

A license has not yet been specified.
