// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

module FIO =

    type Channel<'R> private (bc : BlockingCollection<obj>) =
        new() = Channel(new BlockingCollection<obj>())
        member _.Add(value : 'R) = bc.Add value
        member _.Take() : 'R = bc.Take() :?> 'R
        member internal _.Upcast() = Channel<obj>(bc)

    type LowLevelFiber internal (bc : BlockingCollection<Result<obj, obj>>) =
        let mutable completed = false
        member internal _.Complete(res : Result<obj, obj>) =
            match completed with
            | false -> completed <- true
                       bc.Add res
            | true  -> failwith "LowLevelFiber: Complete was called on an already completed LowLevelFiber!"
        member internal _.Await() : Result<obj, obj> = bc.Take()

    type Fiber<'R, 'E> private (bc : BlockingCollection<Result<obj, obj>>) =
        new() = Fiber(new BlockingCollection<Result<obj, obj>>())
        member internal _.ToLowLevel() = LowLevelFiber(bc)
        member _.Await() : Result<'R, 'E> =
            match bc.Take() with
            | Ok res    -> Ok (res :?> 'R)
            | Error err -> Error (err :?> 'E)

    and FIO<'R, 'E> =
        | NonBlocking of action: (unit -> Result<'R, 'E>)
        | Blocking of chan: Channel<'R>
        | Concurrent of fio: FIO<obj, obj> * fiber: obj * llfiber: LowLevelFiber
        | Await of llfiber: LowLevelFiber
        | Sequence of fio: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
        | Success of result: 'R
        | Failure of error: 'E

        member internal this.UpcastResult<'R, 'E>() : FIO<obj, 'E> =
            match this with
            | NonBlocking action               -> NonBlocking(fun () ->
                                                      match action() with
                                                      | Ok res    -> Ok (res :> obj)
                                                      | Error err -> Error err)
            | Blocking chan                    -> Blocking (chan.Upcast())
            | Concurrent (fio, fiber, llfiber) -> Concurrent (fio, fiber, llfiber)
            | Await llfiber                    -> Await llfiber
            | Sequence (fio, cont)             -> Sequence (fio, fun res -> (cont res).UpcastResult())
            | Success res                      -> Success (res :> obj)
            | Failure err                      -> Failure err

        member internal this.UpcastError<'R, 'E>() : FIO<'R, obj> =
            match this with
            | NonBlocking action               -> NonBlocking(fun () ->
                                                      match action() with
                                                      | Ok res    -> Ok res
                                                      | Error err -> Error (err :> obj))
            | Blocking chan                    -> Blocking chan
            | Concurrent (fio, fiber, llfiber) -> Concurrent (fio, fiber, llfiber)
            | Await llfiber                    -> Await llfiber
            | Sequence (fio, cont)             -> Sequence (fio.UpcastError(), fun res -> (cont res).UpcastError())
            | Success res                      -> Success res
            | Failure err                      -> Failure (err :> obj)

        member internal this.Upcast<'R, 'E>() : FIO<obj, obj> =
            this.UpcastResult().UpcastError()

    let Send<'V, 'E>(value : 'V, chan : Channel<'V>) : FIO<Unit, 'E> =
        NonBlocking <| fun () -> Ok <| chan.Add value

    let Receive<'R, 'E>(chan : Channel<'R>) : FIO<'R, 'E> =
        Blocking chan

    let (>>) (fio : FIO<'R1, 'E>) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Sequence (fio.UpcastResult(), fun res -> cont (res :?> 'R1))

    let Spawn<'R1, 'E1, 'E>(fio : FIO<'R1, 'E1>) : FIO<Fiber<'R1, 'E1>, 'E> =
        let fiber = new Fiber<'R1, 'E1>()
        Concurrent (fio.Upcast(), fiber, fiber.ToLowLevel())

    let Await<'R, 'E>(fiber : Fiber<'R, 'E>) : FIO<'R, 'E> =
        Await <| fiber.ToLowLevel()

    let Succeed<'R, 'E>(res : 'R) : FIO<'R, 'E> =
        Success res

    let Fail<'R, 'E>(err : 'E) : FIO<'R, 'E> =
        Failure err

    let End<'E>() : FIO<Unit, 'E> =
        Success ()
    
    let Parallel<'R1, 'R2, 'E>(fio1 : FIO<'R1, 'E>, fio2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        Spawn(fio1) >> fun fiber1 ->
        Spawn(fio2) >> fun fiber2 ->
        Await(fiber1) >> fun res1 ->
        Await(fiber2) >> fun res2 ->
        Success (res1, res2)

    let OrElse<'R, 'E>(fio : FIO<'R, 'E>, elseFIO : FIO<'R, 'E>) : FIO<'R, 'E> =
        Spawn(fio) >> fun fiber ->
        match fiber.Await() with
        | Ok res  -> Success res
        | Error _ -> Spawn(elseFIO) >> fun fiber ->
                     Await(fiber) >> fun res ->
                     Success res

    let OnError<'R, 'E1, 'E>(fio : FIO<'R, 'E1>, cont : 'E1 -> FIO<'R, 'E>) =
        Spawn(fio) >> fun fiber ->
        match fiber.Await() with
        | Ok res    -> Success res
        | Error err -> cont err

    let Race<'R, 'E>(fio1 : FIO<'R, 'E>, fio2 : FIO<'R, 'E>) : FIO<'R, 'E> =
        Spawn(fio1) >> fun fiber1 ->
        Spawn(fio2) >> fun fiber2 ->
        let task1 = Task.Factory.StartNew(fun () -> fiber1.Await())
        let task2 = Task.Factory.StartNew(fun () -> fiber2.Await())
        let task = Task.WhenAny [task1; task2]
        match task.Result.Result with
        | Ok res    -> Success res
        | Error err -> Failure err
