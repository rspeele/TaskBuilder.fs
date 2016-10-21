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
    [<Struct>]
    type Step<'a, 'm> =
        /// If this task has produced a return value, this is that value, and the `Continuation`
        /// property will be null. Idiomatic F# would use a discriminated union but we want to
        /// avoid unnecessary allocations.
        val public ImmediateValue : 'a
        /// If non-null, an object implementing the next step in the task.
        val public Continuation : StepContinuation<'a, 'm>
        new(immediate, continuation) = { ImmediateValue = immediate; Continuation = continuation }
        /// Create a step from an immediately available return value.
        static member OfImmediate(immediate) = Step(immediate, Unchecked.defaultof<_>)
        /// Create a step from a continuation.
        static member OfContinuation(con) = Step(Unchecked.defaultof<_>, con)
    and
        /// Encapsulates the pairing of an awaitable and continuation that should execute
        /// when the awaitable has completed in order to reach the next step in the computation.
        [<AllowNullLiteral>]
        StepContinuation<'a, 'm> =
            /// The task or whatever to await. Unfortunately this incurs boxing overhead for
            /// framework awaitables which tend to be structs. On the plus side it won't happen
            /// if they've immediately completed since that'll be a Step.OfImmediate.
            val public Awaitable : INotifyCompletion
            /// The delayed continuation which proceeds to the next step.
            /// Must not be called until the awaitable has finished.
            val public NextStep : unit -> Step<'a, 'm>
            new(await, nextStep) = { Awaitable = await; NextStep = nextStep }
    /// Implements the machinery of running a `Step<'m, 'm>` as a `Task<'m>`.
    and StepStateMachine<'m>(continuation : StepContinuation<'m, 'm>) =
        let mutable methodBuilder = AsyncTaskMethodBuilder<'m>()
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
                true // We need to await the first continuation so that MoveNext() will be called at the right time.
            else
                try
                    let step = continuation.NextStep()
                    if isNull step.Continuation then
                        methodBuilder.SetResult(step.ImmediateValue)
                        false
                    else
                        continuation <- step.Continuation
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
                let mutable awaiter = continuation.Awaitable
                // Tell the builder to call us again when this thing is done.
                methodBuilder.AwaitOnCompleted(&awaiter, &this)
               
        interface IAsyncStateMachine with
            member this.MoveNext() = this.MoveNext()
            member this.SetStateMachine(_) = () // Doesn't really apply since we're a reference type.

    /// Used to represent no-ops like the implicit empty "else" branch of an "if" expression.
    /// Notice that this doesn't impose any constraints on the return type of the task block.
    let zero() = Step<unit, _>.OfImmediate(())

    /// Used to return a value. Notice that the result type of this step must be the same as the
    /// result type of the entire method.
    let ret (x : 'a) = Step<'a, 'a>.OfImmediate(x)

    // The following flavors of `bind` are for sequencing tasks with the continuations
    // that should run following them. They all follow pretty much the same formula.

    let bindTask (task : 'a Task) (continuation : 'a -> Step<'b, 'm>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then // Proceed to the next step based on the result we already have.
            taskAwaiter.GetResult() |> continuation
        else // Await and continue later when a result is available.
            StepContinuation
                ( taskAwaiter
                , (fun () -> taskAwaiter.GetResult() |> continuation)
                ) |> Step<'b, 'm>.OfContinuation

    let bindVoidTask (task : Task) (continuation : unit -> Step<'b, 'm>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then continuation() else
        StepContinuation
            ( taskAwaiter
            , continuation
            ) |> Step<'b, 'm>.OfContinuation

    let bindConfiguredTask (task : 'a ConfiguredTaskAwaitable) (continuation : 'a -> Step<'b, 'm>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then
            taskAwaiter.GetResult() |> continuation
        else
            StepContinuation
                ( taskAwaiter
                , (fun () -> taskAwaiter.GetResult() |> continuation)
                ) |> Step<'b, 'm>.OfContinuation

    let bindVoidConfiguredTask (task : ConfiguredTaskAwaitable) (continuation : unit -> Step<'b, 'm>) =
        let taskAwaiter = task.GetAwaiter()
        if taskAwaiter.IsCompleted then continuation() else
        StepContinuation
            ( taskAwaiter
            , continuation
            ) |> Step<'b, 'm>.OfContinuation

    let inline
        bindGenericAwaitable< ^a, ^b, ^c, ^m when ^a : (member GetAwaiter : unit -> ^b) and ^b :> INotifyCompletion >
        (awt : ^a) (continuation : unit -> Step< ^c, ^m >) =
        let taskAwaiter = (^a : (member GetAwaiter : unit -> ^b)(awt))
        StepContinuation
            ( taskAwaiter
            , continuation
            ) |> Step< ^c, ^m >.OfContinuation

    /// Chains together a step with its following step.
    /// Note that this requires that the first step has no result.
    /// This prevents constructs like `task { return 1; return 2; }`.
    let rec combine (step : Step<unit, 'm>) (continuation : unit -> Step<'b, 'm>) =
        let stepContinuation = step.Continuation
        if isNull stepContinuation then // The preceding step is just a value, so we can ignore it.
            continuation()
        else
            let stepNext = stepContinuation.NextStep
            StepContinuation
                ( // We can reuse the awaitable.
                  stepContinuation.Awaitable
                  // ... but we need to glue our continuation onto the next step of the one we're wrapping.
                , fun () -> combine (stepNext()) continuation
                ) |> Step<'b, 'm>.OfContinuation

    /// Builds a step that executes the body while the condition predicate is true.
    let inline whileLoop (cond : unit -> bool) (body : unit -> Step<unit, 'm>) =
        if cond() then
            // Create a self-referencing closure to test whether to repeat the loop on future iterations.
            let rec repeat () =
                if cond() then combine (body()) repeat
                else zero()
            // Run the body the first time and chain it to the repeat logic.
            combine (body()) repeat
        else zero()

    /// The recursive part of a try/with statement. Nothing fancy here, just chains the try/with back
    /// onto the continuation it's given.
    let rec tryWithCore (stepContinuation : StepContinuation<'a, 'm>) (catch : exn -> Step<'a, 'm>) =
        let stepNext = stepContinuation.NextStep
        StepContinuation
            ( stepContinuation.Awaitable
            , fun () -> tryWithNonInline stepNext catch
            ) |> Step<'a, 'm>.OfContinuation

    /// Wraps a step in a try/with. This catches exceptions both in the evaluation of the function
    /// to retrieve the step, and in the continuation of the step (if any).
    and inline tryWith (step : unit -> Step<'a, 'm>) (catch : exn -> Step<'a, 'm>) =
        try
            let step = step()
            if isNull step.Continuation then
                step
            else
                tryWithCore step.Continuation catch
        with
        | exn -> catch exn

    /// Necessary to be able to use recursively in tryWithCore.
    and tryWithNonInline step catch = tryWith step catch

    /// Similar to tryWithCore, this functions only job is to chain the tryFinally onto
    /// a continuation.
    let rec tryFinallyCore (stepContinuation : StepContinuation<'a, 'm>) (fin : unit -> unit) =
        let stepNext = stepContinuation.NextStep
        StepContinuation
            ( stepContinuation.Awaitable
            , fun () -> tryFinallyNonInline stepNext fin
            ) |> Step<'a, 'm>.OfContinuation

    /// Wraps a step in a try/finally. This catches exceptions both in the evaluation of the function
    /// to retrieve the step, and in the continuation of the step (if any).
    and inline tryFinally (step : unit -> Step<'a, 'm>) fin =
        let step =
            try step()
            // Important point: we use a try/with, not a try/finally, to implement tryFinally.
            // The reason for this is that if we're just building a continuation, we definitely *shouldn't*
            // execute the `fin()` part yet -- the actual execution of the asynchronous code hasn't completed!
            with
            | _ ->
                fin()
                reraise()
        if isNull step.Continuation then
            // We're at no risk of running fin() twice here since where it's run above, it's followed by reraise().
            fin() 
            step
        else
            // Have to wrap the continuation with the same finally block (which hasn't run yet).
            tryFinallyCore step.Continuation fin

    /// Necessary to be able to use recursively in tryFinallyCore.
    and tryFinallyNonInline step fin = tryFinally step fin

    /// Implements a using statement that disposes `disp` after `body` has completed.
    let inline using (disp : #IDisposable) (body : _ -> Step<'a, 'm>) =
        // A using statement is just a try/finally with the finally block disposing if non-null.
        tryFinally
            (fun () -> body disp)
            (fun () -> if not (isNull (box disp)) then disp.Dispose())

    /// Implements a loop that runs `body` for each element in `sequence`.
    let forLoop (sequence : 'a seq) (body : 'a -> Step<unit, 'm>) =
        // A for loop is just a using statement on the sequence's enumerator...
        using (sequence.GetEnumerator())
            // ... and its body is a while loop that advances the enumerator and runs the body on each element.
            (fun e -> whileLoop e.MoveNext (fun () -> body e.Current))

    /// Runs a step as a task -- with a short-circuit for immediately completed steps.
    let inline run (firstStep : unit -> Step<'m, 'm>) =
        try
            let step = firstStep()
            if isNull step.Continuation then
                Task.FromResult(step.ImmediateValue)
            else
                StepStateMachine<'m>(step.Continuation).Run()
        // Any exceptions should go on the task, rather than being thrown from this call.
        // This matches C# behavior where you won't see an exception until awaiting the task,
        // even if it failed before reaching the first "await".
        with
        | exn -> Task.FromException<_>(exn)

    type TaskBuilder() =
        // These methods are consistent between the two builders.
        // Unfortunately, inline members do not work with inheritance.
        member inline __.Delay(f : unit -> Step<_, _>) = f
        member inline __.Run(f : unit -> Step<'m, 'm>) = run f
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
        member inline __.Delay(f : unit -> Step<_, _>) = f
        member inline __.Run(f : unit -> Step<'m, 'm>) = run f
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

