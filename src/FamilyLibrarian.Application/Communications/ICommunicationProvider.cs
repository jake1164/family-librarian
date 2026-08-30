namespace FamilyLibrarian.Application.Communications;

/// <summary>A registered communications transport, identified by a stable id (e.g. <c>"smtp"</c>).</summary>
public interface ICommunicationProvider
{
    string ProviderId { get; }
}
