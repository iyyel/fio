// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

module FIO =

    type Channel<'V> private (bc : BlockingCollection<obj>) =
        new() = Channel(new BlockingCollection<obj>())
        member _.Add(value : 'V) = bc.Add(value)
        member _.Take() : 'V = bc.Take() :?> 'V
        member _.Count() = bc.Count
        member internal _.Upcast() = Channel<obj>(bc)

    type Fiber<'R, 'E>(fio : FIO<'R, 'E>, interpret : FIO<'R, 'E> -> Result<'R, 'E>) =
        let task = Task.Factory.StartNew(fun () -> interpret fio)
        member _.Await() = task.Result
        member internal _.Task() = task

    and FIO<'R, 'E> =
        | NonBlocking of action: (unit -> Result<'R, 'E>)
        | Blocking of chan: Channel<'R>
        | Concurrent of eff: FIO<obj, 'E> * cont: (Fiber<obj, 'E> -> FIO<'R, 'E>)
        | Await of fiber: Fiber<obj, 'E> * cont: (Result<obj, 'E> -> FIO<'R, 'E>)
        | Sequence of eff: FIO<obj, 'E> * cont: (obj -> FIO<'R, 'E>)
        | Success of result: 'R
        | Failure of error: 'E

    let rec internal upcastResult<'R, 'E>(fio : FIO<'R, 'E>) : FIO<obj, 'E> =
        match fio with
        | NonBlocking action     -> NonBlocking(fun () ->
                                        match action() with
                                        | Ok res    -> Ok (res :> obj)
                                        | Error err -> Error err)
        | Blocking chan          -> Blocking (chan.Upcast())
        | Concurrent (fio, cont) -> Concurrent (fio, fun fiber -> upcastResult <| cont fiber)
        | Await (fiber, cont)    -> Await (fiber, fun res -> upcastResult <| cont res)
        | Sequence (fio, cont)   -> Sequence (fio, fun res -> upcastResult <| cont res)
        | Success res            -> Success (res :> obj)
        | Failure err            -> Failure err

    let rec internal upcastError<'R, 'E>(fio : FIO<'R, 'E>) : FIO<'R, obj> =
        match fio with
        | NonBlocking action     -> NonBlocking(fun () ->
                                        match action() with
                                        | Ok res    -> Ok res
                                        | Error err -> Error (err :> obj))
        | Blocking chan          -> Blocking chan
     // | Concurrent (fio, cont) -> Concurrent (upcastError fio, fun fiber -> failwith "") // Why is fiber : Fiber<obj, obj> when it should be Fiber<obj, 'E>?
     // | Await (fiber, cont)    -> Await (fiber, fun res -> upcastError <| cont res)
        | Sequence (fio, cont)   -> Sequence (upcastError fio, fun res -> upcastError <| cont res)
        | Success res            -> Success res
        | Failure err            -> Failure (err :> obj)

    let internal upcastBoth<'R, 'E>(fio : FIO<'R, 'E>) : FIO<obj, obj> =
        upcastResult <| upcastError fio

    let Send<'V, 'E>(value : 'V, chan : Channel<'V>) : FIO<Unit, 'E> =
        NonBlocking (fun () -> Ok <| chan.Add(value))

    let Receive<'R, 'E>(chan : Channel<'R>) : FIO<'R, 'E> =
        Blocking chan

    let (>>) (fio : FIO<'R1, 'E>) (cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Sequence (upcastResult fio, fun res -> cont (res :?> 'R1))

    let Succeed<'R, 'E>(res : 'R) : FIO<'R, 'E> =
        Success res

    let Fail<'R, 'E>(err : 'E) : FIO<'R, 'E> =
        Failure err

    let End<'E>() : FIO<Unit, 'E> =
        Success ()
    
    let Parallel<'R1, 'R2, 'E>(fio1 : FIO<'R1, 'E>, fio2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        Concurrent(upcastResult fio1, fun fiber1 ->
            Concurrent(upcastResult fio2, fun fiber2 ->
                Await(fiber1, fun res1 ->
                    Await(fiber2, fun res2 ->
                        match res1, res2 with
                        | Ok res1, Ok res2 -> Success (res1 :?> 'R1, res2 :?> 'R2)
                        | Error err, _     -> Failure err
                        | _, Error err     -> Failure err
                        ))))
    
    let AwaitFIO<'R, 'R1, 'E>(fio : FIO<'R1, 'E>, contSucc : 'R1 -> FIO<'R, 'E>, contErr : 'E -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Concurrent(upcastResult fio, fun fiber ->
            Await(fiber, fun res ->
                match res with
                | Ok res    -> contSucc (res :?> 'R1)
                | Error err -> contErr err))

    let SequenceFunc<'R, 'R1, 'E>(fio : FIO<'R1, 'E>, cont : 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        AwaitFIO(fio, (fun res -> AwaitFIO(cont res,
                                      (fun res -> Success res),
                                      (fun err -> Failure err))),
                      (fun err -> Failure err))

    let OrElse<'R, 'E>(fio : FIO<'R, 'E>, elseFIO : FIO<'R, 'E>) : FIO<'R, 'E> =
        AwaitFIO(fio, (fun res -> Success res),
                      (fun _   -> AwaitFIO(elseFIO,
                                      (fun res -> Success res),
                                      (fun err -> Failure err))))

(*
    let OnError<'R, 'E, 'E1>(fio : FIO<'R, 'E1>, cont : 'E1 -> FIO<'R, 'E>) =
        AwaitFIO(fio, (fun res -> Success res),
                      (fun err -> AwaitFIO(cont err,
                                      (fun res -> Success res), 
                                      (fun err -> Failure err))))
*)

    let Race<'R, 'E>(fio1 : FIO<'R, 'E>, fio2 : FIO<'R, 'E>) : FIO<'R, 'E> =
        Concurrent(upcastResult fio1, fun fiber1 ->
            Concurrent(upcastResult fio2, fun fiber2 ->
                let task = Task.WhenAny([fiber1.Task(); fiber2.Task()])
                match task.Result.Result with
                | Ok res    -> Success (res :?> 'R)
                | Error err -> Failure err))
        

                