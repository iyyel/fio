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

    and Fiber<'R, 'E>(fio : FIO<'R, 'E>, interpret : FIO<'R, 'E> -> Result<'R, 'E>) =
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
        | Blocking chan -> Blocking chan
     // | Concurrent (fio, cont) -> Concurrent (upcastError fio, fun fiber -> upcastError <| cont fiber)
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

    let (>>) (fio : FIO<'R1, 'E>) (cont : ('R1 -> FIO<'R, 'E>)) : FIO<'R, 'E> =
        Sequence (upcastResult fio, fun (res : obj) -> cont (res :?> 'R1))

    let Succeed<'R, 'E>(res : 'R) : FIO<'R, 'E> =
        Success res

    let Fail<'R, 'E>(err : 'E) : FIO<'R, 'E> =
        Failure err

    let End<'E>() : FIO<Unit, 'E> =
        Success ()
    
    (*
    let Parallel<'R1, 'R2, 'E>(eff1 : FIO<'R1, 'E>, eff2 : FIO<'R2, 'E>) : FIO<'R1 * 'R2, 'E> =
        Concurrent(upcastResult eff1, fun fiber1 ->
            Concurrent(upcastResult eff2, fun fiber2 ->
                Await(fiber1, fun res1 ->
                    Await(fiber2, fun res2 ->
                        Success (res1, res2)))))
    *)

    (*
    let AwaitEffect<'FIOError, 'FIOResult, 'Error, 'Result>
        (eff : FIO<'FIOError, 'FIOResult>,
            contSuccess : 'FIOResult -> FIO<'Error, 'Result>,
            effError : 'FIOError -> FIO<'Error, 'Result>) =
    Concurrent(eff, fun fiber ->
        Await(fiber, fun result ->
            match result with
            | Success res -> contSuccess res
            | Error error -> effError error))

    let Sequence<'FIOResult, 'Error, 'Result>
            (eff : FIO<'Error, 'FIOResult>,
             cont : 'FIOResult -> FIO<'Error, 'Result>) =
        AwaitEffect(eff, (fun resEff -> AwaitEffect(cont resEff,
                                            (fun resCont -> Succeed resCont),
                                            (fun errCont -> Fail errCont))),
                         (fun errEff -> Fail errEff))

    let (>>=) (eff : FIO<'Error, 'FIOResult>) (cont : 'FIOResult -> FIO<'Error, 'Result>) =
        Sequence<'FIOResult, 'Error, 'Result>(eff, cont)

    let OrElse<'Error, 'Result>(eff : FIO<'Error, 'Result>, elseEff : FIO<'Error, 'Result>) =
        AwaitEffect(eff, (fun resEff -> Succeed resEff),
                         (fun _      -> AwaitEffect(elseEff,
                                            (fun resElse -> Succeed resElse),
                                            (fun errElse -> Fail errElse))))

    let OnError<'FIOError, 'Error, 'Result>
            (eff : FIO<'FIOError, 'Result>,
             cont : 'FIOError -> FIO<'Error, 'Result>) =
        AwaitEffect(eff, (fun resEff -> Succeed resEff),
                         (fun errEff -> AwaitEffect(cont errEff,
                                            (fun resCont -> Succeed resCont),
                                            (fun errCont -> Fail errCont))))

    let Race<'Error, 'Result>
            (effA : FIO<'Error, 'Result>,
             effB : FIO<'Error, 'Result>) =
        let rec loop (fiberA : Fiber<'Error, 'Result>) (fiberB : Fiber<'Error, 'Result>) =
            if fiberA.IsCompleted() then
                // cancel and dispose of fiber B?
                fiberA.Await()
            else if fiberB.IsCompleted() then
                // cancel and dispose of fiber A?
                fiberB.Await()
            else
                loop fiberA fiberB
        Concurrent(effA, fun fiberA ->
            Concurrent(effB, fun fiberB ->
                match loop fiberA fiberB with
                | Success res -> Succeed res
                | Error error -> Fail error))

                *)