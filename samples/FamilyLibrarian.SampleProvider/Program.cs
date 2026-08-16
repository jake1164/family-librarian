// A minimal, reference implementation of the Family Librarian external-provider
// protocol (see /docs and the M13 plan). See README.md for the full protocol.
// Endpoint registration lives in SampleProviderHost.Build so the conformance
// test can build and start a real instance without going through app.Run()'s
// blocking loop.
var app = FamilyLibrarian.SampleProvider.SampleProviderHost.Build(args);
app.Run();
