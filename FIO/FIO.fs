(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FSharp.FIO

open System.Threading
open System.Collections.Concurrent

[<AutoOpen>]
module FIO =

    type internal Action =
        | RescheduleForRunning
        | RescheduleForBlocking of BlockingItem
        | Evaluated

    and internal BlockingItem =
        | BlockingChannel of Channel<obj>
        | BlockingFiber of LowLevelFiber

    and internal WorkItem =
        { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber; PrevAction: Action }
        static member Create eff llfiber prevAction =
            { Eff = eff; LLFiber = llfiber; PrevAction = prevAction }
        member this.Complete res =
            this.LLFiber.Complete <| res

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
            Interlocked.Read dataCounter > 0
        member _.Add (value: 'R) =
            Interlocked.Increment dataCounter |> ignore
            chan.Add value
        member _.Take() : 'R = chan.Take() :?> 'R
        member _.Count() = chan.Count

    and internal LowLevelFiber internal (
        chan: BlockingCollection<Result<obj, obj>>,
        blockingWorkItems: BlockingCollection<WorkItem>,
        completed: int64 ref) =
        member internal _.Complete res =
            if Interlocked.Read completed = 0 then
                Interlocked.Exchange(completed, 1) |> ignore
                chan.Add res
            else
                failwith "LowLevelFiber: Complete was called on an already completed LowLevelFiber!"
        member internal _.Await() =
            let res = chan.Take()
            chan.Add res
            res
        member internal _.Completed() =
            Interlocked.Read completed = 1
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
        member internal _.ToLowLevel() = LowLevelFiber(chan, blockingWorkItems, completed)
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
        | Concurrent of effect: FIO<obj, obj> * fiber: obj * llfiber: LowLevelFiber
        | AwaitFiber of llfiber: LowLevelFiber
        | SequenceSuccess of effect: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
        | SequenceError of FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
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
            | Concurrent (eff, fiber, llfiber) ->
                Concurrent (eff, fiber, llfiber)
            | AwaitFiber llfiber ->
                AwaitFiber llfiber
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
            | Concurrent (eff, fiber, llfiber) ->
                Concurrent (eff, fiber, llfiber)
            | AwaitFiber llfiber ->
                AwaitFiber llfiber
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

        /// onError attempts to interpret this effect but if an error occurs
        /// (errorEff) is then attempted to be interpreted.
        member this.onError<'R, 'E> (errorEff : FIO<'R, 'E>) : FIO<'R, 'E> =
            SequenceError (this.UpcastResult(), fun _ -> errorEff)

    /// Transforms the expression (func) into a FIO.
    let fio<'R, 'E> (func : Unit -> 'R) : FIO<'R, 'E> =
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
        Concurrent (eff.Upcast(), fiber, fiber.ToLowLevel())

    /// Await creates a blocking effect that awaits the result of the given fiber.
    let await<'R, 'E> (fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
        AwaitFiber <| fiber.ToLowLevel()

    let (>>) (eff : FIO<'R1, 'E>) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        SequenceSuccess (eff.UpcastResult(), fun res -> cont (res :?> 'R1))

    let (|||) (eff1 : FIO<'R1, 'E>) (eff2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        spawn eff1 >> fun fiber1 ->
        spawn eff2 >> fun fiber2 ->
        await fiber1 >> fun res1 ->
        await fiber2 >> fun res2 ->
        Success (res1, res2)
    
    let (|||*) (eff1 : FIO<'R1, 'E>) (eff2 : FIO<'R2, 'E>) : FIO<Unit, 'E> =
        eff1 ||| eff2
        >> fun (_, _) ->
        stop

    let zip (eff1 : FIO<'R1, 'E>) (eff2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        eff1 >> fun res1 ->
        eff2 >> fun res2 ->
        succeed (res1, res2)

    /// race models the parallel execution of two effects (eff1) and (eff2)
    /// where the result of the effect that completes first is returned.
    let race<'R, 'E> (eff1 : FIO<'R, 'E>) (eff2 : FIO<'R, 'E>) : FIO<'R, 'E> =
        let rec loop (fiber1 : LowLevelFiber) (fiber2 : LowLevelFiber) =
            if fiber1.Completed() then fiber1
            else if fiber2.Completed() then fiber2
            else loop fiber1 fiber2
        spawn eff1 >> fun fiber1 ->
        spawn eff2 >> fun fiber2 ->
        match (loop (fiber1.ToLowLevel()) (fiber2.ToLowLevel())).Await() with
        | Ok res -> Success (res :?> 'R)
        | Error err -> Failure (err :?> 'E)