open FSharp.Control.Tasks
open System.Diagnostics
open System.Threading.Tasks
open System.IO

let bufferSize = 128

let writeFile path =
    task {
        let junk = Array.zeroCreate bufferSize
        use file = File.Create(path)
        for i = 1 to 10000 do
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
        for i = 1 to 10 do
            do! writeFile tmp
            do! readFile tmp
        sw.Stop()
        printfn "Completed in %d ms" sw.ElapsedMilliseconds
        File.Delete(tmp)
    }

[<EntryPoint>]
let main argv = 
    bench().Wait()
    0 // return an integer exit code
