open System
open System.Diagnostics
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.IO
open TaskBuilder

let require x msg = if not x then failwith msg

let testDelay() =
    let mutable x = 0
    let t =
        task {
            do! Task.Delay(5)
            x <- x + 1
        }
    require (x = 0) "task already ran"
    t.Wait()

let testNoDelay() =
    let mutable x = 0
    let t =
        task {
            x <- x + 1
            do! Task.Delay(5)
            x <- x + 1
        }
    require (x = 1) "first part didn't run yet"
    t.Wait()

let testNonBlocking() =
    let sw = Stopwatch()
    sw.Start()
    let t =
        task {
            do! Task.Yield()
            Thread.Sleep(100)
        }
    sw.Stop()
    require (sw.ElapsedMilliseconds < 50L) "sleep blocked caller"
    t.Wait()

exception Test of string

let failtest str = raise (Test str)

let testCatching1() =
    let mutable x = 0
    let mutable y = 0
    let t =
        task {
            try
                do! Task.Delay(0)
                failtest "hello"
                x <- 1
                do! Task.Delay(100)
            with
            | Test msg ->
                require (msg = "hello") "message tampered"
            | _ ->
                require false "other exn type"
            y <- 1
        }
    t.Wait()
    require (y = 1) "bailed after exn"
    require (x = 0) "ran past failure"

let testCatching2() =
    let mutable x = 0
    let mutable y = 0
    let t =
        task {
            try
                do! Task.Yield() // can't skip through this
                failtest "hello"
                x <- 1
                do! Task.Delay(100)
            with
            | Test msg ->
                require (msg = "hello") "message tampered"
            | _ ->
                require false "other exn type"
            y <- 1
        }
    t.Wait()
    require (y = 1) "bailed after exn"
    require (x = 0) "ran past failure"

let testNestedCatching() =
    let mutable counter = 1
    let mutable caughtInner = 0
    let mutable caughtOuter = 0
    let t1() =
        task {
            try
                do! Task.Yield()
                failtest "hello"
            with
            | Test msg as exn ->
                caughtInner <- counter
                counter <- counter + 1
                raise exn
        }
    let t2 =
        task {
            try
                do! t1()
            with
            | Test msg as exn ->
                caughtOuter <- counter
                raise exn
            | _ ->
                require false "invalid msg type"
        }
    try
        t2.Wait()
        require false "ran past failed task wait"
    with
    | :? AggregateException as exn ->
        require (exn.InnerExceptions.Count = 1) "more than 1 exn"
    require (caughtInner = 1) "didn't catch inner"
    require (caughtOuter = 2) "didn't catch outer"

[<EntryPoint>]
let main argv =
    testDelay()
    testNoDelay()
    testNonBlocking()
    testCatching1()
    testCatching2()
    testNestedCatching()
    0
