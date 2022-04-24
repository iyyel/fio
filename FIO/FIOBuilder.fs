(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FSharp.FIO

open FSharp.FIO

[<AutoOpen>]
module FIOBuilder =

    let internal bind (eff : FIO<obj, 'E>) (func : obj -> FIO<'R, 'E>) : FIO<'R, 'E> =
        Sequence (eff, func)

    let internal delay (func : unit -> FIO<'R, 'E>) : FIO<'R, 'E> =
        func ()

    let internal returnFunc value =
        Success value

    let internal returnFrom eff =
        eff

    let internal run () =
        ()

    type FIOBuilder() =
        member _.Bind eff func = bind eff func
        member _.Delay func = delay func
        member _.Return value = returnFunc value
        member _.ReturnFrom value = returnFrom value
        member _.Run = run

    /// FIO computation expression builder
    let fio = new FIOBuilder()
