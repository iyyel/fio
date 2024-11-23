(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module FIO.Core

open System.Threading
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

and internal Queue<'T> = BlockingCollection<'T>

and internal InternalFiber internal (resultQueue: Queue<Result<obj, obj>>, blockingWorkItemQueue: Queue<WorkItem>, completed: int64 ref) =

    member internal this.Complete result =
        let mutable temp = completed.Value
        if Interlocked.Read &temp = 0 then
            Interlocked.Exchange(completed, 1) |> ignore
            resultQueue.Add result
        else
            failwith "InternalFiber: Complete was called on an already completed InternalFiber!"

    member internal this.Await() =
        let result = resultQueue.Take()
        resultQueue.Add result
        result

    member internal this.Completed() =
        let mutable temp = completed.Value
        Interlocked.Read &temp = 1

    member internal this.AddBlockingWorkItem workItem =
        blockingWorkItemQueue.Add workItem

    member internal this.BlockingWorkItemsCount() =
        blockingWorkItemQueue.Count

    member internal this.RescheduleBlockingWorkItems (workItemQueue: Queue<WorkItem>) =
        while blockingWorkItemQueue.Count > 0 do
            workItemQueue.Add <| blockingWorkItemQueue.Take()

/// A Fiber is a construct that represents a lightweight-thread.
/// Fibers are used to execute multiple effects in parallel and
/// can be awaited to retrieve the result of the effect.
and Fiber<'R, 'E> private (resultQueue: Queue<Result<obj, obj>>, blockingWorkItemQueue: Queue<WorkItem>) =
    let completed : int64 ref = ref 0
    new() = Fiber(new Queue<Result<obj, obj>>(), new Queue<WorkItem>())

    member internal this.ToInternal() = 
        InternalFiber(resultQueue, blockingWorkItemQueue, completed)

    member this.Await() : Result<'R, 'E> =
        let result = resultQueue.Take()
        resultQueue.Add result
        match result with
        | Ok result -> Ok (result :?> 'R)
        | Error error -> Error (error :?> 'E)

/// A channel represents a communication queue that holds
/// data of the type ('R). Data can be both be sent and 
/// retrieved (blocking) on a channel.
and Channel<'D> private (dataQueue: Queue<obj>, blockingWorkItemQueue: Queue<WorkItem>, dataCounter: int64 ref) =

    new() = Channel(new Queue<obj>(), new Queue<WorkItem>(), ref 0)

    member internal this.AddBlockingWorkItem workItem =
        blockingWorkItemQueue.Add workItem

    member internal this.RescheduleBlockingWorkItem (workItemQueue: Queue<WorkItem>) =
        if blockingWorkItemQueue.Count > 0 then
            workItemQueue.Add <| blockingWorkItemQueue.Take()

    member internal this.HasBlockingWorkItems() =
        blockingWorkItemQueue.Count > 0

    member internal this.Upcast() = 
        Channel<obj>(dataQueue, blockingWorkItemQueue, dataCounter)

    member internal this.UseAvailableData() =
        Interlocked.Decrement dataCounter |> ignore

    member internal this.DataAvailable() =
        let mutable temp = dataCounter.Value
        Interlocked.Read &temp > 0

    member this.Add (data : 'D) =
        Interlocked.Increment dataCounter |> ignore
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
    | SendMessage of message: 'R * channel: Channel<'R>
    | Concurrent of effect: FIO<obj, obj> * fiber: obj * ifiber: InternalFiber
    | AwaitFiber of ifiber: InternalFiber
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
        | SendMessage (message, channel) ->
            SendMessage (message :> obj, channel.Upcast())
        | Concurrent (effect, fiber, ifiber) ->
            Concurrent (effect, fiber, ifiber)
        | AwaitFiber ifiber ->
            AwaitFiber ifiber
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
        | SendMessage (message, channel) ->
            SendMessage (message, channel)
        | Concurrent (effect, fiber, ifiber) ->
            Concurrent (effect, fiber, ifiber)
        | AwaitFiber ifiber ->
            AwaitFiber ifiber
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
let fioZ<'R, 'E> (func : Unit -> 'R) : FIO<'R, 'E> =
    NonBlocking (fun _ -> Ok (func ()))

/// succeed creates an effect that succeeds with the result argument when interpreted.
let succeed<'R, 'E> (result : 'R) : FIO<'R, 'E> =
    Success result

/// fail creates an effect that fails with the error argument when interpreted.
let fail<'R, 'E> (error : 'E) : FIO<'R, 'E> =
    Failure error

/// stop creates an effect that models the end of an effect by succeeding with Unit when interpreted.
let stop<'E> : FIO<Unit, 'E> =
    Success ()

/// *> (send message) creates an effect that puts a message on the channel argument when interpreted.
let ( *> ) (message : 'R) (channel : Channel<'R>) : FIO<'R, 'E> =
    SendMessage (message, channel)

/// receive creates a blocking effect that awaits data retrieval on the channel argument when interpreted.
let receive<'R, 'E> (channel : Channel<'R>) : FIO<'R, 'E> =
    Blocking channel

/// concurrently creates an effect that executes the effect argument concurrently
/// and returns the associated fiber to the continuation when interpreted.
let concurrently<'R1, 'E1, 'E> (effect : FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
    let fiber = new Fiber<'R1, 'E1>()
    Concurrent (effect.Upcast(), fiber, fiber.ToInternal())

/// await creates a blocking effect that awaits the result of the fiber argument when interpreted.
let await<'R, 'E> (fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
    AwaitFiber <| fiber.ToInternal()

/// >> (sequence success) creates an effect that passes the success result of the effect argument 
/// to the continuation when interpreted. Returns immediately if error.
let ( >> ) (effect : FIO<'R1, 'E>) (continuation : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    SequenceSuccess (effect.UpcastResult(), fun result -> continuation (result :?> 'R1))

/// ?> (sequence error) creates an effect that passes the error result of the effect argument to the continuation function when interpreted.
let ( ?> ) (effect : FIO<'R, 'E1>) (continuation : 'E1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    SequenceError (effect.Upcast(), fun error -> continuation (error :?> 'E1))

/// <~> (parallelize) creates an effect that executes the two effect arguments in parallel when interpreted.
/// Returns immediately if error.
let ( <~> ) (leftEffect : FIO<'R1, 'E>) (rightEffect : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    concurrently rightEffect >> fun rightFiber ->
    leftEffect >> fun leftResult ->
    await rightFiber >> fun rightResult ->
    Success (leftResult, rightResult)
    
/// <*> (parallelize Unit) creates an effect that executes the two effect arguments in parallel and returns Unit if success when interpreted.
/// Returns immediately if error.
let ( <*> ) (leftEffect : FIO<'R1, 'E>) (rightEffect : FIO<'R2, 'E>) : FIO<Unit, 'E> =
    leftEffect <~> rightEffect
    >> fun _ ->
    stop

/// <^> (zip) creates an effect that returns the results of the effect arguments in a tuple when interpreted.
/// Errors are returned immediately.
let ( <^> ) (leftEffect : FIO<'R1, 'E>) (rightEffect : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    leftEffect >> fun leftResult ->
    rightEffect >> fun rightResult ->
    Success (leftResult, rightResult)

/// <?> (race) creates an effect that races the two effect arguments against each other,
/// where the result of the effect that completes first is returned when interpreted.
let ( <?> ) (leftEffect : FIO<'R, 'E>) (rightEffect : FIO<'R, 'E>) : FIO<'R, 'E> =
    let rec loop (leftFiber : InternalFiber) (rightFiber : InternalFiber) =
        if leftFiber.Completed() then leftFiber
        else if rightFiber.Completed() then rightFiber
        else loop leftFiber rightFiber
    concurrently leftEffect >> fun leftFiber ->
    concurrently rightEffect >> fun rightFiber ->
    match (loop (leftFiber.ToInternal()) (rightFiber.ToInternal())).Await() with
    | Ok result -> Success (result :?> 'R)
    | Error error -> Failure (error :?> 'E)

// TODO: It would be nice if we could put this into its own module.
module internal FIOBuilderHelper =
    let rec bind (result : 'R1) (continuation : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        // This is kind of a hack. The Success here should not be necessary.
        // Let's look into this later.
        Success <| result >> continuation

    let fioReturn (result : 'R) : FIO<'R, 'E> =
        Success result

    let zero () : FIO<Unit, 'E> =
        Success ()

type FIOBuilder () =
    member _.Bind(effect, continuation) = FIOBuilderHelper.bind effect continuation
    member _.Return(result) = FIOBuilderHelper.fioReturn result
    member _.ReturnForm(result) = result

let fio = FIOBuilder ()