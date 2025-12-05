# cljr - Clojure for .NET
Clojure implementation that transpiles to C# and compiles to .NET IL. Works with standard .NET tooling.


# HERE BE DRAGONS (EXPERIMENTAL)

## Features

- `dotnet build`, `dotnet run`, `dotnet test` just work
- nREPL for editor integration (Calva, CIDER, etc.)
- Native AOT support (single binary, no runtime required)
- Blazor and ASP.NET compatible
- Type hints for .NET interop
- async/await support
- macro support (WIP, will rework in the future to be more like JVM Clojure)
- embeddable in .NET 10 applications (why I made this)

## Quick Start
There's samples under /samples. 

e.g.  to run the embedded repl demo (check foo.cljr for the code)

```bash
dotnet run --project samples/EmbeddedReplDemo
```

### nREPL

```bash
dotnet run --project src/Cljr.Cli -- nrepl
```

Connect your editor to `localhost:7888` (Calva: "Connect to a running REPL" → "Generic nREPL")

### Build & Run

```bash
# Standard .NET build
dotnet build
dotnet run

# Release build
dotnet publish -c Release
```

### Native AOT (single binary, no .NET runtime required)

Add to your `.csproj`:
```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

Then:
```bash
dotnet publish -c Release
# Output: single ~4MB native executable
```

## Samples

- `samples/BlazorDemo` - Blazor web app with hot reload via nREPL
- `samples/MinimalApi` - ASP.NET Minimal API

## Status

Experimental. Missing some core library functions. Works well enough for debugging and prototyping.
