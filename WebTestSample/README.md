# WebTestSample

Minimal .NET project demonstrating the proposed `.cs` Code Step model.

## Projects

- `WebTestSample.csproj`: ordinary .NET class library referencing `LoadTest.Sdk`.
- `Scripts/CalculateValue.cs`: a normal C# class implementing `ICodedTest`.
- `../WebTestSample.Tests`: unit tests that invoke the Code Step directly with a `TestContext`.
- `Sample.webtest.yaml`: a minimal declarative WebTest definition.

## Open in VS Code

Open the `WebTestSample` folder (or the repository root) in VS Code. Install the Microsoft C# extension / C# Dev Kit and allow the projects to restore.

## Build and test

From the repository root:

```powershell
dotnet build .\WebTestSample\WebTestSample.csproj
dotnet test .\WebTestSample.Tests\WebTestSample.Tests.csproj
```

Set a breakpoint in `Scripts/CalculateValue.cs` and debug the test
`RunAsync_calculates_total_and_updates_test_context` from the Test Explorer.

## Current scope

This is an isolated proof of concept for authoring and debugging ordinary C# Code Steps.
The existing Engine still executes the current `.csx` Code Step format. Wiring compiled
`.cs` assemblies into WebTest execution and packaging is a later step; this sample
does not claim that `Sample.webtest.yaml` invokes `CalculateValue` yet.
