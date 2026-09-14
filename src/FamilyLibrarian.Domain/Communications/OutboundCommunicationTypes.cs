namespace FamilyLibrarian.Domain.Communications;

/// <summary>Known <see cref="OutboundCommunication.CommunicationType"/> values.</summary>
public static class OutboundCommunicationTypes
{
    public const string RequestStatusChanged = "request.status_changed";

    /// <summary>
    /// KINDLE-7's "did it arrive?" ask, now routed through the outbound
    /// pipeline (COMM-1 §A) instead of staying in-app-tray only. Also the one
    /// promptable type Matrix's inbound router (COMM-1 §D) recognizes.
    /// </summary>
    public const string KindleDeliveryConfirmationRequested = "delivery.kindle_confirmation_requested";
}
