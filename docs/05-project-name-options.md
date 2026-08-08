# Family Librarian — Name Decision

**Status:** Decided; historical shortlist retained below  
**Date:** 2026-08-08

## Decision

The official product name is **Family Librarian**. The repository/project slug is
`family-librarian`, and .NET solution, assembly, and namespace names use
`FamilyLibrarian`.

The following content is retained as pre-decision background only. It must not be
used to select new namespaces, Docker image names, service names, or URLs.

The product is broader than a downloader or Calibre replacement. It combines:

- requests;
- discovery;
- series/author tracking;
- acquisition orchestration;
- security/approval;
- ebook/audiobook delivery.

Names that sound only like "download" or "Kindle" are therefore less desirable because they constrain the project conceptually.

---

## Strongest Options

### 1. BookHarbor

**Why it works:**

- implies a safe place where books arrive and are organized;
- fits quarantine/security and delivery;
- works for ebooks and audiobooks;
- not tied to Kindle, Calibre, or one acquisition source.

Possible tagline:

> Request, track, secure, and deliver your books.

---

### 2. BookRelay

**Why it works:**

- emphasizes orchestration rather than storage;
- books move from discovery -> acquisition -> validation -> delivery;
- works well with the provider/plugin architecture.

Possible tagline:

> From request to reader.

---

### 3. BookPilot

**Why it works:**

- suggests automation and guidance;
- fits series intelligence and recommendations;
- short and easy to remember.

Potential downside:

- more generic and likely to collide with existing product names.

---

### 4. ShelfPilot

**Why it works:**

- combines library/shelf concepts with automation;
- series tracking and delivery both fit;
- modern self-hosted-project feel.

Potential downside:

- "Shelf" names are becoming common in this software category.

---

### 5. BookTrail

**Why it works:**

- especially good for series and reading progression;
- evokes following a reader through authors/series;
- friendly family-facing name.

Potential downside:

- less obviously about acquisition/delivery.

---

## Other Good Options

### TomeTrack

Strong for series/history tracking; less friendly sounding.

### ReadRelay

Good orchestration name and works for audio + text.

### StoryRelay

Friendly but potentially sounds fiction-only.

### ShelfFlow

Good workflow/orchestration association.

### BookFlow

Very clear, but generic.

### PagePilot

Friendly and memorable, though audiobook support is less obvious.

### ReadHarbor

Broad enough for ebooks/audiobooks and has a safe-library feel.

### LibraryRelay

Accurate but sounds more institutional.

### BookBridge

Books move from request/source to reader; simple concept.

### ReadBridge

Similar but broad enough for audiobook listening.

### BookPath

Series/progression friendly.

### ChapterPath

Good reader-facing identity but less accurate for general acquisition.

### TomeFlow

Distinctive, slightly more technical.

### Bibliora

Invented brand-style name; flexible but doesn't immediately explain itself.

### Librivo

Invented name; library association, but should be checked carefully for conflicts.

---

## Names I Would Avoid

### KindleFinder

Too tied to Amazon and doesn't accommodate audiobook/media-library delivery.

### CalibreReplacement

Describes the origin of the project rather than the product.

### BookDownloader

Undersells series tracking, approval, delivery, and family workflow.

### Readarr 2 / Bookarr / similar

Would incorrectly imply an *arr clone and make the project seem acquisition-first.

### Audiobook-oriented names

The architecture deliberately supports ebooks and audiobooks equally.

---

## Historical shortlist recommendation

The initial top five were:

```text
BookHarbor
BookRelay
ShelfPilot
BookTrail
ReadRelay
```

### Historical architecture-oriented preference

```text
BookRelay
```

It describes the actual system well:

```text
Request
  ->
Discover
  ->
Acquire
  ->
Secure
  ->
Deliver
```

### Historical friendly-product preference

```text
BookHarbor
```

It sounds like a family-facing application rather than infrastructure.

### Historical series-tracking preference

```text
BookTrail
```

It naturally fits following an author/series and knowing what comes next.

---

## Naming checks for public release

Before publishing the selected Family Librarian name, check:

- GitHub organization/repository names;
- Docker Hub;
- NuGet;
- common domain names;
- existing software/products;
- US trademark conflicts if the project becomes significant.

The implementation naming conventions above are authoritative; public release checks can be completed in parallel with implementation.
