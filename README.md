## About

This is a single-file project that implements a
[computation expression](https://docs.microsoft.com/en-us/dotnet/articles/fsharp/language-reference/computation-expressions)
for writing `Task`s in F#.

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
type X() =
  static member WriteFile() =
    task {
      do! Console.Out.WriteLineAsync("Enter a filename:")
      let! name = Console.In.ReadLineAsync()
      use file = File.CreateText(name)
      for i in Enumerable.Range(0, 100) do
        do! file.WriteLineAsync(String.Format("hello {0}", i))
      do! Console.Out.WriteLineAsync("Done")
      return name
    }
```

Should work exactly the same as this C# method:

```csharp
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
    }
    await Console.Out.WriteLineAsync("Done");
    return name;
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