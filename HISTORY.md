# v1.1.0

* Adds overloads for binding F# `Async`. This means `let!` works with `Async` as well as `Task` and other awaitables.

# v1.0.1

* Fixes bug in which the `finally` blocks or disposable of `use` bindings would not run if the wrapped code ended in
  `return!`.

# v1.0.0

* First version on NuGet.