// A single read-only lookup: the browser's own resolved IANA time zone, used
// to default a household member's quiet-hours time zone field (still editable
// afterward -- this is a convenience default, never authoritative).

export function getBrowserTimeZone() {
    return Intl.DateTimeFormat().resolvedOptions().timeZone;
}
