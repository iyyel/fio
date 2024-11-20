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
    | SuccHandler of succCont: (obj -> FIO<obj, obj>)
    | ErrorHandler of errCont: (obj -> FIO<obj, obj>)

and internal WorkItem =
    { Eff: FIO<obj, obj>; Stack: List<StackFrame>; IFiber: InternalFiber; PrevAction: Action }
    static member Create eff stack ifiber prevAction =
        { Eff = eff; Stack = stack; IFiber = ifiber; PrevAction = prevAction }
    member this.Complete res =
        this.IFiber.Complete <| res

/// A channel represents a communication queue that holds
/// data of the type ('R). Data can be both be sent and 
/// retrieved (blocking) on a channel.
and Channel<'R> private (
    chan: BlockingCollection<obj>,
    blockingWorkItems: BlockingCollection<WorkItem>,
    dataCounter: int64 ref) =
    new() = Channel(new BlockingCollection<obj>(),
                    new BlockingCollection<WorkItem>(),
                    ref 0)
    member internal _.AddBlockingWorkItem workItem =
        blockingWorkItems.Add workItem
    member internal _.RescheduleBlockingWorkItem (workItemQueue: BlockingCollection<WorkItem>) =
        if blockingWorkItems.Count > 0 then
            workItemQueue.Add <| blockingWorkItems.Take()
    member internal _.HasBlockingWorkItems() =
        blockingWorkItems.Count > 0
    member internal _.Upcast() = Channel<obj>(chan, blockingWorkItems, dataCounter)
    member internal _.UseAvailableData() =
        Interlocked.Decrement dataCounter |> ignore
    member internal _.DataAvailable() =
        let mutable temp = !dataCounter
        Interlocked.Read &temp > 0
    member _.Add (value: 'R) =
        Interlocked.Increment dataCounter |> ignore
        chan.Add value
    member _.Take() : 'R = chan.Take() :?> 'R
    member _.Count() = chan.Count

and internal InternalFiber internal (
    chan: BlockingCollection<Result<obj, obj>>,
    blockingWorkItems: BlockingCollection<WorkItem>,
    completed: int64 ref) =
    member internal _.Complete res =
        let mutable temp = !completed
        if Interlocked.Read &temp = 0 then
            Interlocked.Exchange(completed, 1) |> ignore
            chan.Add res
        else
            failwith "InternalFiber: Complete was called on an already completed InternalFiber!"
    member internal _.Await() =
        let res = chan.Take()
        chan.Add res
        res
    member internal _.Completed() =
        let mutable temp = !completed
        Interlocked.Read &temp = 1
    member internal _.AddBlockingWorkItem workItem =
        blockingWorkItems.Add workItem
    member internal _.BlockingWorkItemsCount() =
        blockingWorkItems.Count
    member internal _.RescheduleBlockingWorkItems (workItemQueue: BlockingCollection<WorkItem>) =
        while blockingWorkItems.Count > 0 do
            workItemQueue.Add <| blockingWorkItems.Take()

/// A Fiber is a construct that represents a lightweight-thread.
/// Fibers are used to execute multiple effects in parallel and
/// can be awaited to retrieve the result of the effect.
and Fiber<'R, 'E> private (
    chan: BlockingCollection<Result<obj, obj>>,
    blockingWorkItems: BlockingCollection<WorkItem>) =
    let completed : int64 ref = ref 0
    new() = Fiber(new BlockingCollection<Result<obj, obj>>(),
                    new BlockingCollection<WorkItem>())
    member internal _.ToInternal() = InternalFiber(chan, blockingWorkItems, completed)
    member _.Await() : Result<'R, 'E> =
        let res = chan.Take()
        chan.Add res
        match res with
        | Ok res -> Ok (res :?> 'R)
        | Error err -> Error (err :?> 'E)
    
/// FIO is an effect that can either succeed with a result ('R)
/// or fail with an error ('E) when interpreted.
and FIO<'R, 'E> =
    internal
    | NonBlocking of action: (unit -> Result<'R, 'E>)
    | Blocking of chan: Channel<'R>
    | SendMessage of msg: 'R * chan: Channel<'R>
    | Concurrent of effect: FIO<obj, obj> * fiber: obj * ifiber: InternalFiber
    | AwaitFiber of ifiber: InternalFiber
    | SequenceSuccess of effect: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
    | SequenceError of FIO<obj, obj> * cont: (obj -> FIO<'R, 'E>)
    | Success of result: 'R
    | Failure of error: 'E

    member internal this.UpcastResult<'R, 'E>() : FIO<obj, 'E> =
        match this with
        | NonBlocking action ->
            NonBlocking <| fun () ->
            match action () with
            | Ok res -> Ok (res :> obj)
            | Error err -> Error err
        | Blocking chan -> 
            Blocking <| chan.Upcast()
        | SendMessage (msg, chan) ->
            SendMessage (msg :> obj, chan.Upcast())
        | Concurrent (eff, fiber, ifiber) ->
            Concurrent (eff, fiber, ifiber)
        | AwaitFiber ifiber ->
            AwaitFiber ifiber
        | SequenceSuccess (eff, cont) ->
            SequenceSuccess (eff, fun res -> (cont res).UpcastResult())
        | SequenceError (eff, cont) ->
            SequenceError (eff, fun res -> (cont res).UpcastResult())
        | Success res ->
            Success (res :> obj)
        | Failure err ->
            Failure err

    member internal this.UpcastError<'R, 'E>() : FIO<'R, obj> =
        match this with
        | NonBlocking action ->
            NonBlocking <| fun () ->
            match action () with
            | Ok res -> Ok res
            | Error err -> Error (err :> obj)
        | Blocking chan ->
            Blocking chan
        | SendMessage (value, chan) ->
            SendMessage (value, chan)
        | Concurrent (eff, fiber, ifiber) ->
            Concurrent (eff, fiber, ifiber)
        | AwaitFiber ifiber ->
            AwaitFiber ifiber
        | SequenceSuccess (eff, cont) ->
            SequenceSuccess (eff.UpcastError(), fun res -> (cont res).UpcastError())
        | SequenceError (eff, cont) ->
            SequenceError (eff.UpcastError(), fun res -> (cont res).UpcastError())
        | Success res ->
            Success res
        | Failure err ->
            Failure (err :> obj)
            
    member internal this.Upcast<'R, 'E>() : FIO<obj, obj> =
        this.UpcastResult().UpcastError()

/// Transforms the expression (func) into a FIO. OBS: This has temporarily been renamed to fioZ to not conflict with the F# fio computation expression.
let fioZ<'R, 'E> (func : Unit -> 'R) : FIO<'R, 'E> =
    NonBlocking (fun _ -> Ok (func ()))

/// succeed creates an effect that models succeeding with the given value res.
/// succeed is identical to the Success opcode.
let succeed<'R, 'E> (result : 'R) : FIO<'R, 'E> =
    Success result

/// fail creates an effect that models failure with the given value err.
/// fail is identical to the Failure opcode.
let fail<'R, 'E> (error : 'E) : FIO<'R, 'E> =
    Failure error

/// stop creates an effect that models the end of an effect
/// by succeeding with Unit (Success ()).
let stop<'E> : FIO<Unit, 'E> =
    Success ()

/// send sends a message (value) on the given channel (chan).
let send<'R, 'E> (value : 'R) (chan : Channel<'R>) : FIO<'R, 'E> =
    SendMessage (value, chan)
        
/// receive creates a blocking effect that awaits data retrieval on the given channel chan.
let receive<'R, 'E> (chan : Channel<'R>) : FIO<'R, 'E> =
    Blocking chan

/// Spawn creates an effect that executes the given effect eff concurrently and returns the corresponding fiber.
let spawn<'R1, 'E1, 'E> (eff : FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
    let fiber = new Fiber<'R1, 'E1>()
    Concurrent (eff.Upcast(), fiber, fiber.ToInternal())

/// Await creates a blocking effect that awaits the result of the given fiber.
let await<'R, 'E> (fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
    AwaitFiber <| fiber.ToInternal()

let (>>) (eff : FIO<'R1, 'E>) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    SequenceSuccess (eff.UpcastResult(), fun res -> cont (res :?> 'R1))

let (|||) (eff1 : FIO<'R1, 'E>) (eff2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    spawn eff1 >> fun fiber1 ->
    eff2 >> fun res2 ->
    await fiber1 >> fun res1 ->
    Success (res1, res2)
    
let (|||*) (eff1 : FIO<'R1, 'E>) (eff2 : FIO<'R2, 'E>) : FIO<Unit, 'E> =
    eff1 ||| eff2
    >> fun (_, _) ->
    stop

/// attempt attempts to interpret the (eff) effect but if an error occurs
/// the error is passed to the continuation.
let attempt<'R, 'E1, 'E> (eff : FIO<'R, 'E1>) (cont : 'E1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
    SequenceError (eff.Upcast(), fun err -> cont (err :?> 'E1))

/// zip returns the results of (eff1) and (eff2) in a tuple.
/// Errors are returned immediately.
let zip<'R1, 'R2, 'E> (eff1 : FIO<'R1, 'E>) (eff2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
    eff1 >> fun res1 ->
    eff2 >> fun res2 ->
    Success (res1, res2)

/// race models the parallel execution of two effects (eff1) and (eff2)
/// where the result of the effect that completes first is returned.
let race<'R, 'E> (eff1 : FIO<'R, 'E>) (eff2 : FIO<'R, 'E>) : FIO<'R, 'E> =
    let rec loop (fiber1 : InternalFiber) (fiber2 : InternalFiber) =
        if fiber1.Completed() then fiber1
        else if fiber2.Completed() then fiber2
        else loop fiber1 fiber2
    spawn eff1 >> fun fiber1 ->
    spawn eff2 >> fun fiber2 ->
    match (loop (fiber1.ToInternal()) (fiber2.ToInternal())).Await() with
    | Ok res -> Success (res :?> 'R)
    | Error err -> Failure (err :?> 'E)


// TODO: It would be nice if we could put this into its own module.
module internal FIOBuilderHelper =
    
    let rec bind (res : 'R1) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        // This is kind of a hack. The Success here should not be necessary.
        // Let's look into this later.
        Success <| res >> cont

    let fioReturn (res : 'R) : FIO<'R, 'E> =
        Success res

    let zero () : FIO<Unit, 'E> =
        Success ()

type FIOBuilder () =
    member _.Bind(eff, cont) = FIOBuilderHelper.bind eff cont
    member _.Return(res) = FIOBuilderHelper.fioReturn res
    member _.ReturnForm(res) = res

let fio = FIOBuilder ()