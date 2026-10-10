// HUMAN-ACQ-1 Phase 3: thin wrapper around vendored noVNC (lib/novnc) so the
// Blazor page never touches the RFB client's own API surface directly. Never
// solves anything or interprets frames -- this only opens/closes the socket
// and forwards a couple of lifecycle events back to .NET.

export async function open(container, wsUrl, dotNetRef) {
    const { default: RFB } = await import("../lib/novnc/core/rfb.js");

    const rfb = new RFB(container, wsUrl);
    rfb.scaleViewport = true;
    rfb.showDotCursor = true;

    rfb.addEventListener("connect", () => {
        dotNetRef.invokeMethodAsync("OnRemoteViewConnected");
    });
    rfb.addEventListener("disconnect", (event) => {
        dotNetRef.invokeMethodAsync("OnRemoteViewDisconnected", !!event.detail?.clean);
    });

    return rfb;
}

export function close(rfb) {
    if (rfb) {
        rfb.disconnect();
    }
}
