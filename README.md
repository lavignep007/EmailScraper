# GmailArchive

GmailArchive is a .NET application for creating a local, structured and searchable archive of email messages.

It retrieves messages from Gmail, preserves the original email content, extracts attachments and metadata, reconstructs conversations into threads, generates human-readable PDF representations, and maintains a SQLite database and search index for querying the resulting archive.

The application supports both full archive creation and incremental synchronization.

> **Current version:** 0.1.0  
> **Status:** Early development

## Features

GmailArchive currently supports:

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
- Stable Gmail and RFC Message-ID preservation
- Incremental updates without re-downloading existing messages unnecessarily

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

Human-readable PDF representation of individual messages.

### `pdf/threads/`

PDF documents combining messages belonging to a reconstructed conversation thread.

PDF files are derived artifacts and can be regenerated from the archive.

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
Thread reconstruction
  │
  ├──► Message PDFs
  ├──► Thread PDFs
  └──► Search index
```

## Message Identity

GmailArchive preserves several forms of message identity.

### Gmail ID

The Gmail API message ID is stored as a stable identifier for the Gmail message.

### RFC Message-ID

The original RFC `Message-ID` header is preserved when available.

These identifiers should be preferred when correlating messages between archive generations.

Internal SQLite row IDs are implementation details and should not be considered stable identifiers across independently created databases.

## Thread Reconstruction

Threads are reconstructed using standard email relationship headers, including:

- `Message-ID`
- `In-Reply-To`
- `References`

This allows GmailArchive to reconstruct conversations independently of local database row IDs.

Each reconstructed thread has a unique `ThreadKey` representing its logical identity.

The SQLite `Threads.Id` value exists primarily as an internal relational key and should not be used as an external or permanent thread identifier.

Thread identity and cross-archive stability are areas of active development.

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
    ↓
Validate archive
```

A complete rebuild should be capable of recreating the derived archive from the source messages.

## Incremental Synchronization

After an initial archive has been created, GmailArchive can query Gmail for changes occurring after the last known Gmail history position.

New messages are downloaded and added to the existing archive.

Existing messages are not intentionally duplicated.

After synchronization, derived data such as thread membership, PDFs and search documents can be regenerated to incorporate the new messages.

Stable message identifiers allow successive archive versions to be compared even when internal database identifiers differ.

## Duplicate and Forwarded Messages

GmailArchive preserves distinct source messages rather than attempting to collapse messages solely because their content appears identical.

For example, an original message and a subsequently forwarded copy are separate email records and may have different Gmail IDs and RFC Message-IDs.

This preserves the source mailbox accurately while allowing higher-level analysis to identify related or substantively duplicated content separately.

## Search

Parsed message content is indexed in SQLite for local searching.

Searchable information can include message subjects, participants and message content.

Search functionality is currently under development and will continue to improve.

## Archive Validation

GmailArchive includes validation routines intended to detect inconsistencies such as:

- missing EML files
- empty or invalid EML files
- missing attachments
- orphaned attachment records
- duplicate identifiers
- invalid message/thread relationships
- missing generated artifacts
- database inconsistencies

Validation is intended to make archive-generation problems visible rather than silently producing an incomplete archive.

## Technology

GmailArchive is currently built using:

- .NET 10
- C#
- SQLite
- Gmail API
- MIME parsing
- QuestPDF

Additional libraries are used for message parsing, HTML processing and related archive operations.

## Configuration

The application requires Gmail API credentials and appropriate authorization to access the mailbox being archived.

Configuration includes the archive output location and Gmail authentication information.

Secrets and authentication credentials should not be committed to source control.

## Usage

The current application is console based.

Run the application:

```bash
dotnet run
```

The current development version exposes individual archive operations through an interactive menu.

Typical usage consists of either:

1. creating/rebuilding an archive from the mailbox; or
2. performing an incremental synchronization and regenerating affected archive data.

The user interface and workflow are expected to be simplified in future versions.

## Data Safety

Original EML messages are the most important archive artifacts.

Generated PDFs, reconstructed threads, indexes and other derived information should be considered reproducible data.

When manipulating an existing archive, maintaining a backup is recommended while the project remains in early development.

The application should not be considered a substitute for the original mail provider or a formally certified archival system.

## Current Limitations

GmailArchive is under active development.

Known architectural limitations include:

- Gmail is currently the primary supported mail source.
- Thread reconstruction can evolve as additional edge cases are discovered.
- Internal numeric database IDs are not guaranteed to remain identical across independent full rebuilds.
- Generated thread artifact management is being improved.
- Search behavior and normalization are still evolving.
- The console workflow contains development and diagnostic operations that will eventually be consolidated.

## Planned Development

Future development is expected to include:

### Simplified workflow

Reduce the interactive interface to the principal operations:

- Full archive/rebuild
- Incremental synchronization
- Search

Parsing, thread reconstruction, PDF generation, indexing and validation should become automatic pipeline stages rather than operations the user normally invokes individually.

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

Separate mailbox acquisition from archive processing so additional providers can be supported.

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

### Stable cross-archive thread identity

Improve thread identity so equivalent conversations can be correlated reliably between independently generated or filtered archive databases.

`ThreadKey` will remain the logical foundation while local SQLite IDs remain implementation details.

### Improved search normalization

Search should become insensitive to:

- capitalization
- accents/diacritics

For example, searches for:

```text
Québec
quebec
QUEBEC
```

should produce equivalent results where appropriate.

### Artifact generation

Improve PDF generation so stale derived artifacts are removed or replaced atomically when threads change.

## Development Status

Version `0.1.x` represents the first functional archival implementation.

The project currently prioritizes correctness, reproducibility and validation over API stability or polished user experience.

Database schemas, archive layout and internal APIs may change while the application remains pre-1.0.

## License

A license has not yet been specified.
