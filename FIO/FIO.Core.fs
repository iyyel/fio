(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module FIO.Core

open System.Collections.Concurrent
open System.Collections

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

and internal Stack = List<StackFrame>

and internal WorkItem =
    { 
        Effect: FIO<obj, obj>
        Stack: Stack
        IFiber: InternalFiber
        PrevAction: Action
    }

    static member Create effect stack ifiber prevAction =
        { 
            Effect = effect
            Stack = stack
            IFiber = ifiber
            PrevAction = prevAction
        }

    member internal this.Complete result =
        this.IFiber.Complete result

and internal InternalQueue<'T> = BlockingCollection<'T>

and internal InternalFiber internal (resultQueue: InternalQueue<Result<obj, obj>>, blockingWorkItemQueue: InternalQueue<WorkItem>) =

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

    member internal this.RescheduleBlockingWorkItems (workItemQueue: InternalQueue<WorkItem>) =
        while blockingWorkItemQueue.Count > 0 do
            workItemQueue.Add <| blockingWorkItemQueue.Take()

/// A Fiber is a construct that represents a lightweight-thread.
/// Fibers are used to execute multiple effects in parallel and
/// can be awaited to retrieve the result of the effect.
and Fiber<'R, 'E> private (resultQueue: InternalQueue<Result<obj, obj>>, blockingWorkItemQueue: InternalQueue<WorkItem>) =
    let completed : int64 ref = ref 0
    new() = Fiber(new  InternalQueue<Result<obj, obj>>(), new InternalQueue<WorkItem>())

    member internal this.ToInternal() = 
        InternalFiber(resultQueue, blockingWorkItemQueue)

    member this.Await() : Result<'R, 'E> =
        let result = resultQueue.Take()
        resultQueue.Add result
        match result with
        | Ok result -> Ok (result :?> 'R)
        | Error error -> Error (error :?> 'E)

/// A channel represents a communication queue that holds
/// data of the type 'D. Data can be both be sent and 
/// retrieved (blocking) on a channel.
and Channel<'D> private (dataQueue: InternalQueue<obj>, blockingWorkItemQueue: InternalQueue<WorkItem>) =
    new() = Channel(new InternalQueue<obj>(), new InternalQueue<WorkItem>())

    member internal this.AddBlockingWorkItem workItem =
        blockingWorkItemQueue.Add workItem

    member internal this.RescheduleBlockingWorkItem (workItemQueue: InternalQueue<WorkItem>) =
        if blockingWorkItemQueue.Count > 0 then
            workItemQueue.Add <| blockingWorkItemQueue.Take()

    member internal this.HasBlockingWorkItems() =
        blockingWorkItemQueue.Count > 0

    member internal this.Upcast() = 
        Channel<obj>(dataQueue, blockingWorkItemQueue)

    member this.Add (data : 'D) =
        dataQueue.Add data

    member this.Take() : 'D =
        dataQueue.Take() :?> 'D

    member this.Count() = 
        dataQueue.Count
        
/// FIO is an effect that can either succeed with a result ('R)
/// or fail with an error ('E) when interpreted.
and FIO<'R, 'E> =
    internal
    | NonBlocking of action: (unit -> Result<'R, 'E>)
    | Blocking of channel: Channel<'R>
    | Send of message: 'R * channel: Channel<'R>
    | Concurrent of effect: FIO<obj, obj> * fiber: obj * ifiber: InternalFiber
    | Await of ifiber: InternalFiber
    | SequenceSuccess of effect: FIO<obj, 'E> * continuation: (obj -> FIO<'R, 'E>)
    | SequenceError of effect: FIO<obj, obj> * continuation: (obj -> FIO<'R, 'E>)
    | Success of result: 'R
    | Failure of error: 'E

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
        | SequenceSuccess (effect, continuation) ->
            SequenceSuccess (effect, fun result -> (continuation result).UpcastResult())
        | SequenceError (effect, continuation) ->
            SequenceError (effect, fun result -> (continuation result).UpcastResult())
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
        | SequenceSuccess (effect, continuation) ->
            SequenceSuccess (effect.UpcastError(), fun result -> (continuation result).UpcastError())
        | SequenceError (effect, continuation) ->
            SequenceError (effect.UpcastError(), fun result -> (continuation result).UpcastError())
        | Success result ->
            Success result
        | Failure error ->
            Failure (error :> obj)
            
    member internal this.Upcast<'R, 'E>() : FIO<obj, obj> =
        this.UpcastResult().UpcastError()

/// Transforms the expression (func) into a FIO. OBS: This has temporarily been renamed to fioZ to not conflict with the F# fio computation expression.
/// The fio computation expression is going to replace this function in the future.
let fioZ<'R, 'E> (func : Unit -> 'R) : FIO<'R, 'E> =
    NonBlocking (fun _ -> Ok (func ()))

/// succeed is an effect that succeeds with the result argument ('R) when interpreted.
let succeed<'R, 'E> (result : 'R) : FIO<'R, 'E> =
    Success result

/// !+ is an alias of succeed which succeeds with the result argument ('R) when interpreted.
let ( !+ ) (result : 'R) : FIO<'R, 'E> =
    succeed result

/// fail is an effect that fails with the error argument ('E) when interpreted.
let fail<'R, 'E> (error : 'E) : FIO<'R, 'E> =
    Failure error

/// !- is an alias of fail which fails with the error argument ('E) when interpreted.
let ( !- ) (error : 'E) : FIO<'R, 'E> =
    fail error

/// stop is an effect that ends the effect by succeeding with Unit when interpreted.
let stop<'E> (unit : Unit) : FIO<Unit, 'E> =
    Success unit

/// ! is an alias of stop which ends the effect by succeeding with Unit when interpreted.
let ( ! ) (unit : Unit) : FIO<Unit, 'E> =
    stop unit

/// send is an effect that puts a message on the channel argument when interpreted.
/// The effect succeeds with the message argument ('R).
let send<'R, 'E> (message : 'R) (channel : Channel<'R>) : FIO<'R, 'E> =
    Send (message, channel)

/// *> is an alias of send which puts a message on the channel argument when interpreted.
/// The effect succeeds with the message argument ('R).
let ( *> ) (message : 'R) (channel : Channel<'R>) : FIO<'R, 'E> =
    send message channel

/// receive is a blocking effect that awaits message retrieval on the channel argument when interpreted.
/// The effect succeeds with the received message argument ('R).
let receive<'R, 'E> (channel : Channel<'R>) : FIO<'R, 'E> =
    Blocking channel

/// !*> is an alias of receive which awaits message retrieval on the channel argument when interpreted.
/// The effect succeeds with the received message argument ('R).
let ( !*> ) (channel : Channel<'R>) : FIO<'R, 'E> =
    receive channel

/// concurrently is an effect that executes the effect argument concurrently when interpreted.
/// The effect succeeds with the associated fiber which can be awaited to retrieve the result.
let concurrently<'R1, 'E1, 'E> (effect : FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
    let fiber = new Fiber<'R1, 'E1>()
    Concurrent (effect.Upcast(), fiber, fiber.ToInternal())

/// !> is an alias of concurrently which executes the effect argument concurrently when interpreted.
/// The effect succeeds with the associated fiber which can be awaited to retrieve the result.
let ( !> ) (effect : FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
    concurrently effect

/// await is a blocking effect that awaits the result of the fiber argument when interpreted.
/// The effect succeeds with the result of the fiber argument ('R).
let await<'R, 'E> (fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
    Await <| fiber.ToInternal()

/// !?> is an alias of await which awaits the result of the fiber argument when interpreted.
/// The effect succeeds with the result of the fiber argument ('R).
let ( !?> ) (fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
    await fiber

/// sequenceSuccess is an effect that passes the success result ('R1) of the effect argument 
/// to the continuation function when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let sequenceSuccess<'R1, 'R, 'E> (effect : FIO<'R1, 'E>) (continuation : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    SequenceSuccess (effect.UpcastResult(), fun result -> continuation (result :?> 'R1))

/// >> is an alias of sequenceSuccess. which passes the success result ('R1) of the effect argument
/// to the continuation function when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let ( >> ) (effect : FIO<'R1, 'E>) (continuation : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    sequenceSuccess effect continuation

/// sequenceError is an effect that passes the error result ('E1) of the effect argument
/// to the continuation function when interpreted.
/// If a success ('R) occurs the effect returns immediately. TODO: Is this part true?
let sequenceError<'R, 'E1, 'E> (effect : FIO<'R, 'E1>) (continuation : 'E1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    SequenceError (effect.Upcast(), fun error -> continuation (error :?> 'E1))

/// ?> is an alias of sequenceError which passes the error result ('E1) of the effect argument
/// to the continuation function when interpreted.
/// If a success ('R) occurs the effect returns immediately. TODO: Is this part true?
let ( ?> ) (effect : FIO<'R, 'E1>) (continuation : 'E1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    sequenceError effect continuation

/// parallelize is an effect that executes the two effect arguments in parallel when interpreted.
/// The effects succeeds with the results of the two effect arguments in a tuple ('R1 * 'R2).
/// If an error ('E) occurs the effect returns immediately.
let parallelize<'R1, 'R2, 'E> (firstEffect : FIO<'R1, 'E>) (secondEffect : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    !> secondEffect >> fun secondFiber ->
    firstEffect >> fun firstResult ->
    !?> secondFiber >> fun secondResult ->
    !+ (firstResult, secondResult)

/// <*> is an alias of parallelize which executes the two effect arguments in parallel when interpreted.
/// The effects succeeds with the results of the two effect arguments in a tuple ('R1 * 'R2).
/// If an error ('E) occurs the effect returns immediately.
let ( <*> ) (leftEffect : FIO<'R1, 'E>) (rightEffect : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    parallelize leftEffect rightEffect
    
/// <!> is an alias of parallelize which executes the two effect arguments in parallel when interpreted.
/// The effects succeeds with Unit.
/// If an error ('E) occurs the effect returns immediately.
let ( <!> ) (leftEffect : FIO<'R1, 'E>) (rightEffect : FIO<'R2, 'E>) : FIO<Unit, 'E> =
    parallelize leftEffect rightEffect >> fun _ -> ! ()

/// zip is an effect that succeeds with the results of the effect arguments ('R1, 'R2) in a tuple ('R1 * 'R2) when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let zip<'R1, 'R2, 'E> (firstEffect : FIO<'R1, 'E>) (secondEffect : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    firstEffect >> fun firstResult ->
    secondEffect >> fun secondResult ->
    !+ (firstResult, secondResult)

/// <^> is an alias of zip which succeeds with the results of the effect arguments ('R1, 'R2) in a tuple ('R1 * 'R2) when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let ( <^> ) (leftEffect : FIO<'R1, 'E>) (rightEffect : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    zip leftEffect rightEffect

/// race is an effect that races the two effect arguments against each other,
/// where the result of the effect that completes first ('R) is returned on success when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let race<'R, 'E> (firstEffect : FIO<'R, 'E>) (secondEffect : FIO<'R, 'E>) : FIO<'R, 'E> =
    let rec loop (firstFiber : InternalFiber) (secondFiber : InternalFiber) =
        if firstFiber.Completed() then firstFiber
        else if secondFiber.Completed() then secondFiber
        else loop firstFiber secondFiber
    !> firstEffect >> fun firstFiber ->
    !> secondEffect >> fun secondFiber ->
    match (loop (firstFiber.ToInternal()) (secondFiber.ToInternal())).Await() with
    | Ok result -> !+ (result :?> 'R)
    | Error error -> !- (error :?> 'E)

/// <?> is an alias of race which races the two effect arguments against each other,
/// where the result of the effect that completes first ('R) is returned on success when interpreted.
/// If an error ('E) occurs the effect returns immediately.
let ( <?> ) (leftEffect : FIO<'R, 'E>) (rightEffect : FIO<'R, 'E>) : FIO<'R, 'E> =
    race leftEffect rightEffect

// TODO: It would be nice if we could put this into its own module.
module internal FIOBuilderHelper =

     /// Binds the result of one FIO computation to the next.
    let rec Bind (effect : FIO<'R1, 'E>) (continuation : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        effect >> continuation

    /// Wraps a value in a successful FIO computation.
    let Return (result : 'R) : FIO<'R, 'E> =
        !+ result

    /// Directly returns an existing FIO computation.
    let ReturnFrom (effect : FIO<'R, 'E>) : FIO<'R, 'E> =
        effect

    /// Combines two computations, running one after the other.
    let Combine (firstEffect : FIO<'R, 'E>) (secondEffect : FIO<'R1, 'E>) : FIO<'R1, 'E> =
        firstEffect >> fun _ -> secondEffect

    /// Handles "zero" computations, which in this case might signify failure or stopping.
    let Zero () : FIO<Unit, 'E> =
        ! ()

    /// Delays the execution of an FIO computation.
    let Delay (factory : unit -> FIO<'R, 'E>) : FIO<'R, 'E> =
        NonBlocking (fun () -> Ok ()) >> fun _ -> factory ()

    /// Evaluates a delayed FIO computation.
    let Run (effect : FIO<'R, 'E>) : FIO<'R, 'E> =
        effect

    /// Handles failure cases in the effect using the provided handler.
    let TryWith (effect : FIO<'R, 'E>) (handler : 'E -> FIO<'R, 'E>) : FIO<'R, 'E> =
        effect ?> handler

    /// Ensures a finalizer is executed after the main computation, regardless of success or failure.
    let TryFinally (effect : FIO<'R, 'E>) (finalizer : unit -> unit) : FIO<'R, 'E> =
        effect >> fun result ->
        try
            finalizer ()
            !+ result
        with ex ->
            // Optional: Handle unexpected exceptions in the finalizer if needed.
            !- ex

type FIOBuilder () =
    member this.Bind(effect, continuation) = FIOBuilderHelper.Bind effect continuation
    member this.Return(result) = FIOBuilderHelper.Return result
    member this.ReturnFrom(result) = FIOBuilderHelper.ReturnFrom result
    member this.Combine(firstEffect, secondEffect) = FIOBuilderHelper.Combine firstEffect secondEffect
    member this.Zero() = FIOBuilderHelper.Zero
    member this.Delay(factory) = FIOBuilderHelper.Delay factory
    member this.Run(effect) = FIOBuilderHelper.Run effect
    member this.TryWith(effect, handler) = FIOBuilderHelper.TryWith effect handler
    member this.TryFinally(effect, finalizer) = FIOBuilderHelper.TryFinally effect finalizer

let fio = FIOBuilder ()

// 2. Der skal være computation expressions for fio. Find ud af hvordan det bedst sættes op.

// 3. Find ud af hvordan vi får "gemt" en runtime og bare kan køre den på fio computation expressions. Måske en default advanced runtime eller noget i den stil?

// 4. Kan vi omskrive eksemplerne og få dem til at køre med den nye stil?

// 5. Tests?

// TODO: Replace data available and completed and all that jazz with semaphores to make thread-safe Channel and Fibers? Perhaps create semaphores?