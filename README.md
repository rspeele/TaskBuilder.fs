[![Build Status](https://travis-ci.org/rspeele/TaskBuilder.fs.svg?branch=master)](https://travis-ci.org/rspeele/TaskBuilder.fs)

## About

This is a single-file project that implements a
[computation expression](https://docs.microsoft.com/en-us/dotnet/articles/fsharp/language-reference/computation-expressions)
for writing `Task`s in F#.
It is free and unencumbered software released into the public domain.

F# comes with its own `Async` type and functions to convert back and
forth between `Async` and `Task`, but this is a bit of a hassle --
especially since now that `Task` has language-level support in C# and
VB.NET, it's the de facto standard for asynchrony on .NET.
Additionally, F#'s `Async` behaves a little differently from `Task`,
which can be confusing if you're used to the latter.

The goal of this computation expression builder is to let you write
asynchronous blocks that behave just like `async` methods in C# do.

For example, this F# method:

```fsharp
open System
open System.IO
open System.Linq
open FSharp.Control.Tasks

type X() =
  static member WriteFile() =
    task {
      do! unitTask <| Console.Out.WriteLineAsync("Enter a filename:")
      let! name = Console.In.ReadLineAsync()
      use file = File.CreateText(name)
      for i in Enumerable.Range(0, 100) do
        do! unitTask <| file.WriteLineAsync(String.Format("hello {0}", i))
      do! unitTask <| Console.Out.WriteLineAsync("Done")
      return name
    }
```

Should work exactly the same as this C# method:

```csharp
using System
using System.IO
using System.Linq
using System.Threading.Tasks

class X
{
  public static async Task<string> WriteFile()
  {
    await Console.Out.WriteLineAsync("Enter a filename:");
    var name = await Console.In.ReadLineAsync();
    using (var file = File.CreateText(name))
    {
      foreach (var i in Enumerable.Range(0, 100))
      {
        await file.WriteLineAsync(String.Format("hello {0}", i));
      }
      await Console.Out.WriteLineAsync("Done");
      return name;
    }
  }
}
```

In practice there is a small performance hit compared to the C#
version, because the C# compiler compiles each `async` method to a
specialized state machine class, while `TaskBuilder` uses a
general-purpose state machine and must chain together continuation
functions to represent the computation. However, `TaskBuilder` should
still be faster than using `Task.ContinueWith` or `Async.StartAsTask`.

## Usage

This is public domain code. I encourage you to simply copy
TaskBuilder.fs into your own project and use it as you see fit. It is
not necessary to credit me or include any legal notice with your copy
of the code.

The other files are tests which you do not need to copy (but again,
you are free to do so).

Note that by default, if you open `FSharp.Control.Tasks`, you'll get
a `task { ... }` builder that behaves as closely to C#'s async methods as possible.

However, I have also included a version of the `task { ... }` builder under
`FSharp.Control.Tasks.ContextInsensitive` which makes one minor change: it will
automatically call `task.ConfigureAwait(false)` on every task you await.

This can improve performance if you're writing library code or server-side code
and don't need to interact with thread-unsafe things like Windows forms controls.
If you're not sure whether you want to use this version of the builder,
reading [this MSDN article](https://msdn.microsoft.com/en-us/magazine/jj991977.aspx)
may help.