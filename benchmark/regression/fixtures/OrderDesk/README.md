# OrderDesk Fixture

Small, dependency-free order-pricing workspace used by the regression benchmark.

```powershell
dotnet build .\OrderDesk.slnx
dotnet run --project .\tests\OrderDesk.Tests\OrderDesk.Tests.csproj
```

The console test runner exits with code `0` only when every check passes.
