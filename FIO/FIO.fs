// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

module FIO =

    type Channel<'R> private (bc : BlockingCollection<obj>) =
        new() = Channel(new BlockingCollection<obj>())
        member _.Add(value : 'R) = bc.Add(value)
        member _.Take() : 'R = bc.Take() :?> 'R
        member _.Count() = bc.Count
        member internal _.Upcast() = Channel<obj>(bc)

    type Fiber<'R, 'E>(fio : FIO<'R, 'E>, lowLevelEval : FIO<obj, obj> -> Result<obj, obj>) =
        let task = Task.Factory.StartNew(fun () -> lowLevelEval <| fio.upcastBoth())
        member _.Await() : Result<obj, obj> = task.Result
        member internal _.Task() : Task<Result<obj, obj>> = task
        member internal _.UpcastResult() = Fiber<obj, 'E>(fio.upcastResult(), lowLevelEval)
        member internal _.UpcastError() = Fiber<'R, obj>(fio.upcastError(), lowLevelEval)

    and FIO<'R, 'E> =
        | NonBlocking of action: (unit -> Result<'R, 'E>)
        | Blocking of chan: Channel<'R>
        | Concurrent of fio: FIO<obj, obj>
        | Await of fiber: Fiber<'R, 'E>
        | Sequence of fio: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
        | Success of result: 'R
        | Failure of error: 'E

        member internal this.upcastResult<'R, 'E>() : FIO<obj, 'E> =
            match this with
            | NonBlocking action   -> NonBlocking(fun () ->
                                          match action() with
                                          | Ok res    -> Ok (res :> obj)
                                          | Error err -> Error err)
            | Blocking chan        -> Blocking (chan.Upcast())
            | Concurrent fio       -> Concurrent fio
            | Await fiber          -> Await (fiber.UpcastResult())
            | Sequence (fio, cont) -> Sequence (fio, fun res -> (cont res).upcastResult())
            | Success res          -> Success (res :> obj)
            | Failure err          -> Failure err

        member internal this.upcastError<'R, 'E>() : FIO<'R, obj> =
            match this with
            | NonBlocking action   -> NonBlocking(fun () ->
                                          match action() with
                                          | Ok res    -> Ok res
                                          | Error err -> Error (err :> obj))
            | Blocking chan        -> Blocking chan
            | Concurrent fio       -> Concurrent fio
            | Await fiber          -> Await (fiber.UpcastError())
            | Sequence (fio, cont) -> Sequence (fio.upcastError(), fun res -> (cont res).upcastError())
            | Success res          -> Success res
            | Failure err          -> Failure (err :> obj)

        member internal this.upcastBoth<'R, 'E>() : FIO<obj, obj> =
            this.upcastResult().upcastError()

    let Send<'V, 'E>(value : 'V, chan : Channel<'V>) : FIO<Unit, 'E> =
        NonBlocking (fun () -> Ok <| chan.Add(value))

    let Receive<'R, 'E>(chan : Channel<'R>) : FIO<'R, 'E> =
        Blocking chan

    let (>>) (fio : FIO<'R1, 'E>) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Sequence(fio.upcastResult(), fun res -> cont (res :?> 'R1))

    let Succeed<'R, 'E>(res : 'R) : FIO<'R, 'E> =
        Success res

    let Fail<'R, 'E>(err : 'E) : FIO<'R, 'E> =
        Failure err

    let End<'E>() : FIO<Unit, 'E> =
        Success ()
    
    let Parallel<'R1, 'R2, 'E>(fio1 : FIO<'R1, 'E>, fio2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        Concurrent(fio1.upcastBoth()) >> fun fiber1 ->
        Concurrent(fio2.upcastBoth()) >> fun fiber2 ->
        Await(fiber1) >> fun res1 ->
        Await(fiber2) >> fun res2 ->
        match res1, res2 with
        | Ok res1, Ok res2 -> Success (res1, res2)
        | Error err, _     -> Failure err
        | _, Error err     -> Failure err

    let AwaitFIO<'R, 'R1, 'E>(fio : FIO<'R1, 'E>, contSucc : 'R1 -> FIO<'R, 'E>, contErr : 'E -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Concurrent(fio.upcastBoth()) >> fun fiber ->
        Await(fiber) >> fun res ->
        match res with
        | Ok res    -> contSucc res
        | Error err -> contErr err

    let SequenceFunc<'R, 'R1, 'E>(fio : FIO<'R1, 'E>, cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        AwaitFIO(fio, 
            (fun res -> AwaitFIO(cont res,
                            (fun res -> Success res),
                            (fun err -> Failure err))),
            (fun err -> Failure err))

    let OrElse<'R, 'E>(fio : FIO<'R, 'E>, elseFIO : FIO<'R, 'E>) : FIO<'R, 'E> =
        AwaitFIO(fio, 
            (fun res -> Success res),
            (fun _   -> AwaitFIO(elseFIO,
                            (fun res -> Success res),
                            (fun err -> Failure err))))

    let OnError<'R, 'E>(fio : FIO<'R, 'E>, cont : 'E -> FIO<'R, 'E>) =
        AwaitFIO(fio, 
            (fun res -> Success res),
            (fun err -> AwaitFIO(cont err,
                            (fun res -> Success res), 
                            (fun err -> Failure err))))

    let Race<'R, 'E>(fio1 : FIO<'R, 'E>, fio2 : FIO<'R, 'E>) : FIO<'R, 'E> =
        Concurrent(fio1.upcastBoth()) >> fun (fiber1 : Fiber<'R, 'E>) ->
        Concurrent(fio2.upcastBoth()) >> fun (fiber2 : Fiber<'R, 'E>) ->
        let task = Task.WhenAny([fiber1.Task(); fiber2.Task()])
        match task.Result.Result with
        | Ok res    -> Success (res :?> 'R)
        | Error err -> Failure (err :?> 'E)
