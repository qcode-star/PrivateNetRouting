# PrivateNetRouting

A personal Windows toolkit (WPF, .NET 10 LTS) that keeps Claude Desktop running through a private
proxy chain: a local forward proxy with DoH resolution, a chain supervisor, a launcher, idle
auto-stop, an MSIX package updater, direct-access blocking, and diagnostics. It consolidates the
previous set of PowerShell/Python scripts into a single application.

## Status

The project is in early development. The working (production) setup still lives in the old script
set — the application replaces it module by module. Development rules are in [AGENTS.md](AGENTS.md).

## Build

```bash
dotnet build              # or the full path if PATH is stale in the session:
                          # "/c/Program Files/dotnet/dotnet.exe" build
dotnet test
dotnet run --project src/PrivateNetRouting.App
```

## Configuration

Everything environment-specific (endpoint addresses, ports, bypass domain lists, paths, timeouts)
lives in the application's user config and never reaches git. The repository holds only templates
with placeholders in `config/*.example.json`. The repository is private and anonymous: no real
addresses, domains, names, or secrets (rule #2 in AGENTS.md).