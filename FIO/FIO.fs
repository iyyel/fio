(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FSharp.FIO

open System
open System.Collections.Concurrent

module FIO =
    
    type Channel<'R> private (id: Guid, chan: BlockingCollection<obj>) =
        new() = Channel(Guid.NewGuid(), new BlockingCollection<obj>())
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
        member internal _.Upcast() = Channel<obj>(id, chan)
        member _.Add(value: 'R) = chan.Add value
        member _.Take() : 'R = chan.Take() :?> 'R
        member _.Count() = chan.Count

    type LowLevelFiber internal (id: Guid, chan: BlockingCollection<Result<obj, obj>>) =
        let _lock = obj()
        let mutable completed = false
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
        member internal _.Complete(res: Result<obj, obj>) =
            lock (_lock) (fun _ ->
                if not completed then
                    completed <- true
                    chan.Add res
                else
                    failwith "LowLevelFiber: Complete was called on an already completed LowLevelFiber!")
        member internal _.Await() : Result<obj, obj> = 
            let res = chan.Take()
            chan.Add res
            res
        member internal _.Completed() = chan.Count > 0

    and Fiber<'R, 'E> private (id: Guid, chan: BlockingCollection<Result<obj, obj>>) =
        new() = Fiber(Guid.NewGuid(), new BlockingCollection<Result<obj, obj>>())
        member internal _.ToLowLevel() = LowLevelFiber(id, chan)
        member _.Await() : Result<'R, 'E> = 
            let res = chan.Take()
            chan.Add res
            match res with
            | Ok res -> Ok (res :?> 'R)
            | Error err -> Error (err :?> 'E)
        
    and FIO<'R, 'E> =
        | NonBlocking of action: (unit -> Result<'R, 'E>)
        | Blocking of chan: Channel<'R>
        | Send of value: 'R * chan: Channel<'R>
        | Concurrent of effect: FIO<obj, obj> * fiber: obj * llfiber: LowLevelFiber
        | AwaitFiber of llfiber: LowLevelFiber
        | Sequence of effect: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
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
            | Send (value, chan) ->
                Send (value :> obj, chan.Upcast())
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
            | Send (value, chan) ->
                Send (value, chan)
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
            
        member internal this.Upcast<'R, 'E>() : FIO<obj, obj> = this.UpcastResult().UpcastError()
        
    let (>>) (eff: FIO<'R1, 'E>) (cont: 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Sequence (eff.UpcastResult(), fun res -> cont (res :?> 'R1))

    let (>>|) (eff: FIO<'R1, 'E>) (cont: 'E -> FIO<'R, 'E>) : FIO<'R, 'E> =
        SequenceError (eff.UpcastResult(), fun res -> cont (res :?> 'E))
        
    let Receive<'R, 'E> (chan: Channel<'R>) : FIO<'R, 'E> =
        Blocking chan

    let Spawn<'R1, 'E1, 'E> (eff: FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
        let fiber = new Fiber<'R1, 'E1>()
        Concurrent (eff.Upcast(), fiber, fiber.ToLowLevel())

    let Await<'R, 'E> (fiber: Fiber<'R, 'E>) : FIO<'R, 'E> =
        AwaitFiber <| fiber.ToLowLevel()

    let Succeed<'R, 'E> (res: 'R) : FIO<'R, 'E> =
        Success res

    let Fail<'R, 'E> (err: 'E) : FIO<'R, 'E> =
        Failure err

    let End<'E> () : FIO<Unit, 'E> =
        Success ()

    let Parallel<'R1, 'R2, 'E> (eff1: FIO<'R1, 'E>, eff2: FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        Spawn eff1 >> fun fiber1 ->
        eff2 >> fun res2 ->
        Await fiber1 >> fun res1 ->
        Success (res1, res2)

    let OnError<'R, 'E> (eff: FIO<'R, 'E>, elseEff: FIO<'R, 'E>) : FIO<'R, 'E> =
        eff >>| fun _ ->
        elseEff >> fun res -> 
        Success res

    let Race<'R, 'E> (eff1: FIO<'R, 'E>, eff2: FIO<'R, 'E>) : FIO<'R, 'E> =
        let rec loop (fiber1: LowLevelFiber) (fiber2: LowLevelFiber) =
            if fiber1.Completed() then fiber1
            else if fiber2.Completed() then fiber2
            else loop fiber1 fiber2
        Spawn eff1 >> fun fiber1 ->
        Spawn eff2 >> fun fiber2 ->
        match (loop (fiber1.ToLowLevel()) (fiber2.ToLowLevel())).Await() with
        | Ok res    -> Success (res :?> 'R)
        | Error err -> Failure (err :?> 'E)
        