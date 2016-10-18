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
        val mutable public Awaiter : TaskAwaiter
        val mutable public NextStep : unit -> Step<'a, 'm>
        new(awaiter, nextStep) = { Awaiter = awaiter; NextStep = nextStep }
    end
and StepStateMachine<'m>(step : Step<'m, 'm>) =
    let mutable methodBuilder = AsyncTaskMethodBuilder<'m>()
    let mutable step = step
    let mutable awaiter = Unchecked.defaultof<StepAwaiter<'m, 'm>>
    let mutable awaiting = false
    member this.Run() =
        let mutable this = this
        methodBuilder.Start(&this)
        methodBuilder.Task
    member this.MoveNext() =
        if awaiting then
            step <- awaiter.NextStep()
        if isNull (box step.Continuation) then
            methodBuilder.SetResult(step.ImmediateValue)
        else
            awaiter <- step.Continuation()
            if awaiter.Awaiter.IsCompleted then
                step <- awaiter.NextStep()
                this.MoveNext()
            else
                awaiting <- true
                let mutable this = this
                methodBuilder.AwaitOnCompleted(&awaiter.Awaiter, &this)
    interface IAsyncStateMachine with
        member this.MoveNext() = this.MoveNext()
        member this.SetStateMachine(_) = ()

module Step =
    let zero() = Step<unit, _>.OfImmediate(())

    let ret (x : 'a) = Step<'a, 'a>.OfImmediate(x)

    let bindTask (task : 'a Task) (continuation : 'a -> Step<'b, 'm>) =
        Step<'b, 'm>.OfContinuation(fun () ->
            let taskAwaiter = (task :> Task).GetAwaiter()
            StepAwaiter(taskAwaiter, fun () -> continuation <| task.GetAwaiter().GetResult()))

    let bindVoidTask (task : Task) (continuation : unit -> Step<'b, 'm>) =
        Step<'b, 'm>.OfContinuation(fun () ->
            StepAwaiter(task.GetAwaiter(), continuation))

    let rec combine (step : Step<'a, 'm>) (continuation : unit -> Step<'b, 'm>) =
        if isNull (box step.Continuation) then
            continuation()
        else
            Step<'b, 'm>.OfContinuation(fun () ->
                let awaiter = step.Continuation()
                let innerNext = awaiter.NextStep
                StepAwaiter(awaiter.Awaiter, fun () -> combine (innerNext()) continuation))

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
                    StepAwaiter(awaiter.Awaiter, fun () ->
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
                    StepAwaiter(awaiter.Awaiter, fun () ->
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

type TaskBuilder() =
    member inline __.Delay(f : unit -> Step<_, _>) = f
    member inline __.Run(f : unit -> Step<'m, 'm>) = run (f())

    member inline __.Zero() = zero()
    member inline __.Return(x) = ret x
    member inline __.ReturnFrom(task) = bindTask task ret
    member inline __.ReturnFrom(task) = bindVoidTask task ret
    member inline __.Combine(step, continuation) = combine step continuation
    member inline __.Bind(task, continuation) = bindTask task continuation
    member inline __.Bind(task, continuation) = bindVoidTask task continuation
    member inline __.While(condition, body) = whileLoop condition body
    member inline __.For(sequence, body) = forLoop sequence body
    member inline __.TryWith(body, catch) = tryWith body catch
    member inline __.TryFinally(body, fin) = tryFinally body fin
    member inline __.Using(disp, body) = using disp body

let task = TaskBuilder()