namespace FamilyLibrarian.Contracts.Conversation;

/// <summary>The administrator's chat switches. <paramref name="MatrixConnectionReady"/> is false while the Matrix bot connection is off.</summary>
public sealed record ChatSettingsResponse(bool WebEnabled, bool MatrixEnabled, bool MatrixConnectionReady);

/// <summary>Changes only the switches that are sent; a null leaves that one as it is.</summary>
public sealed record SetChatSettingsRequest(bool? WebEnabled = null, bool? MatrixEnabled = null);
