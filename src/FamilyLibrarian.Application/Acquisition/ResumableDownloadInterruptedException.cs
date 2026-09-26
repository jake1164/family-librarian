namespace FamilyLibrarian.Application.Acquisition;

/// <summary>A resumable transfer stopped temporarily while its verified partial file remains available.</summary>
public sealed class ResumableDownloadInterruptedException(string message, Exception? innerException = null)
    : IOException(message, innerException);
