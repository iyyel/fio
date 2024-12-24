(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Core.DSL

open System.Threading
open System.Threading.Channels
open System.Collections.Concurrent

type internal Action =
    | RescheduleForRunning
    | RescheduleForBlocking of BlockingItem
    | Evaluated

and internal BlockingItem =
    | BlockingChannel of Channel<obj>
    | BlockingFiber of InternalFiber

and internal StackFrame =
    | SuccConts of succCont: (obj -> FIO<obj, obj>)
    | ErrorConts of errCont: (obj -> FIO<obj, obj>)

and internal Stack = StackFrame list

and internal WorkItem =
    { Effect: FIO<obj, obj>
      Stack: Stack
      IFiber: InternalFiber
      PrevAction: Action }

    static member Create effect stack ifiber prevAction =
        { Effect = effect
          Stack = stack
          IFiber = ifiber
          PrevAction = prevAction }

    member internal this.Complete result =
        this.IFiber.Complete result

and internal InternalQueue<'T> = BlockingCollection<'T>

and internal InternalFiber internal (
    resultQueue: InternalQueue<Result<obj, obj>>,
    blockingWorkItemQueue: InternalQueue<WorkItem>
    ) =

    // Use semaphore instead?
    member internal this.Complete result : unit =
        if resultQueue.Count = 0 then
            resultQueue.Add result
        else
            failwith "InternalFiber: Complete was called on an already completed InternalFiber!"

    member internal this.AwaitResult() : Result<obj, obj> =
        let result = resultQueue.Take()
        resultQueue.Add result
        result

    member internal this.Completed() : bool =
        resultQueue.Count > 0

    member internal this.AddBlockingWorkItem workItem : unit =
        blockingWorkItemQueue.Add workItem

    member internal this.BlockingWorkItemsCount() : int =
        blockingWorkItemQueue.Count

    member internal this.RescheduleBlockingWorkItems(workItemQueue: InternalQueue<WorkItem>) : unit =
        while blockingWorkItemQueue.Count > 0 do
            workItemQueue.Add <| blockingWorkItemQueue.Take()

/// A Fiber is a construct that represents a lightweight-thread.
/// Fibers are used to execute multiple effects in parallel and
/// can be awaited to retrieve the result of the effect.
and Fiber<'R, 'E> private (
    resultQueue: InternalQueue<Result<obj, obj>>,
    blockingWorkItemQueue: InternalQueue<WorkItem>
    ) =

    new() = Fiber(new InternalQueue<Result<obj, obj>>(), new InternalQueue<WorkItem>())

    member internal this.ToInternal() : InternalFiber =
        InternalFiber(resultQueue, blockingWorkItemQueue)

    member this.AwaitResult() : Result<'R, 'E> =
        let result = resultQueue.Take()
        resultQueue.Add result
        match result with
        | Ok result -> Ok (result :?> 'R)
        | Error error -> Error (error :?> 'E)

    /// Await waits for the result of the fiber and succeeds with it.
    member this.Await() : FIO<'R, 'E> =
        Await <| this.ToInternal()

/// A channel represents a communication queue that holds
/// data of the type 'D. Data can be both be sent and
/// retrieved (blocking) on a channel.
and Channel<'R> private (
    dataQueue: InternalQueue<obj>,
    blockingWorkItemQueue: InternalQueue<WorkItem>,
    dataCounter: int64 ref
    ) =

    new() = Channel(new InternalQueue<obj>(), new InternalQueue<WorkItem>(), ref 0)
    
    member internal this.AddBlockingWorkItem workItem : unit =
        blockingWorkItemQueue.Add workItem

    member internal this.RescheduleBlockingWorkItem(workItemQueue: InternalQueue<WorkItem>) : unit =
        if blockingWorkItemQueue.Count > 0 then
            workItemQueue.Add <| blockingWorkItemQueue.Take()

    member internal this.HasBlockingWorkItems() : bool =
        blockingWorkItemQueue.Count > 0

    member internal this.Upcast() : Channel<obj> =
        Channel<obj>(dataQueue, blockingWorkItemQueue, dataCounter)

    member internal this.UseAvailableData() =
        Interlocked.Decrement dataCounter |> ignore

    member internal this.DataAvailable() =
        let mutable temp = dataCounter.Value
        Interlocked.Read &temp > 0

    member this.Add(message: 'R) : unit =
        Interlocked.Increment dataCounter |> ignore
        dataQueue.Add message

    member this.Take() : 'R =
        dataQueue.Take() :?> 'R

    member this.Count() : int =
        dataQueue.Count

    /// Send puts the message on the given channel and succeeds with the message.
    member this.Send<'R, 'E> (message: 'R) : FIO<'R, 'E> =
        Send (message, this)

    /// Receive retrieves a message from the channel and succeeds with it.
    member this.Receive<'R, 'E>() : FIO<'R, 'E> =
        Blocking this

and channel<'R> = Channel<'R>

/// The FIO type models a functional effect that can either succeed
/// with a result or fail with an error when executed.
and FIO<'R, 'E> =
    internal
    | NonBlocking of action: (unit -> Result<'R, 'E>)
    | Blocking of channel: Channel<'R>
    | Send of message: 'R * channel: Channel<'R>
    | Concurrent of effect: FIO<obj, obj> * fiber: obj * ifiber: InternalFiber
    | Await of ifiber: InternalFiber
    | ChainSuccess of effect: FIO<obj, 'E> * continuation: (obj -> FIO<'R, 'E>)
    | ChainError of effect: FIO<obj, obj> * continuation: (obj -> FIO<'R, 'E>)
    | Success of result: 'R
    | Failure of error: 'E

    /// Fork executes an effect concurrently and returns the fiber that executes it.
    /// The fiber can be awaited for the result of the effect.
    member this.Fork<'R, 'E, 'E1>() : FIO<Fiber<'R, 'E>, 'E1> =
        let fiber = new Fiber<'R, 'E>()
        Concurrent (this.Upcast(), fiber, fiber.ToInternal())

    /// BindOnSuccess binds a continuation to the success result of an effect.
    /// If the effect fails, the error is immediately returned.
    member this.BindOnSuccess<'R, 'R1, 'E> (continuation: 'R -> FIO<'R1, 'E>) : FIO<'R1, 'E> =
        ChainSuccess (this.UpcastResult(), fun result -> continuation (result :?> 'R))

    /// BindOnError binds a continuation to the error result of an effect.
    /// If the effect succeeds, the result is immediately returned.
    member this.BindOnError<'R, 'E, 'E1> (continuation: 'E -> FIO<'R, 'E1>) : FIO<'R, 'E1> =
        ChainError (this.Upcast(), fun error -> continuation (error :?> 'E))

    /// Then sequences two effects, ignoring the result of the first effect.
    /// If the first effect fails, the error is immediately returned.
    member inline this.Then<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R1, 'E> =
        this.BindOnSuccess(fun _ -> other)

    /// ApplyWith combines two effects: one produces a function and the other produces a value.
    /// The function is applied to the value, and the result is returned.
    /// Errors are immediately returned if any effect fails.
    member inline this.ApplyWith<'R, 'R1, 'E> (other: FIO<'R -> 'R1, 'E>) : FIO<'R1, 'E> =
        other.BindOnSuccess(fun otherFunc ->
            this.BindOnSuccess(fun result ->
                succeed (otherFunc result)))

    /// InParallelWith executes two effects concurrently and succeeds with a tuple of their results when both complete.
    /// If either effect fails, the error is immediately returned.
    member inline this.InParallelWith<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
        other.Fork().BindOnSuccess(fun otherFiber ->
            this.BindOnSuccess(fun thisResult ->
                otherFiber.Await().BindOnSuccess(fun otherResult ->
                    succeed (thisResult, otherResult))))

    /// ZipWith combines two effects and succeeds with a tuple of their results when both complete.
    /// Errors are immediately returned if any effect fails.
    member inline this.ZipWith<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
        this.BindOnSuccess(fun thisResult ->
            other.BindOnSuccess(fun otherResult ->
                succeed (thisResult, otherResult)))

    /// RaceWith executes two effects concurrently and succeeds with the result of the first effect that completes.
    /// If both effects fail, the first error is returned.
    member this.RaceWith<'R, 'E> (other: FIO<'R, 'E>) : FIO<'R, 'E> =
        let rec loop (thisFiber: InternalFiber) (otherFiber: InternalFiber) =
            if thisFiber.Completed() then thisFiber
            else if otherFiber.Completed() then otherFiber
            else loop thisFiber otherFiber
        this.Fork().BindOnSuccess(fun thisFiber ->
            other.Fork().BindOnSuccess(fun otherFiber ->
                match (loop (thisFiber.ToInternal()) (otherFiber.ToInternal())).AwaitResult() with
                | Ok result -> Success (result :?> 'R)
                | Error error -> Failure (error :?> 'E)))

    member internal this.UpcastResult<'R, 'E>() : FIO<obj, 'E> =
        match this with
        | NonBlocking action ->
            NonBlocking <| fun () ->
            match action () with
            | Ok result -> Ok (result :> obj)
            | Error error -> Error error
        | Blocking channel ->
            Blocking <| channel.Upcast()
        | Send (message, channel) ->
            Send (message :> obj, channel.Upcast())
        | Concurrent (effect, fiber, ifiber) ->
            Concurrent (effect, fiber, ifiber)
        | Await ifiber ->
            Await ifiber
        | ChainSuccess (effect, continuation) ->
            ChainSuccess (effect, fun result -> (continuation result).UpcastResult())
        | ChainError (effect, continuation) ->
            ChainError (effect, fun result -> (continuation result).UpcastResult())
        | Success result ->
            Success (result :> obj)
        | Failure error ->
            Failure error

    member internal this.UpcastError<'R, 'E>() : FIO<'R, obj> =
        match this with
        | NonBlocking action ->
            NonBlocking <| fun () ->
            match action () with
            | Ok result -> Ok result
            | Error error -> Error (error :> obj)
        | Blocking channel ->
            Blocking channel
        | Send (message, channel) ->
            Send (message, channel)
        | Concurrent (effect, fiber, ifiber) ->
            Concurrent (effect, fiber, ifiber)
        | Await ifiber ->
            Await ifiber
        | ChainSuccess (effect, continuation) ->
            ChainSuccess (effect.UpcastError(), fun result -> (continuation result).UpcastError())
        | ChainError (effect, continuation) ->
            ChainError (effect.UpcastError(), fun result -> (continuation result).UpcastError())
        | Success result ->
            Success result
        | Failure error ->
            Failure (error :> obj)

    member internal this.Upcast<'R, 'E>() : FIO<obj, obj> =
        this.UpcastResult().UpcastError()

/// Creates an effect that succeeds immediately with the given result.
let succeed<'R, 'E> (result: 'R) : FIO<'R, 'E> =
    Success result

/// An alias for `succeed`, which succeeds immediately with the given result.
let inline ( !+ ) (result: 'R) : FIO<'R, 'E> =
    succeed result

/// Creates an effect that fails immediately with the given error.
let fail<'R, 'E> (error: 'E) : FIO<'R, 'E> =
    Failure error

/// An alias for `fail`, which fails with the error argument when executed.
let inline ( !- ) (error: 'E) : FIO<'R, 'E> =
    fail error

/// An alias for `Send`, which puts the message on the given channel and succeeds with the message.
let inline ( --> ) (message: 'R) (channel: Channel<'R>) : FIO<'R, 'E> =
    channel.Send message

/// An alias for `Send`, which puts the message on the given channel and succeeds with unit.
let inline ( -*> ) (message: 'R) (channel: Channel<'R>) : FIO<unit, 'E> =
    (channel.Send message).Then(succeed ())

/// An alias for `Receive`, which retrieves a message from the channel and succeeds with it.
let inline ( !->? ) (channel: Channel<'R>) : FIO<'R, 'E> =
    channel.Receive()

/// An alias for `Receive`, which retrieves a message from the channel and succeeds with unit.
let inline ( !*>? ) (channel: Channel<'R>) : FIO<unit, 'E> =
    channel.Receive().Then(succeed ())

/// An alias for `Fork`, which executes an effect concurrently and returns the fiber that executes it.
/// The fiber can be awaited for the result of the effect.
let inline ( ! ) (effect: FIO<'R, 'E>) : FIO<Fiber<'R, 'E>, 'E1> =
    effect.Fork()

/// An alias for `Fork`, which executes an effect concurrently and returns `unit` when executed.
let inline ( !! ) (effect: FIO<'R, 'E>) : FIO<unit, 'E1> =
    effect.Fork().BindOnSuccess(fun _ -> succeed ())

/// An alias for `Await`, which waits for the result of the given fiber and succeeds with it.
let inline ( !? ) (fiber: Fiber<'R, 'E>) : FIO<'R, 'E> =
    fiber.Await()

/// An alias for `BindOnSuccess`, which chains the success result of the effect to the continuation function.
let inline ( >>= ) (effect: FIO<'R, 'E>) (continuation: 'R -> FIO<'R1, 'E>) : FIO<'R1, 'E> =
    effect.BindOnSuccess continuation

/// An alias for `BindOnError`, which chains the error result of the effect to the continuation function.
let inline ( >>? ) (effect: FIO<'R, 'E>) (continuation: 'E -> FIO<'R, 'E1>) : FIO<'R, 'E1> =
    effect.BindOnError continuation

/// An alias for `Then`, which sequences two effects, ignoring the result of the first one.
let inline ( >> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R1, 'E> =
    leftEffect.Then rightEffect

/// An alias for `ApplyWith`, which combines two effects: one producing a function and the other a value, 
/// and applies the function to the value.
let inline ( >>> ) (effect: FIO<'R,' E>) (funcEffect: FIO<'R -> 'R1, 'E>) : FIO<'R1, 'E> =
    effect.ApplyWith funcEffect

/// An alias for `InParallelWith`, which executes two effects concurrently and succeeds with a tuple of their results when both complete.
/// If either effect fails, the error is immediately returned.
let inline ( <*> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
    leftEffect.InParallelWith rightEffect

/// An alias for `InParallelWith`, which executes two effects concurrently and succeeds with `unit` when completed.
/// If either effect fails, the error is immediately returned.
let inline ( <!> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<unit, 'E> =
    (leftEffect.InParallelWith rightEffect).Then(succeed ())

/// An alias for `ZipWith`, which combines the results of two effects into a tuple when both succeed.
/// If either effect fails, the error is immediately returned.
let inline ( <^> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
    leftEffect.ZipWith rightEffect

/// An alias for `RaceWith`, which succeeds with the result of the effect that completes first.
let inline ( <?> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R, 'E>) : FIO<'R, 'E> =
    leftEffect.RaceWith rightEffect

// TODO:

// 1. try-with in computation expressions is not exactly nice. It relies hardcore on exceptions. Can we do better?

// 2. Look through Examples.fs. again. Some of them can be improved a little bit.

// 3. Syntax for different effects. Look into Fsharp for fun and profit. See if you can create apply function.

// 2. Re-write benchmarks using FIO computation expressions.

// 4. Advanced and intermediate runtimes are not always working with tests. Figure out why.
// 4.1 Perhaps look into property-based testing?

// 5. Replace data available and completed and all that jazz with semaphores to make thread-safe Channel and Fibers? Perhaps create semaphores?

// 6. Everything that can be TailCall should have the TailCall attribute.