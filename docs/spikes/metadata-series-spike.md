# Metadata and Series Spike

**Status:** Preliminary evidence recorded — credentialed corpus run pending

**Research date:** 2026-08-09

**Decision owner:** Family Librarian maintainers

## Goal

Select the initial book-metadata providers and set honest rules for title,
author, edition, ISBN, cover, publication-date, and series information. The
result must preserve a provider-neutral catalog model and must not make any
external provider authoritative for the internal `Work`, `Edition`, `Author`, or
`Series` records.

## Findings to date

| Provider | Integration shape | Useful evidence | Constraint | Initial position |
| --- | --- | --- | --- | --- |
| Open Library | Public HTTP JSON API | Search returns work IDs, titles, authors, edition counts, cover references, and ISBN candidates. A live title-and-author search for *The Way of Kings* returned the expected work plus its editions. | Intended for low-volume, human-facing discovery; cache responses and identify the application. The documented rate limit is one request/second without identification and three requests/second with it. | Use as an initial work/author/edition source, with caching and per-provider throttling. Do not present its series data as complete until verified against the corpus. |
| Google Books | REST API | Its Volume resource includes title, authors, publisher, publication date, description, industry identifiers, image links, and availability information. | Public-data calls require an API key or OAuth identification. An unauthenticated spike request was rate limited, so a configured API key is required before a meaningful comparison can run. The documented Volume fields do not establish a provider series model. | Use as an independently configurable source for broad search, covers, descriptions, and edition/ISBN enrichment after a credentialed run. Do not use it as a series authority. |
| Hardcover | Authenticated GraphQL API | The official API and documentation expose GraphQL access and require a Bearer token. Its data model is the candidate supplemental source for series coverage. | A maintainer-provided API token and a review of the applicable terms/rate limits are needed before live testing. | Defer the decision. Evaluate only as an optional supplemental series source; it must never be the domain's sole identity source. |

Sources reviewed:

- [Open Library API and usage guidelines](https://openlibrary.org/developers/api)
- [Open Library Search API](https://openlibrary.org/dev/docs/api/search)
- [Google Books `volumes.list`](https://developers.google.com/books/docs/v1/reference/volumes/list)
- [Google Books Volume resource](https://developers.google.com/books/docs/v1/reference/volumes)
- [Hardcover API documentation repository](https://github.com/hardcoverapp/hardcover-docs)
- [Hardcover API endpoint](https://api.hardcover.app/)

Additional design input:

- [External comparative research](../../.ai_docs/family-librarian-metadata-provider-normalization.md)

The external comparative research is a useful set of hypotheses and references,
not a decision record or an authoritative source. Its claims about other projects,
provider quality, and default priority must be confirmed against current upstream
code/documentation and this spike's corpus before they affect production behavior.

## Lessons accepted from comparative research

The following patterns reinforce the existing architecture and are durable enough
to carry into implementation:

- keep internal UUIDs authoritative and store typed provider identifiers as
  aliases/provenance;
- normalize provider responses into intermediate candidates rather than directly
  into persistent domain entities;
- resolve a `Work` separately from its `Edition` records: matching ISBNs are strong
  edition evidence, while different ISBNs do not prove different Works;
- resolve important fields using field-level evidence and policy rather than one
  global winning provider;
- preserve manual admin corrections and prevent later refreshes from overwriting
  locked canonical values;
- tolerate partial provider failure and expose redacted provider health states such
  as healthy, rate-limited, authentication-failed, degraded, and disabled;
- cache and throttle each provider independently, honoring `Retry-After` and
  stopping repeated calls after authentication failures; and
- log enough non-secret evidence to explain grouping, conflicts, and automatic
  resolution decisions.

The Work/Edition/media-manifestation distinction is also sound. A deliverable
audiobook production or ebook release may need narrator, duration, abridgement,
region, ASIN, or format identity that belongs neither on `Work` nor on a downloaded
`MediaAsset`. The current catalog slice should reserve that boundary in contracts
and terminology, but should not add a manifestation entity until audiobook or
acquisition work requires it.

## Recommendations not adopted yet

- Hardcover is not the default or primary provider until credentialed corpus data
  demonstrates its value and its current terms/rate limits are acceptable.
- Numeric confidence weights copied from another project are not evidence. Begin
  with conservative deterministic rules and tune scores only from committed
  regression fixtures.
- The initial slice will not add Audible, Audnexus-style data, local-file scanning,
  comics, retailer providers, or a plugin marketplace. Those belong to later
  audiobook/acquisition slices.
- Raw provider payloads are not retained indiscriminately. Keep only bounded,
  redacted snapshots when permitted and operationally useful; normalized
  assertions and external references remain the durable record.
- Administrator-configurable field priority is a future refinement. The first
  implementation uses reviewed field policies in code/configuration and exposes
  provider enablement and credentials through the Admin UI; it does not ship a
  complex merge-rule editor before the corpus justifies one.

## Preliminary decision

The catalog must support multiple enabled `IBookMetadataProvider`
implementations. Start implementation with independently configurable **Open
Library** and **Google Books** providers, but keep the demo provider available
for development and tests. The providers are queried independently; the host
normalizes their responses and keeps their IDs and external references as
provenance.

No provider is selected as the authoritative series source yet. The initial UI
must show a series name or position only when supplied by a source and must
label it as provider-supplied until the corpus establishes field-level confidence.
It must not infer series order from search ranking or fill gaps by guesswork.

Hardcover remains an explicitly optional follow-up. Adding it requires a
credentialed corpus result that materially improves correct series membership or
ordering, plus a terms, rate-limit, and operational review.

## Provider merge rules for the first catalog slice

1. Preserve every candidate's `ProviderId`, external ID, source URL, and
   normalized field provenance.
2. Group editions by exact ISBN-13 first. Never manufacture an ISBN from a
   provider ID.
3. For candidates without a matching ISBN, show title-and-author similarities as
   possible matches for user or admin review; do not silently merge them.
4. Treat the selected candidate as the source for its descriptive fields unless
   an administrator corrects a canonical value. Keep the original source value
   and provenance for review.
5. Render incomplete dates at their available precision rather than inventing a
   day or month.
6. Store zero or more series candidates with the provider and confidence context.
   A conflicting or missing series value is a review condition, not a replacement
   for an edited canonical value.

## Representative corpus

Run each query in the two forms most likely to be used by a family member:
title only, and title plus author where ambiguity is expected. For each returned
candidate, retrieve the provider's detail endpoint before scoring it. Record the
raw response only in a local, access-controlled test artifact; commit the
normalized score sheet, not credentials or potentially licensed payloads.

| Scenario | Query | Expected work and test focus |
| --- | --- | --- |
| Exact classic with many editions | `The Hobbit Tolkien` | Correct work, Tolkien, editions/ISBNs, Middle-earth context. |
| Children's series | `A Wrinkle in Time` | Correct work, Madeleine L'Engle, Time Quintet membership and position. |
| Large fantasy series | `The Way of Kings Brandon Sanderson` | Correct work, Stormlight Archive membership and position despite editions/parts. |
| Novella series | `All Systems Red Martha Wells` | Murderbot Diaries membership and first position. |
| Long-running series | `The Last Devil to Die Richard Osman` | Thursday Murder Club membership and fourth position. |
| Prequel ordering | `The Ballad of Songbirds and Snakes Suzanne Collins` | Hunger Games membership with an explicitly represented prequel position. |
| Recent fantasy series | `Emily Wilde's Encyclopaedia of Faeries Heather Fawcett` | Correct punctuation, series membership, and first position. |
| Stand-alone novel | `Project Hail Mary Andy Weir` | Correct work and no invented series. |
| Book with multiple media editions | `Dune Frank Herbert` | Correct work, edition/ISBN grouping, and evidence of audiobook/ebook distinctions where supplied. |
| Ambiguous short title | `Beloved` | Correct Toni Morrison work ahead of similarly titled results. |
| Non-fiction | `Braiding Sweetgrass Robin Wall Kimmerer` | Subtitle, author, dates, and edition/ISBN behavior. |
| Translation | `The Three-Body Problem Cixin Liu` | Original/translated editions are not silently merged. |
| Upcoming title | a current, verified forthcoming family title | Publication-status/date precision and absence of a fabricated edition. |
| Misspelling | `Projec Hail Mary` | Search tolerance and safe presentation of ambiguous matches. |
| ISBN-only | `9780593135204` | Exact edition match and work relationship. |

## Scorecard and gates

For each provider and corpus row, record `Pass`, `Partial`, `Fail`, or `Not
supplied` for the following fields:

| Measure | Pass condition |
| --- | --- |
| Work resolution | Intended work appears in the first five results for an exact query, or is returned by ISBN. |
| Author resolution | Expected primary author is present and no different work is selected. |
| Edition/ISBN | At least one correct ISBN is returned when a known ISBN query is used; editions are distinguishable rather than collapsed. |
| Cover/description | A usable cover URL and/or description is supplied where licensing allows display. Missing data is `Not supplied`, not a failure. |
| Publication data | The available year/month/day precision is retained correctly. |
| Series membership | The series is correct when one is expected; stand-alone works do not gain a series. |
| Series position | The displayed ordinal/prequel relationship is correct and is never inferred when absent. |
| Stability | The provider succeeds within configured timeout/rate limits, with a clear diagnostic on failure. |

Adopt a provider for a field only when it has no critical misidentifications and
meets the following corpus thresholds:

- work and author resolution: at least 90% `Pass`;
- edition/ISBN: at least 80% `Pass` or a documented `Not supplied` reason;
- series membership and position: at least 85% `Pass` before a field can be
  displayed without an uncertainty label.

Any wrong series order, wrong work selected, or conflict with an administrator
correction is a critical result. It blocks automatic merge for that field and is
recorded as a regression fixture.

Before enabling automatic cross-provider linking, deterministic fixtures must also
cover:

- the same Work represented by different hardcover, paperback, and ebook ISBNs;
- conflicting validated ISBNs at the Edition level;
- same title/different author and same title-and-author/different Work cases;
- translations, regional titles, revised editions, omnibuses, and box sets;
- missing or contradictory publication dates;
- series positions such as `Prequel`, `0.5`, and `1.5`;
- two distinct authors with the same normalized name and a known author alias;
- provider timeout, `429`, authentication failure, and malformed individual
  records; and
- an administrator-locked canonical field surviving metadata refresh.

Audiobook production/region/narrator fixtures are required when the deferred
media-manifestation slice begins, not for the initial catalog implementation.

## Execution and operational requirements

1. Obtain a Google Books API key and a Hardcover API token through the
   maintainer's normal secret-management path. Do not place either in source,
   committed test fixtures, browser code, or logs.
2. Give each provider a server-side timeout, cancellation support, bounded retry
   policy for transient errors, per-provider concurrency limit, and cache. Respect
   Open Library's identification and request-rate guidance.
3. Run the corpus manually or with opt-in integration tests. Live provider checks
   stay out of normal CI to avoid secret exposure, rate limits, and flaky results.
4. Save a dated normalized score sheet and representative redacted fixtures. Add
   contract tests for every confirmed mapping and regression.
5. Update this decision record with the scored results, then select field-level
   provider priority. Do not add Hardcover unless its incremental series value is
   demonstrated.
6. Providers that require credentials are disabled until an administrator
   configures them through the server-backed Integrations UI or the deployment
   supplies an explicitly external, read-only secret. The server never returns a
   stored secret to the WebAssembly client.

## Completion criteria

This spike becomes **Complete** only after the credentialed Open Library, Google
Books, and (if evaluated) Hardcover corpus runs are scored; provider terms and
rate limits have been reviewed; and the field-level source priority and series UI
confidence rule are updated above. Until then, production metadata mapping and
unqualified series claims remain intentionally blocked.
