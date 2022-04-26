(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FSharp.FIO

open System
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
        id: Guid,
        chan: BlockingCollection<obj>,
        blockingWorkItems: BlockingCollection<WorkItem>,
        dataCounter: int64 ref) =
        new() = Channel(Guid.NewGuid(),
                        new BlockingCollection<obj>(),
                        new BlockingCollection<WorkItem>(),
                        ref 0)
        interface IComparable with
            member this.CompareTo other =
                match other with
                | :? Channel<'R> as chan -> 
                    if this.Id = chan.Id then 1 else -1
                | _ -> -1
        interface System.IComparable<Channel<'R>> with
            member this.CompareTo other = this.Id.CompareTo other.Id.CompareTo
        override this.Equals other =
            match other with
            | :? Channel<'R> as chan -> this.Id = chan.Id
            | _ -> false
        override _.GetHashCode() = id.GetHashCode()
        member internal _.Id = id
        member internal _.AddBlockingWorkItem workItem =
            blockingWorkItems.Add workItem
        member internal _.RescheduleBlockingWorkItem (workItemQueue: BlockingCollection<WorkItem>) =
            if blockingWorkItems.Count > 0 then
                workItemQueue.Add <| blockingWorkItems.Take()
        member internal _.HasBlockingWorkItems() =
            blockingWorkItems.Count > 0
        member internal _.Upcast() = Channel<obj>(id, chan, blockingWorkItems, dataCounter)
        member _.Add (value: 'R) =
            Interlocked.Increment dataCounter |> ignore
            chan.Add value
        member _.Take() : 'R = chan.Take() :?> 'R
        member _.Count() = chan.Count
        member _.UseAvailableData() =
            Interlocked.Decrement dataCounter |> ignore
        member _.DataAvailable() =
            Interlocked.Read dataCounter > 0

    and internal LowLevelFiber internal (
        id: Guid,
        chan: BlockingCollection<Result<obj, obj>>,
        blockingWorkItems: BlockingCollection<WorkItem>,
        completed: int64 ref) =
        let _lock = obj()
        interface IComparable with
            member this.CompareTo other = 
                match other with
                | :? LowLevelFiber as fiber ->
                    if this.Id = fiber.Id then 1 else -1
                | _ -> -1
        interface System.IComparable<LowLevelFiber> with
            member this.CompareTo other = this.Id.CompareTo other.Id.CompareTo
        override this.Equals other = 
            match other with
            | :? LowLevelFiber as llfiber ->
                this.Id = llfiber.Id
            | _ -> false
        override _.GetHashCode() = id.GetHashCode()
        member internal _.Id = id
        member internal _.Complete res =
            lock _lock (fun _ ->
                if Interlocked.Read completed = 0 then
                    Interlocked.Exchange(completed, 1) |> ignore
                    chan.Add res
                else
                    failwith "LowLevelFiber: Complete was called on an already completed LowLevelFiber!")
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
        id: Guid, 
        chan: BlockingCollection<Result<obj, obj>>,
        blockingWorkItems: BlockingCollection<WorkItem>) =
        let completed : int64 ref = ref 0
        new() = Fiber(Guid.NewGuid(),
                      new BlockingCollection<Result<obj, obj>>(),
                      new BlockingCollection<WorkItem>())
        member internal _.ToLowLevel() = LowLevelFiber(id, chan, blockingWorkItems, completed)
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
          /// The NonBlocking effect models a
          /// non-blocking action (action)
        | NonBlocking of action: (unit -> Result<'R, 'E>)
          /// The Blocking effect models a blocking retrieval
          /// of data on the channel (chan)
        | Blocking of chan: Channel<'R>
          /// The SendMessage effect models the sending of data (msg)
          /// on the channel (chan)
        | SendMessage of msg: 'R * chan: Channel<'R>
          /// The Concurrent effect models the concurrent execution of 
          /// the effect (effect) in the fiber (fiber)
        | Concurrent of effect: FIO<obj, obj> * fiber: obj * llfiber: LowLevelFiber
          /// The AwaitFiber effect models the awaiting for the result
          /// of the given LowLevelFiber (llfiber)
        | AwaitFiber of llfiber: LowLevelFiber
          /// The Sequence effect models the sequencing of two effects,
          /// where the success value of the first effect is passed
          /// on to the second effect. If a failure occurs, this is
          /// returned immediately.
        | Sequence of effect: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
          /// The SequenceError effect models the sequencing of two effects,
          /// where the error value of the first effect is passed
          /// on to the second effect. If a success occurs, this is
          /// returned immediately.
        | SequenceError of FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
          /// The Success effect models the success of an effect
          /// with the value (result)
        | Success of result: 'R
          /// The Failure effect models the failure of an effect
          /// with the value (error)
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
            | Sequence (eff, cont) ->
                Sequence (eff, fun res -> (cont res).UpcastResult())
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
            | Sequence (eff, cont) ->
                Sequence (eff.UpcastError(), fun res -> (cont res).UpcastError())
            | SequenceError (eff, cont) ->
                SequenceError (eff.UpcastError(), fun res -> (cont res).UpcastError())
            | Success res ->
                Success res
            | Failure err ->
                Failure (err :> obj)
            
        member internal this.Upcast<'R, 'E>() : FIO<obj, obj> =
            this.UpcastResult().UpcastError()
        
    let (>>) (eff : FIO<'R1, 'E>) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Sequence (eff.UpcastResult(), fun res -> cont (res :?> 'R1))

    let (>>|) (eff : FIO<'R1, 'E>) (cont : 'E -> FIO<'R, 'E>) : FIO<'R, 'E> =
        SequenceError (eff.UpcastResult(), fun res -> cont (res :?> 'E))

    /// Encapsulate any kind of expressions into a FIO
    let IO<'R, 'E> (action : 'R) : FIO<'R, 'E> =
        NonBlocking (fun () -> Ok action)

    /// Send sends a message (value) on the given channel (chan)
    let Send<'R, 'E> (value : 'R) (chan : Channel<'R>) : FIO<'R, 'E> =
        SendMessage (value, chan)
        
    /// Receive creates a blocking effect that awaits data retrieval on the given channel chan
    let Receive<'R, 'E> (chan : Channel<'R>) : FIO<'R, 'E> =
        Blocking chan

    /// Spawn creates an effect that executes the given effect eff concurrently and returns the corresponding fiber
    let Spawn<'R1, 'E1, 'E> (eff : FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
        let fiber = new Fiber<'R1, 'E1>()
        Concurrent (eff.Upcast(), fiber, fiber.ToLowLevel())

    /// Await creates a blocking effect that awaits the result of the given fiber
    let Await<'R, 'E> (fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
        AwaitFiber <| fiber.ToLowLevel()

    /// Succeed creates an effect that models succeeding with the given value res.
    /// Succeed is identical to the Success effect
    let Succeed<'R, 'E> (res : 'R) : FIO<'R, 'E> =
        Success res

    /// Fail creates an effect that models failure with the given value err.
    /// Fail is identical to the Failure effect
    let Fail<'R, 'E> (err : 'E) : FIO<'R, 'E> =
        Failure err

    /// End creates an effect that models the end of an effect
    /// by succeeding with Unit (Success ())
    let End<'E> () : FIO<Unit, 'E> =
        Success ()

    /// Parallel models the parallel execution of effect (eff1) and
    /// (eff2) and returns their result in a tuple
    let Parallel<'R1, 'R2, 'E> (eff1 : FIO<'R1, 'E>) (eff2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        Spawn eff1 >> fun fiber1 ->
        eff2 >> fun res2 ->
        Await fiber1 >> fun res1 ->
        Success (res1, res2)

    /// Parallel models the parallel execution of effect (eff1) and
    /// (eff2) and returns their result in a tuple
    let OnError<'R, 'E> (eff : FIO<'R, 'E>) (elseEff : FIO<'R, 'E>) : FIO<'R, 'E> =
        eff >>| fun _ ->
        elseEff >> fun res -> 
        Success res

    /// Race models the parallel execution of two effects (eff1) and (eff2)
    /// where the result of the effect that completes first is returned
    let Race<'R, 'E> (eff1 : FIO<'R, 'E>) (eff2 : FIO<'R, 'E>) : FIO<'R, 'E> =
        let rec loop (fiber1 : LowLevelFiber) (fiber2 : LowLevelFiber) =
            if fiber1.Completed() then fiber1
            else if fiber2.Completed() then fiber2
            else loop fiber1 fiber2
        Spawn eff1 >> fun fiber1 ->
        Spawn eff2 >> fun fiber2 ->
        match (loop (fiber1.ToLowLevel()) (fiber2.ToLowLevel())).Await() with
        | Ok res -> Success (res :?> 'R)
        | Error err -> Failure (err :?> 'E)
