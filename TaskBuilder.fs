// Written in 2016 by Robert Peele (https://github.com/rspeele)

// This is free and unencumbered software released into the public domain.
//
// Anyone is free to copy, modify, publish, use, compile, sell, or
// distribute this software, either in source code form or as a compiled
// binary, for any purpose, commercial or non-commercial, and by any
// means.
// 
// In jurisdictions that recognize copyright laws, the author or authors
// of this software dedicate any and all copyright interest in the
// software to the public domain. We make this dedication for the benefit
// of the public at large and to the detriment of our heirs and
// successors. We intend this dedication to be an overt act of
// relinquishment in perpetuity of all present and future rights to this
// software under copyright law.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
// OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
// 
// For more information, please refer to <http://unlicense.org/>

module TaskBuilder
open System
open System.Threading.Tasks
open System.Runtime.CompilerServices

type Step<'a, 'm> =
    struct
        val mutable public ImmediateValue : 'a
        val mutable public Continuation : unit -> StepAwaiter<'a, 'm>
        new(immediate, continuation) = { ImmediateValue = immediate; Continuation = continuation }
        static member OfImmediate(immediate) = Step(immediate, Unchecked.defaultof<_>)
        static member OfContinuation(con) = Step(Unchecked.defaultof<_>, con)
    end
and StepAwaiter<'a, 'm> =
    struct
        val mutable public Await : StepStateMachine<'m> -> bool
        val mutable public NextStep : unit -> Step<'a, 'm>
        new(await, nextStep) = { Await = await; NextStep = nextStep }
    end
and StepStateMachine<'m>(step : Step<'m, 'm>) =
    let mutable methodBuilder = AsyncTaskMethodBuilder<'m>()
    let mutable step = step
    let mutable nextStep = Unchecked.defaultof<_>
    let mutable awaiting = false
    let mutable faulted = false
    member this.Run() =
        let mutable this = this
        methodBuilder.Start(&this)
        methodBuilder.Task

    member this.Await(awaitable : 'await when 'await :> INotifyCompletion) =
        awaiting <- true
        let mutable this = this
        let mutable awaiter = awaitable
        methodBuilder.AwaitOnCompleted(&awaiter, &this)
        false
    member this.MoveNext() =
        if awaiting then
            awaiting <- false
            step <-
                try nextStep() with
                | exn -> 
                    methodBuilder.SetException(exn)
                    faulted <- true
                    Step<'m, 'm>.OfImmediate(Unchecked.defaultof<_>)
        if faulted then
            ()
        elif isNull (box step.Continuation) then
            methodBuilder.SetResult(step.ImmediateValue)
        else
            let moveNext =
                try
                    let stepAwaiter = step.Continuation()
                    awaiting <- true
                    nextStep <- stepAwaiter.NextStep
                    stepAwaiter.Await(this)
                with
                | exn ->
                    methodBuilder.SetException(exn)
                    faulted <- true
                    false
            if moveNext then
                this.MoveNext()
    interface IAsyncStateMachine with
        member this.MoveNext() = this.MoveNext()
        member this.SetStateMachine(_) = ()

module Step =
    let zero() = Step<unit, _>.OfImmediate(())

    let ret (x : 'a) = Step<'a, 'a>.OfImmediate(x)

    let bindTask (task : 'a Task) (continuation : 'a -> Step<'b, 'm>) =
        Step<'b, 'm>.OfContinuation(fun () ->
            let taskAwaiter = task.GetAwaiter()
            StepAwaiter
                ( (fun state -> if taskAwaiter.IsCompleted then true else state.Await(taskAwaiter))
                , (fun () -> continuation <| taskAwaiter.GetResult())
                ))

    let bindVoidTask (task : Task) (continuation : unit -> Step<'b, 'm>) =
        Step<'b, 'm>.OfContinuation(fun () ->
            let taskAwaiter = task.GetAwaiter()
            StepAwaiter
                ( (fun state -> if taskAwaiter.IsCompleted then true else state.Await(taskAwaiter))
                , (fun () -> continuation())
                ))

    let inline bindGenericAwaitable< ^a, ^b, ^c, ^m when ^a : (member GetAwaiter : unit -> ^b) and ^b :> INotifyCompletion >
        (awt : ^a) (continuation : unit -> Step< ^c, ^m >) =
        Step< ^c, ^m >.OfContinuation(fun () ->
            let taskAwaiter = (^a : (member GetAwaiter : unit -> ^b)(awt))
            StepAwaiter
                ( (fun state -> state.Await(taskAwaiter))
                , (fun () -> continuation())
                ))

    let rec combine (step : Step<'a, 'm>) (continuation : unit -> Step<'b, 'm>) =
        if isNull (box step.Continuation) then
            continuation()
        else
            Step<'b, 'm>.OfContinuation(fun () ->
                let awaiter = step.Continuation()
                let innerNext = awaiter.NextStep
                StepAwaiter(awaiter.Await, fun () -> combine (innerNext()) continuation))

    let rec whileLoop (cond : unit -> bool) (body : unit -> Step<unit, 'm>) =
        if cond() then combine (body()) (fun () -> whileLoop cond body)
        else zero()

    let rec tryWithCore (step : Step<'a, 'm>) (catch : exn -> Step<'a, 'm>) =
        if isNull (box step.Continuation) then
            step
        else
            try
                let awaiter = step.Continuation()
                Step<'a, 'm>.OfContinuation(fun () ->
                    let innerNext = awaiter.NextStep
                    StepAwaiter(awaiter.Await, fun () ->
                        try
                            tryWithCore (innerNext()) catch
                        with
                        | exn -> catch exn))
            with
            | exn -> catch exn

    let inline tryWith step catch =
        try
            tryWithCore (step()) catch
        with
        | exn -> catch exn

    let rec tryFinallyCore (step : Step<'a, 'm>) (fin : unit -> unit) =
        if isNull (box step.Continuation) then
            fin()
            step
        else
            try
                let awaiter = step.Continuation()
                Step<'a, 'm>.OfContinuation(fun () ->
                    let innerNext = awaiter.NextStep
                    StepAwaiter(awaiter.Await, fun () ->
                        try
                            tryFinallyCore (innerNext()) fin
                        with
                        | _ ->
                            fin()
                            reraise()))
            with
            | _ ->
                fin()
                reraise()

    let inline tryFinally step fin =
        try
            tryFinallyCore (step()) fin
        with
        | _ ->
            fin()
            reraise()

    let inline using (disp : #IDisposable) (body : _ -> Step<'a, 'm>) =
        tryFinally (fun () -> body disp) (fun () -> disp.Dispose())

    let forLoop (sequence : 'a seq) (body : 'a -> Step<unit, 'm>) =
        using (sequence.GetEnumerator())
            (fun e -> whileLoop e.MoveNext (fun () -> body e.Current))

    let run (step : Step<'m, 'm>) =
        if isNull (box step.Continuation) then
            Task.FromResult(step.ImmediateValue)
        else
            let state = StepStateMachine<'m>(step)
            state.Run()

open Step

type Await<'x>(value : 'x) =
    struct
        member this.Value = value
    end

/// Await a generic awaitable (with no result value).
let await x = Await(x)

type TaskBuilder() =
    member inline __.Delay(f : unit -> Step<_, _>) = f
    member inline __.Run(f : unit -> Step<'m, 'm>) = run (f())

    member inline __.Zero() = zero()
    member inline __.Return(x) = ret x
    member inline __.ReturnFrom(task) = bindTask task ret
    member inline __.ReturnFrom(task) = bindVoidTask task ret
    member inline __.ReturnFrom(yld : YieldAwaitable) = bindGenericAwaitable yld ret
    member inline __.ReturnFrom(awt : _ Await) = bindGenericAwaitable awt.Value ret
    member inline __.Combine(step, continuation) = combine step continuation
    member inline __.Bind(task, continuation) = bindTask task continuation
    member inline __.Bind(task, continuation) = bindVoidTask task continuation
    member inline __.Bind(yld : YieldAwaitable, continuation) = bindGenericAwaitable yld continuation
    member inline __.ReturnFrom(awt : _ Await, continuation) = bindGenericAwaitable awt.Value continuation
    member inline __.While(condition, body) = whileLoop condition body
    member inline __.For(sequence, body) = forLoop sequence body
    member inline __.TryWith(body, catch) = tryWith body catch
    member inline __.TryFinally(body, fin) = tryFinally body fin
    member inline __.Using(disp, body) = using disp body

let task = TaskBuilder()