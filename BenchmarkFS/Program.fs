open FSharp.Control.Tasks
open System.Diagnostics
open System.Threading.Tasks
open System.IO

[<Literal>]
let bufferSize = 128
[<Literal>]
let writeIterations = 10000
[<Literal>]
let executionIterations = 10

module TaskBuilderVersion =
    let writeFile path =
        task {
            let junk = Array.zeroCreate bufferSize
            use file = File.Create(path)
            for i = 1 to writeIterations do
                do! file.WriteAsync(junk, 0, junk.Length)
        }

    let readFile path =
        task {
            let buffer = Array.zeroCreate bufferSize
            use file = File.OpenRead(path)
            let mutable reading = true
            while reading do
                let! countRead = file.ReadAsync(buffer, 0, buffer.Length)
                reading <- countRead > 0
        }

    let bench() =
        let tmp = "tmp"
        task {
            let sw = Stopwatch()
            sw.Start()
            for i = 1 to executionIterations do
                do! writeFile tmp
                do! readFile tmp
            sw.Stop()
            printfn "TaskBuilder completed in %d ms" sw.ElapsedMilliseconds
            File.Delete(tmp)
        }

module FSharpAsyncVersion =
    let writeFile path =
        async {
            let junk = Array.zeroCreate bufferSize
            use file = File.Create(path)
            for i = 1 to writeIterations do
                do! file.AsyncWrite(junk, 0, junk.Length)
        }

    let readFile path =
        async {
            let buffer = Array.zeroCreate bufferSize
            use file = File.OpenRead(path)
            let mutable reading = true
            while reading do
                let! countRead = file.AsyncRead(buffer, 0, buffer.Length)
                reading <- countRead > 0
        }

    let bench() =
        let tmp = "tmp"
        async {
            let sw = Stopwatch()
            sw.Start()
            for i = 1 to executionIterations do
                do! writeFile tmp
                do! readFile tmp
            sw.Stop()
            printfn "F# async completed in %d ms" sw.ElapsedMilliseconds
            File.Delete(tmp)
        }

module FSharpAsyncAwaitTaskVersion =
    let writeFile path =
        async {
            let junk = Array.zeroCreate bufferSize
            use file = File.Create(path)
            for i = 1 to writeIterations do
                do! Async.AwaitTask(file.WriteAsync(junk, 0, junk.Length))
        }

    let readFile path =
        async {
            let buffer = Array.zeroCreate bufferSize
            use file = File.OpenRead(path)
            let mutable reading = true
            while reading do
                let! countRead = Async.AwaitTask(file.ReadAsync(buffer, 0, buffer.Length))
                reading <- countRead > 0
        }

    let bench() =
        let tmp = "tmp"
        async {
            let sw = Stopwatch()
            sw.Start()
            for i = 1 to executionIterations do
                do! writeFile tmp
                do! readFile tmp
            sw.Stop()
            printfn "F# async (AwaitTask) completed in %d ms" sw.ElapsedMilliseconds
            File.Delete(tmp)
        }

[<EntryPoint>]
let main argv = 
    while true do
        BenchmarkCS.CSharp.Bench().Wait()
        TaskBuilderVersion.bench().Wait()
        FSharpAsyncVersion.bench() |> Async.RunSynchronously
        FSharpAsyncAwaitTaskVersion.bench() |> Async.RunSynchronously
    0 // return an integer exit code
