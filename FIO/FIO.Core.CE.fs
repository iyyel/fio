(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Core.CE

module internal FIOBuilderHelper =

    /// Binds the result of one FIO computation to the next.
    let inline internal Bind (effect: FIO<'R1, 'E>) (continuation: 'R1 -> FIO<'R, 'E>) : FIO<'R, 'E> =
        effect >>= continuation

    /// Wraps a value in a successful FIO computation.
    let inline internal Return (result: 'R) : FIO<'R, 'E> =
        !+ result

    /// Directly returns an existing FIO computation.
    let inline internal ReturnFrom (effect: FIO<'R, 'E>) : FIO<'R, 'E> =
        effect

    let inline internal Yield (result: 'R) : FIO<'R, 'E> =
        Return result

    let inline internal YieldFrom (effect: FIO<'R, 'E>) : FIO<'R, 'E> =
        ReturnFrom effect

    /// Combines two computations, running one after the other.
    let inline internal Combine (firstEffect: FIO<'R, 'E>) (secondEffect: FIO<'R1, 'E>) : FIO<'R1, 'E> =
        firstEffect >> secondEffect

    /// Handles "zero" computations, which in this case might signify failure or stopping.
    let inline internal Zero () : FIO<Unit, 'E> =
        !+ ()

    /// Delays the execution of an FIO computation.
    let inline internal Delay (factory: unit -> FIO<'R, 'E>) : FIO<'R, 'E> =
        NonBlocking (fun () -> Ok()) >>= fun _ -> factory ()

    /// Evaluates a delayed FIO computation.
    let inline internal Run (effect: FIO<'R, 'E>) : FIO<'R, 'E> =
        effect

    /// Handles failure cases in the effect using the provided handler.
    let inline internal TryWith (effect: FIO<'R, 'E>) (handler: 'E -> FIO<'R, 'E>) : FIO<'R, 'E> =
        effect >>? handler

    /// Ensures a finalizer is executed after the main computation, regardless of success or failure.
    let inline internal TryFinally (effect: FIO<'R, exn>) (finalizer: unit -> unit) : FIO<'R, exn> =
        effect >>= fun result ->
            try
                finalizer ()
                !+ result
            with ex ->
                !- ex

    let inline internal While (guard: unit -> bool) (effect: FIO<'R, 'E>) : FIO<Unit, 'E> =
        let rec loop () =
            if guard () then
                Delay (fun () -> effect >> loop ())
            else
                !+ ()
        loop ()

type FIOBuilder() =
    member this.Bind(effect, continuation) =
        FIOBuilderHelper.Bind effect continuation

    member this.Return(result) =
        FIOBuilderHelper.Return result

    member this.ReturnFrom(result) =
        FIOBuilderHelper.ReturnFrom result

    member this.Yield(result) =
        FIOBuilderHelper.Yield result

    member this.YieldFrom(result) =
        FIOBuilderHelper.YieldFrom result

    member this.Combine(firstEffect, secondEffect) =
        FIOBuilderHelper.Combine firstEffect secondEffect

    member this.Zero() =
        FIOBuilderHelper.Zero

    member this.Delay(factory) =
        FIOBuilderHelper.Delay factory

    member this.Run(effect) =
        FIOBuilderHelper.Run effect

    member this.TryWith(effect, handler) = 
        FIOBuilderHelper.TryWith effect handler

    member this.TryFinally(effect, finalizer) =
        FIOBuilderHelper.TryFinally effect finalizer

    member this.While(guard, effect) =
        FIOBuilderHelper.While guard effect

let fio = FIOBuilder()
