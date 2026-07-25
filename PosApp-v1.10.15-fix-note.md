# PosApp v1.10.15 Build Fix

Fixed the `PosApp.SyncRegression` build failure reported on 2026-07-25.

## Fix

- Removed the local-function name collision in `tests/PosApp.SyncRegression/Program.cs`.
- Kept the generic reflection helper as `InvokeAsync<T>` for methods returning `Task<T>`.
- Renamed the non-generic reflection helper to `InvokeTaskAsync` for methods returning `Task`.
- Updated all three non-generic call sites accordingly.

This addresses:

- CS0411 at lines 81, 94, and 103: generic type arguments could not be inferred.
- CS0128 at line 175: local function `InvokeAsync` was already defined in the scope.
- CS8321 at line 175: the duplicate local helper was considered unused.

The EF1002, CS8629, and CA1416 entries in the supplied log are warnings and were not the cause of this build failure.

## Validation

Source-level checks confirm one generic `InvokeAsync<T>` helper, one non-generic `InvokeTaskAsync` helper, and all expected call sites. The current execution environment does not include the .NET SDK, so `dotnet run` could not be executed locally.
