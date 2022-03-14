// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

module FIO =

    type Channel<'R> private (chan : BlockingCollection<obj>) =
        new() = Channel(new BlockingCollection<obj>())
        member _.Add(value : 'R) = chan.Add value
        member _.Take() : 'R = chan.Take() :?> 'R
        member internal _.Upcast() = Channel<obj> chan

    type LowLevelFiber internal (chan : BlockingCollection<Result<obj, obj>>) =
        let mutable completed = false
        member internal _.Complete(res : Result<obj, obj>) =
            match completed with
            | false -> completed <- true
                       chan.Add res
            | true  -> failwith "LowLevelFiber: Complete was called on an already completed LowLevelFiber!"
        member internal _.Await() : Result<obj, obj> = chan.Take()

    and Fiber<'R, 'E> private (chan : BlockingCollection<Result<obj, obj>>) =
        new() = Fiber(new BlockingCollection<Result<obj, obj>>())
        member internal _.ToLowLevel() = LowLevelFiber chan
        member _.Await() : Result<'R, 'E> =
            match chan.Take() with
            | Ok res    -> Ok (res :?> 'R)
            | Error err -> Error (err :?> 'E)

    and FIO<'R, 'E> =
        | NonBlocking of action: (unit -> Result<'R, 'E>)
        | Blocking of chan: Channel<'R>
        | Concurrent of effect: FIO<obj, obj> * fiber: obj * llfiber: LowLevelFiber
        | Await of llfiber: LowLevelFiber
        | Sequence of effect: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
        | Success of result: 'R
        | Failure of error: 'E

        member internal this.UpcastResult<'R, 'E>() : FIO<obj, 'E> =
            match this with
            | NonBlocking action               -> NonBlocking <| fun () ->
                                                  match action() with
                                                  | Ok res    -> Ok (res :> obj)
                                                  | Error err -> Error err
            | Blocking chan                    -> Blocking <| chan.Upcast()
            | Concurrent (eff, fiber, llfiber) -> Concurrent (eff, fiber, llfiber)
            | Await llfiber                    -> Await llfiber
            | Sequence (eff, cont)             -> Sequence (eff, fun res -> (cont res).UpcastResult())
            | Success res                      -> Success (res :> obj)
            | Failure err                      -> Failure err

        member internal this.UpcastError<'R, 'E>() : FIO<'R, obj> =
            match this with
            | NonBlocking action               -> NonBlocking <| fun () ->
                                                  match action() with
                                                  | Ok res    -> Ok res
                                                  | Error err -> Error (err :> obj)
            | Blocking chan                    -> Blocking chan
            | Concurrent (eff, fiber, llfiber) -> Concurrent (eff, fiber, llfiber)
            | Await llfiber                    -> Await llfiber
            | Sequence (eff, cont)             -> Sequence (eff.UpcastError(), fun res -> (cont res).UpcastError())
            | Success res                      -> Success res
            | Failure err                      -> Failure (err :> obj)

        member internal this.Upcast<'R, 'E>() : FIO<obj, obj> =
            this.UpcastResult().UpcastError()

    let Send<'V, 'E>(value : 'V, chan : Channel<'V>) : FIO<Unit, 'E> =
        NonBlocking <| fun () -> Ok <| chan.Add value

    let Receive<'R, 'E>(chan : Channel<'R>) : FIO<'R, 'E> =
        Blocking chan

    let (>>) (eff : FIO<'R1, 'E>) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Sequence (eff.UpcastResult(), fun res -> cont (res :?> 'R1))

    let Spawn<'R1, 'E1, 'E>(eff : FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
        let fiber = new Fiber<'R1, 'E1>()
        Concurrent (eff.Upcast(), fiber, fiber.ToLowLevel())

    let Await<'R, 'E>(fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
        Await <| fiber.ToLowLevel()

    let Succeed<'R, 'E>(res : 'R) : FIO<'R, 'E> =
        Success res

    let Fail<'R, 'E>(err : 'E) : FIO<'R, 'E> =
        Failure err

    let End<'E>() : FIO<Unit, 'E> =
        Success ()
    
    let Parallel<'R1, 'R2, 'E>(eff1 : FIO<'R1, 'E>, eff2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        Spawn eff1 >> fun fiber1 ->
        Spawn eff2 >> fun fiber2 ->
        Await fiber1 >> fun res1 ->
        Await fiber2 >> fun res2 ->
        Success (res1, res2)

    let OrElse<'R, 'E>(eff : FIO<'R, 'E>, elseEff : FIO<'R, 'E>) : FIO<'R, 'E> =
        Spawn eff >> fun fiber ->
        match fiber.Await() with
        | Ok res  -> Success res
        | Error _ -> Spawn elseEff >> fun fiber ->
                     Await fiber >> fun res ->
                     Success res

    let OnError<'R, 'E1, 'E>(eff : FIO<'R, 'E1>, cont : 'E1 -> FIO<'R, 'E>) =
        Spawn eff >> fun fiber ->
        match fiber.Await() with
        | Ok res    -> Success res
        | Error err -> cont err

    let Race<'R, 'E>(eff1 : FIO<'R, 'E>, eff2 : FIO<'R, 'E>) : FIO<'R, 'E> =
        Spawn eff1 >> fun fiber1 ->
        Spawn eff2 >> fun fiber2 ->
        let task1 = Task.Factory.StartNew(fun () -> fiber1.Await())
        let task2 = Task.Factory.StartNew(fun () -> fiber2.Await())
        let task = Task.WhenAny [task1; task2]
        match task.Result.Result with
        | Ok res    -> Success res
        | Error err -> Failure err
