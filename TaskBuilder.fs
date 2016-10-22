// TaskBuilder.fs - TPL task computation expressions for F#
//
// Written in 2016 by Robert Peele (humbobst@gmail.com)
//
// To the extent possible under law, the author(s) have dedicated all copyright and related and neighboring rights
// to this software to the public domain worldwide. This software is distributed without any warranty.
//
// You should have received a copy of the CC0 Public Domain Dedication along with this software.
// If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.

namespace FSharp.Control.Tasks
open System
open System.Threading.Tasks
open System.Runtime.CompilerServices

// This module is not really obsolete, but it's not intended to be referenced directly from user code.
// However, it can't be private because it is used within inline functions that *are* user-visible.
// Marking it as obsolete is a workaround to hide it from auto-completion tools.
[<Obsolete>]
module TaskBuilder =
    /// Represents the state of a computation:
    /// either awaiting something with a continuation,
    /// or completed with a return value.
    /// The 'a generic parameter is the result type of this step, whereas the 'm generic parameter
    /// is the result type of the entire `task` block it occurs in.
    type Step<'a> =
        | Immediate of 'a
        | Continuation of INotifyCompletion * (unit -> Step<'a>)
    /// Implements the machinery of running a `Step<'m, 'm>` as a `Task<'m>`.
    and StepStateMachine<'a>(awaiter, continuation : unit -> Step<'a>) =
        let mutable methodBuilder = AsyncTaskMethodBuilder<'a>()
        /// The thing we're awaiting.
        let mutable awaiter = awaiter
        /// The continuation we left off awaiting on our last MoveNext().
        let mutable continuation = continuation
        /// If true, this is our first MoveNext(), and should await the first
        /// continuation instead of proceeding to its next step.
        let mutable initial = true

        /// Start execution as a `Task<'m>`.
        member this.Run() =
            let mutable this = this
            methodBuilder.Start(&this)
            methodBuilder.Task

        /// Return true if we should call `AwaitOnCompleted` on the current awaitable.
        member inline private __.ShouldAwait() =
            if initial then
                initial <- false
                true // We need to await the first so that MoveNext() will be called at the right time.
            else
                try
                    match continuation() with
                    | Immediate r ->
                        methodBuilder.SetResult(r)
                        false
                    | Continuation (await, next) ->
                        continuation <- next
                        awaiter <- await
                        true
                with
                | exn ->
                    methodBuilder.SetException(exn)
                    false

        /// Proceed to one of three states: result, failure, or awaiting.
        /// If awaiting, MoveNext() will be called again when the awaitable completes.
        member this.MoveNext() =
            if this.ShouldAwait() then
                let mutable this = this
                // Tell the builder to call us again when this thing is done.
                methodBuilder.AwaitOnCompleted(&awaiter, &this)
               
        interface IAsyncStateMachine with
            member this.MoveNext() = this.MoveNext()
            member this.SetStateMachine(_) = () // Doesn't really apply since we're a reference type.

    /// Used to represent no-ops like the implicit empty "else" branch of an "if" expression.
    /// Notice that this doesn't impose any constraints on the return type of the task block.
    let inline zero() = Immediate ()

    /// Used to return a value. Notice that the result type of this step must be the same as the
    /// result type of the entire method.
    let inline ret (x : 'a) = Immediate x

    // The following flavors of `bind` are for sequencing tasks with the continuations
    // that should run following them. They all follow pretty much the same formula.

    let inline bindTask (task : 'a Task) (continuation : 'a -> Step<'b>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then // Proceed to the next step based on the result we already have.
            taskAwaiter.GetResult() |> continuation
        else // Await and continue later when a result is available.
            Continuation
                ( taskAwaiter
                , (fun () -> taskAwaiter.GetResult() |> continuation)
                )

    let inline bindVoidTask (task : Task) (continuation : unit -> Step<'b>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then continuation() else
        Continuation
            ( taskAwaiter
            , continuation
            )

    let inline bindConfiguredTask (task : 'a ConfiguredTaskAwaitable) (continuation : 'a -> Step<'b>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then
            taskAwaiter.GetResult() |> continuation
        else
            Continuation
                ( taskAwaiter
                , (fun () -> taskAwaiter.GetResult() |> continuation)
                )

    let inline bindVoidConfiguredTask (task : ConfiguredTaskAwaitable) (continuation : unit -> Step<'b>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then continuation() else
        Continuation
            ( taskAwaiter
            , continuation
            )

    let inline
        bindGenericAwaitable< ^a, ^b, ^c when ^a : (member GetAwaiter : unit -> ^b) and ^b :> INotifyCompletion >
        (awt : ^a) (continuation : unit -> Step< ^c >) =
        let taskAwaiter = (^a : (member GetAwaiter : unit -> ^b)(awt))
        Continuation
            ( taskAwaiter
            , continuation
            )

    /// Chains together a step with its following step.
    /// Note that this requires that the first step has no result.
    /// This prevents constructs like `task { return 1; return 2; }`.
    let rec combine (step : Step<unit>) (continuation : unit -> Step<'b>) =
        match step with
        | Immediate _ -> continuation()
        | Continuation (awaitable, next) ->
            Continuation
                ( awaitable
                , fun () -> combine (next()) continuation
                )

    /// Builds a step that executes the body while the condition predicate is true.
    let inline whileLoop (cond : unit -> bool) (body : unit -> Step<unit>) =
        if cond() then
            // Create a self-referencing closure to test whether to repeat the loop on future iterations.
            let rec repeat () =
                if cond() then combine (body()) repeat
                else zero()
            // Run the body the first time and chain it to the repeat logic.
            combine (body()) repeat
        else zero()

    /// Wraps a step in a try/with. This catches exceptions both in the evaluation of the function
    /// to retrieve the step, and in the continuation of the step (if any).
    let rec tryWith(step : unit -> Step<'a>) (catch : exn -> Step<'a>) =
        try
            match step() with
            | Immediate _ as i -> i
            | Continuation (awaitable, next) -> Continuation (awaitable, fun () -> tryWith next catch)
        with
        | exn -> catch exn

    /// Wraps a step in a try/finally. This catches exceptions both in the evaluation of the function
    /// to retrieve the step, and in the continuation of the step (if any).
    let rec tryFinally (step : unit -> Step<'a>) fin =
        let step =
            try step()
            // Important point: we use a try/with, not a try/finally, to implement tryFinally.
            // The reason for this is that if we're just building a continuation, we definitely *shouldn't*
            // execute the `fin()` part yet -- the actual execution of the asynchronous code hasn't completed!
            with
            | _ ->
                fin()
                reraise()
        match step with
        | Immediate _ as i ->
            fin()
            i
        | Continuation (awaitable, next) ->
            Continuation (awaitable, fun () -> tryFinally next fin)

    /// Implements a using statement that disposes `disp` after `body` has completed.
    let inline using (disp : #IDisposable) (body : _ -> Step<'a>) =
        // A using statement is just a try/finally with the finally block disposing if non-null.
        tryFinally
            (fun () -> body disp)
            (fun () -> if not (isNull (box disp)) then disp.Dispose())

    /// Implements a loop that runs `body` for each element in `sequence`.
    let forLoop (sequence : 'a seq) (body : 'a -> Step<unit>) =
        // A for loop is just a using statement on the sequence's enumerator...
        using (sequence.GetEnumerator())
            // ... and its body is a while loop that advances the enumerator and runs the body on each element.
            (fun e -> whileLoop e.MoveNext (fun () -> body e.Current))

    /// Runs a step as a task -- with a short-circuit for immediately completed steps.
    let inline run (firstStep : unit -> Step<'a>) =
        try
            match firstStep() with
            | Immediate x -> Task.FromResult(x)
            | Continuation (awaitable, continuation) ->
                StepStateMachine<'a>(awaitable, continuation).Run()
        // Any exceptions should go on the task, rather than being thrown from this call.
        // This matches C# behavior where you won't see an exception until awaiting the task,
        // even if it failed before reaching the first "await".
        with
        | exn -> Task.FromException<_>(exn)

    type TaskBuilder() =
        // These methods are consistent between the two builders.
        // Unfortunately, inline members do not work with inheritance.
        member inline __.Delay(f : unit -> Step<_>) = f
        member inline __.Run(f : unit -> Step<'m>) = run f
        member inline __.Zero() = zero()
        member inline __.Return(x) = ret x
        member inline __.ReturnFrom(task) = bindConfiguredTask task ret
        member inline __.ReturnFrom(task) = bindVoidConfiguredTask task ret
        member inline __.ReturnFrom(yld : YieldAwaitable) = bindGenericAwaitable yld ret
        member inline __.Combine(step, continuation) = combine step continuation
        member inline __.Bind(task, continuation) = bindConfiguredTask task continuation
        member inline __.Bind(task, continuation) = bindVoidConfiguredTask task continuation
        member inline __.Bind(yld : YieldAwaitable, continuation) = bindGenericAwaitable yld continuation
        member inline __.While(condition, body) = whileLoop condition body
        member inline __.For(sequence, body) = forLoop sequence body
        member inline __.TryWith(body, catch) = tryWith body catch
        member inline __.TryFinally(body, fin) = tryFinally body fin
        member inline __.Using(disp, body) = using disp body
        // End of consistent methods -- the following methods are different between
        // `TaskBuilder` and `ContextInsensitiveTaskBuilder`!

        member inline __.ReturnFrom(task : _ Task) =
            bindTask task ret
        member inline __.ReturnFrom(task : Task) =
            bindVoidTask task ret
        member inline __.Bind(task : _ Task, continuation) =
            bindTask task continuation
        member inline __.Bind(task : Task, continuation) =
            bindVoidTask task continuation

    type ContextInsensitiveTaskBuilder() =
        // These methods are consistent between the two builders.
        // Unfortunately, inline members do not work with inheritance.
        member inline __.Delay(f : unit -> Step<_>) = f
        member inline __.Run(f : unit -> Step<'m>) = run f
        member inline __.Zero() = zero()
        member inline __.Return(x) = ret x
        member inline __.ReturnFrom(task) = bindConfiguredTask task ret
        member inline __.ReturnFrom(task) = bindVoidConfiguredTask task ret
        member inline __.ReturnFrom(yld : YieldAwaitable) = bindGenericAwaitable yld ret
        member inline __.Combine(step, continuation) = combine step continuation
        member inline __.Bind(task, continuation) = bindConfiguredTask task continuation
        member inline __.Bind(task, continuation) = bindVoidConfiguredTask task continuation
        member inline __.Bind(yld : YieldAwaitable, continuation) = bindGenericAwaitable yld continuation
        member inline __.While(condition, body) = whileLoop condition body
        member inline __.For(sequence, body) = forLoop sequence body
        member inline __.TryWith(body, catch) = tryWith body catch
        member inline __.TryFinally(body, fin) = tryFinally body fin
        member inline __.Using(disp, body) = using disp body
        // End of consistent methods -- the following methods are different between
        // `TaskBuilder` and `ContextInsensitiveTaskBuilder`!

        member inline __.ReturnFrom(task : _ Task) =
            bindConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) ret
        member inline __.ReturnFrom(task : Task) =
            bindVoidConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) ret
        member inline __.Bind(task : _ Task, continuation) =
            bindConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) continuation
        member inline __.Bind(task : Task, continuation) =
            bindVoidConfiguredTask (task.ConfigureAwait(continueOnCapturedContext = false)) continuation

// Don't warn about our use of the "obsolete" module we just defined (see notes at start of file).
#nowarn "44"

[<AutoOpen>]
module ContextSensitive =
    /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method.
    /// Use this like `task { let! taskResult = someTask(); return taskResult.ToString(); }`.
    let task = TaskBuilder.TaskBuilder()

module ContextInsensitive =
    /// Builds a `System.Threading.Tasks.Task<'a>` similarly to a C# async/await method, but with
    /// all awaited tasks automatically configured *not* to resume on the captured context.
    /// This is often preferable when writing library code that is not context-aware, but undesirable when writing
    /// e.g. code that must interact with user interface controls on the same thread as its caller.
    let task = TaskBuilder.ContextInsensitiveTaskBuilder()

