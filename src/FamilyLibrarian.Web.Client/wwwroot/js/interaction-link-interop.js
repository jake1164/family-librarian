// HUMAN-ACQ-1 D11: the magic-link token travels in the URL fragment (never the
// path or query), and is read once by InteractionLink.razor on first render.
// This immediately scrubs it from the address bar and browser history -- a
// bookmark, a screenshot, or the back button must never surface it again.

export function clearFragment() {
    history.replaceState(null, "", location.pathname + location.search);
}
