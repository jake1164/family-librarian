namespace FamilyLibrarian.Contracts.Accounts;

/// <summary>A household member's own audiobook narration preference.</summary>
public sealed record AudiobookNarrationPreferenceResponse(string Preference);

public sealed record SetAudiobookNarrationPreferenceRequest(string Preference);
