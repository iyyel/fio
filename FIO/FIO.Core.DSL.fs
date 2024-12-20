(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Core.DSL

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
    member internal this.Complete result =
        if resultQueue.Count = 0 then
            resultQueue.Add result
        else
            failwith "InternalFiber: Complete was called on an already completed InternalFiber!"

    member internal this.Await() =
        let result = resultQueue.Take()
        resultQueue.Add result
        result

    member internal this.Completed() =
        resultQueue.Count > 0

    member internal this.AddBlockingWorkItem workItem =
        blockingWorkItemQueue.Add workItem

    member internal this.BlockingWorkItemsCount() =
        blockingWorkItemQueue.Count

    member internal this.RescheduleBlockingWorkItems(workItemQueue: InternalQueue<WorkItem>) =
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

    member internal this.ToInternal() =
        InternalFiber(resultQueue, blockingWorkItemQueue)

    member this.AwaitResult() : Result<'R, 'E> =
        let result = resultQueue.Take()
        resultQueue.Add result
        match result with
        | Ok result -> Ok (result :?> 'R)
        | Error error -> Error (error :?> 'E)

    member this.Await() : FIO<'R, 'E> =
        Await <| this.ToInternal()

/// A channel represents a communication queue that holds
/// data of the type 'D. Data can be both be sent and
/// retrieved (blocking) on a channel.
and Channel<'R> private (
    dataQueue: InternalQueue<obj>,
    blockingWorkItemQueue: InternalQueue<WorkItem>
    ) =

    new() = Channel(new InternalQueue<obj>(), new InternalQueue<WorkItem>())
    
    member internal this.AddBlockingWorkItem workItem =
        blockingWorkItemQueue.Add workItem

    member internal this.RescheduleBlockingWorkItem(workItemQueue: InternalQueue<WorkItem>) =
        if blockingWorkItemQueue.Count > 0 then
            workItemQueue.Add <| blockingWorkItemQueue.Take()

    member internal this.HasBlockingWorkItems() =
        blockingWorkItemQueue.Count > 0

    member internal this.Upcast() =
        Channel<obj>(dataQueue, blockingWorkItemQueue)

    member this.Add(data: 'R) =
        dataQueue.Add data

    member this.Take() : 'R =
        dataQueue.Take() :?> 'R

    member this.Count() =
        dataQueue.Count

    member this.Receive<'R, 'E>() : FIO<'R, 'E> =
        Blocking this

    member this.Send<'R, 'E> (message: 'R) : FIO<'R, 'E> =
        Send (message, this)

/// FIO is an effect that can either succeed with a result ('R)
/// or fail with an error ('E) when interpreted.
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

    /// Fork is an effect that executes the effect argument concurrently when interpreted.
    /// The effect succeeds with the associated fiber which can be awaited to retrieve the result.
    member this.Fork<'R, 'E, 'E1>() : FIO<Fiber<'R, 'E>, 'E1> =
        let fiber = new Fiber<'R, 'E>()
        Concurrent (this.Upcast(), fiber, fiber.ToInternal())

    /// OnSuccess is an effect that passes the success result ('R1) of the effect argument
    /// to the continuation function when interpreted.
    /// If an error ('E) occurs the effect returns immediately.
    member this.OnSuccess<'R, 'R1, 'E> (continuation: 'R -> FIO<'R1, 'E>) : FIO<'R1, 'E> =
        ChainSuccess (this.UpcastResult(), fun result -> continuation (result :?> 'R))

    /// OnError is an effect that passes the error result ('E1) of the effect argument
    /// to the continuation function when interpreted.
    /// If a success ('R) occurs the effect returns immediately. TODO: Is this part true?
    member this.OnError<'R, 'E, 'E1> (continuation: 'E -> FIO<'R, 'E1>) : FIO<'R, 'E1> =
        ChainError (this.Upcast(), fun error -> continuation (error :?> 'E))

    /// Then is an effect that sequences the two effect arguments where the result
    /// of the first effect ('R1) is ignored.
    /// If an error ('E) occurs the effect returns immediately.
    member this.Then<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R1, 'E> =
        ChainSuccess (this.UpcastResult(), fun _ -> other)

    /// Parallel is an effect that executes the two effect arguments in parallel when interpreted.
    /// The effects succeeds with the results of the two effect arguments in a tuple ('R1 * 'R2).
    /// If an error ('E) occurs the effect returns immediately.
    member this.Parallel<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
        other.Fork().OnSuccess(fun otherFiber ->
            this.OnSuccess(fun thisResult ->
                (Await <| otherFiber.ToInternal()).OnSuccess(fun otherResult ->
                    Success (thisResult, otherResult))))

    /// Zip is an effect that succeeds with the results of the effect arguments ('R1, 'R2) in a tuple ('R1 * 'R2) when interpreted.
    /// If an error ('E) occurs the effect returns immediately.
    member this.Zip<'R, 'R1, 'E> (other: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
        this.OnSuccess(fun thisResult ->
            other.OnSuccess(fun otherResult ->
                Success (thisResult, otherResult)))

    /// Race is an effect that races the two effect arguments against each other,
    /// where the result of the effect that completes first ('R) is returned on success when interpreted.
    /// If an error ('E) occurs the effect returns immediately.
    member this.Race<'R, 'E> (other: FIO<'R, 'E>) : FIO<'R, 'E> =
        let rec loop (thisFiber: InternalFiber) (otherFiber: InternalFiber) =
            if thisFiber.Completed() then thisFiber
            else if otherFiber.Completed() then otherFiber
            else loop thisFiber otherFiber
        this.Fork().OnSuccess(fun thisFiber ->
            other.Fork().OnSuccess(fun otherFiber ->
                match (loop (thisFiber.ToInternal()) (otherFiber.ToInternal())).Await() with
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

/// succeed is an effect that succeeds with the result argument ('R) when interpreted.
let succeed<'R, 'E> (result: 'R) : FIO<'R, 'E> =
    Success result

/// !+ is an alias of succeed which succeeds with the result argument ('R) when interpreted.
let ( !+ ) (result: 'R) : FIO<'R, 'E> =
    succeed result

/// fail is an effect that fails with the error argument ('E) when interpreted.
let fail<'R, 'E> (error: 'E) : FIO<'R, 'E> =
    Failure error

/// !- is an alias of fail which fails with the error argument ('E) when interpreted.
let ( !- ) (error: 'E) : FIO<'R, 'E> =
    fail error

/// stop is an effect that ends the effect by succeeding with Unit when interpreted.
let stop<'E> (unit: Unit) : FIO<Unit, 'E> =
    Success unit

/// ! is an alias of stop which ends the effect by succeeding with Unit when interpreted.
let ( ! ) (unit: Unit) : FIO<Unit, 'E> =
    stop unit

/// **> is an alias of send which puts a message on the channel argument when interpreted.
/// The effect succeeds with the message argument ('R).
let ( **> ) (message: 'R) (channel: Channel<'R>) : FIO<'R, 'E> =
    channel.Send message

/// !*> is an alias of receive which awaits message retrieval on the channel argument when interpreted.
/// The effect succeeds with the received message argument ('R).
let ( !*? ) (channel: Channel<'R>) : FIO<'R, 'E> =
    channel.Receive()

/// !> is an alias of concurrently which executes the effect argument concurrently when interpreted.
/// The effect succeeds with the associated fiber which can be awaited to retrieve the result.
let ( !! ) (effect: FIO<'R, 'E>) : FIO<Fiber<'R, 'E>, 'E1> =
    effect.Fork()

/// !> is an alias of concurrently which executes the effect argument concurrently when interpreted.
/// The effect succeeds with the associated fiber which can be awaited to retrieve the result.
let ( !!! ) (effect: FIO<'R, 'E>) : FIO<unit, 'E1> =
    effect.Fork().OnSuccess(fun _ -> ! ())

/// !?> is an alias of await which awaits the result of the fiber argument when interpreted.
/// The effect succeeds with the result of the fiber argument ('R).
let ( !? ) (fiber: Fiber<'R, 'E>) : FIO<'R, 'E> =
    fiber.Await()

/// >>= is an alias of chainSuccess which passes the success result ('R1) of the effect argument
/// to the continuation function when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let ( >>= ) (effect: FIO<'R, 'E>) (continuation: 'R -> FIO<'R1, 'E>) : FIO<'R1, 'E> =
    effect.OnSuccess continuation

/// >> is an alias of Then which sequences the two effect arguments where the result
/// of the first effect ('R1) is ignored.
/// If an error ('E) occurs the effect returns immediately.
let ( >> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R1, 'E> =
    leftEffect.Then rightEffect

/// >>? is an alias of sequenceError which passes the error result ('E1) of the effect argument
/// to the continuation function when interpreted.
/// If a success ('R) occurs the effect returns immediately. TODO: Is this part true?
let ( >>? ) (effect: FIO<'R, 'E>) (continuation: 'E -> FIO<'R, 'E1>) : FIO<'R, 'E1> =
    effect.OnError continuation

/// <*> is an alias of parallelize which executes the two effect arguments in parallel when interpreted.
/// The effects succeeds with the results of the two effect arguments in a tuple ('R1 * 'R2).
/// If an error ('E) occurs the effect returns immediately.
let ( <*> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
    leftEffect.Parallel rightEffect

/// <!> is an alias of parallelize which executes the two effect arguments in parallel when interpreted.
/// The effects succeeds with Unit.
/// If an error ('E) occurs the effect returns immediately.
let ( <!> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<Unit, 'E> =
    leftEffect <*> rightEffect >>= fun _ -> ! ()

/// <^> is an alias of zip which succeeds with the results of the effect arguments ('R1, 'R2) in a tuple ('R1 * 'R2) when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let ( <^> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R1, 'E>) : FIO<'R * 'R1, 'E> =
    leftEffect.Zip rightEffect

/// <?> is an alias of race which races the two effect arguments against each other,
/// where the result of the effect that completes first ('R) is returned on success when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let ( <?> ) (leftEffect: FIO<'R, 'E>) (rightEffect: FIO<'R, 'E>) : FIO<'R, 'E> =
    leftEffect.Race rightEffect

// 4. Kan vi omskrive eksemplerne og få dem til at køre med den nye stil (FIO, og app?)

// 5. Advanced runtime virker ikke altid i tests. Kan vi finde ud af hvorfor? Internemdiate virker måske ikke, da jeg har fjernet async.

// 6. Flere tests?

// 7. Publish en nuget pakke.

// TODO: Replace data available and completed and all that jazz with semaphores to make thread-safe Channel and Fibers? Perhaps create semaphores?
