(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Core.DSL

open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading

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

    /// Await is an effect that awaits the result of the fiber argument when executed.
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

    /// Send is an effect that puts the message argument on the channel and succeeds 
    /// with the message argument when executed.
    member this.Send<'R, 'E> (message: 'R) : FIO<'R, 'E> =
        Send (message, this)

    /// Receive is an effect that awaits message retrieval on the channel and succeeds 
    /// with the first message that is retrieved when executed.
    member this.Receive<'R, 'E>() : FIO<'R, 'E> =
        Blocking this

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

    /// Fork is an effect that executes the effect argument concurrently
    /// and succeeds with the associated fiber when executed.
    member this.Fork<'R, 'E, 'E1>() : FIO<Fiber<'R, 'E>, 'E1> =
        let fiber = new Fiber<'R, 'E>()
        Concurrent (this.Upcast(), fiber, fiber.ToInternal())

    /// OnSuccess is an effect that passes the success result of the effect argument
    /// to the specified continuation function when executed. Errors are 
    /// immediately returned.
    member this.OnSuccess<'R, 'R1, 'E> (continuation: 'R -> FIO<'R1, 'E>) : FIO<'R1, 'E> =
        ChainSuccess (this.UpcastResult(), fun result -> continuation (result :?> 'R))

    /// OnError is an effect that passes the error of the effect argument
    /// to the specified continuation function when executed. Success results are 
    /// immediately returned. // TODO: Is this part true?
    member this.OnError<'R, 'E, 'E1> (continuation: 'E -> FIO<'R, 'E1>) : FIO<'R, 'E1> =
        ChainError (this.Upcast(), fun error -> continuation (error :?> 'E))

    /// Then is an effect that sequences the two effect arguments where the result
    /// of the first effect is ignored. Errors are immediately returned.
    member this.Then<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R1, 'E> =
        ChainSuccess (this.UpcastResult(), fun _ -> other)

    /// Parallel is an effect that executes the two effect arguments in parallel and succeeds
    /// with the results in a tuple when executed. Errors are immediately returned.
    member this.Parallel<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
        other.Fork().OnSuccess(fun otherFiber ->
            this.OnSuccess(fun thisResult ->
                (Await <| otherFiber.ToInternal()).OnSuccess(fun otherResult ->
                    Success (thisResult, otherResult))))

    /// Zip is an effect that succeeds with the results of the effect arguments in a
    /// tuple when executed. Errors are immediately returned.
    member this.Zip<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
        this.OnSuccess(fun thisResult ->
            other.OnSuccess(fun otherResult ->
                Success (thisResult, otherResult)))

    /// Race is an effect that succeeds with the result of the effect that
    /// completes first when executed. Errors are immediately returned.
    member this.Race<'R, 'E> (other: FIO<'R, 'E>) : FIO<'R, 'E> =
        let rec loop (thisFiber: InternalFiber) (otherFiber: InternalFiber) =
            if thisFiber.Completed() then thisFiber
            else if otherFiber.Completed() then otherFiber
            else loop thisFiber otherFiber
        this.Fork().OnSuccess(fun thisFiber ->
            other.Fork().OnSuccess(fun otherFiber ->
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

/// succeed is an effect that succeeds with the result argument when executed.
let succeed<'R, 'E> (result: 'R) : FIO<'R, 'E> =
    Success result

/// !+ is a short-hand alias of succeed which succeeds with the result argument when executed.
let inline ( !+ ) (result: 'R) : FIO<'R, 'E> =
    succeed result

/// fail is an effect that fails with the error argument when executed.
let fail<'R, 'E> (error: 'E) : FIO<'R, 'E> =
    Failure error

/// !- is a short-hand alias of fail which fails with the error argument when executed.
let inline ( !- ) (error: 'E) : FIO<'R, 'E> =
    fail error

/// **> is a short-hand alias of Send on a channel which is an effect that puts the message argument
/// on the channel argument and succeeds with the message argument when executed.
let inline ( **> ) (message: 'R) (channel: Channel<'R>) : FIO<'R, 'E> =
    channel.Send message

/// !*? is a short-hand alias of Receive on a channel which awaits message retrieval on the channel argument
/// and succeeds with the first message that is retrieved when executed.
let inline ( !*? ) (channel: Channel<'R>) : FIO<'R, 'E> =
    channel.Receive()

/// ! is a short-hand alias of Fork on an effect which executes the effect argument concurrently
/// and succeeds with the associated fiber when executed.
let inline ( ! ) (effect: FIO<'R, 'E>) : FIO<Fiber<'R, 'E>, 'E1> =
    effect.Fork()

/// !! is a short-hand alias of Fork on an effect which executes the effect argument concurrently
/// and succeeds with unit when executed.
let inline ( !! ) (effect: FIO<'R, 'E>) : FIO<unit, 'E1> =
    effect.Fork().OnSuccess(fun _ -> !+ ())

/// !? is a short-hand alias of Await on a fiber which awaits the result
/// of the fiber argument when executed.
let inline ( !? ) (fiber: Fiber<'R, 'E>) : FIO<'R, 'E> =
    fiber.Await()

/// >>= is a short-hand alias of OnSuccess on an effect which passes the success result of the effect argument
/// to the specified continuation function when executed. Errors are 
/// immediately returned.
let inline ( >>= ) (effect: FIO<'R, 'E>) (continuation: 'R -> FIO<'R1, 'E>) : FIO<'R1, 'E> =
    effect.OnSuccess continuation

/// >> is a short-hand of Then which sequences the two effect arguments where the result
/// of the first effect is ignored. Errors are immediately returned.
let inline ( >> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R1, 'E> =
    leftEffect.Then rightEffect

/// >>? is a short-hand alias of OnError on an effect which sequences the two effect arguments where the result
/// of the first effect is ignored. Errors are immediately returned.
let inline ( >>? ) (effect: FIO<'R, 'E>) (continuation: 'E -> FIO<'R, 'E1>) : FIO<'R, 'E1> =
    effect.OnError continuation

/// <*> is a short-hand alias of Parallel on an effect which executes the two effect arguments in parallel and succeeds
/// with the results in a tuple when executed. Errors are immediately returned.
let inline ( <*> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
    leftEffect.Parallel rightEffect

/// <!> is a short-hand alias of Parallel on an effect which executes the two effect arguments in parallel and succeeds
/// with unit when executed. Errors are immediately returned.
let inline ( <!> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<unit, 'E> =
    leftEffect <*> rightEffect >> !+ ()

/// <^> is a short-hand alias of Zip on an effect which succeeds with the results of the effect arguments in a
/// tuple when executed. Errors are immediately returned.
let inline ( <^> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
    leftEffect.Zip rightEffect

/// <?> is an alias of Race on an effect which succeeds with the result of the effect that
/// completes first when executed. Errors are immediately returned.
let inline ( <?> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R, 'E>) : FIO<'R, 'E> =
    leftEffect.Race rightEffect

// TODO:

// 1. try-with in computation expressions is not exactly nice. It relies hardcore on exceptions. Can we do better?

// 2. See if we can use Channel instead of BlockingCollection?

// 3. Re-write benchmarks using FIO computation expressions.

// 4. Advanced and intermediate runtimes are not always working with tests. Figure out why.
// 4.1 Perhaps look into property-based testing?

// 5. Replace data available and completed and all that jazz with semaphores to make thread-safe Channel and Fibers? Perhaps create semaphores?

// 6. Everything that can be TailCall should have the TailCall attribute.