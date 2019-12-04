# v2.2.0

* Support for .NET Standard 2.0

# v2.1.0

* Enable source link

# V2.0.0

* All features from V1.2.0, but with new builders moved to FSharp.Control.Tasks.V2 namespace to avoid dependency conflicts.
* Target lowest FSharp.Core that supports .NET Standard 1.6.

# v1.2.0-rc

* Support for .NET frameworks 4.5, 4.6, and 4.7 in NuGet package.
* New operator-based overload resolution for F# 4.0 compatibility by Gustavo Leon.

# v1.1.0

* Adds overloads for binding F# `Async`. This means `let!` works with `Async` as well as `Task` and other awaitables.

# v1.0.1

* Fixes bug in which the `finally` blocks or disposable of `use` bindings would not run if the wrapped code ended in
  `return!`.

# v1.0.0

* First version on NuGet.