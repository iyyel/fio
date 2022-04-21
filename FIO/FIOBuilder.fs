(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FSharp.FIO

open FSharp.FIO

[<AutoOpen>]
module FIOBuilder =

    let runtime = Advanced.Runtime()

    let internal bind (eff : FIO<'R, 'E>) (func : Result<'R, 'E> -> FIO<'R, 'E>) : FIO<'R, 'E> =
        toFIO(Ok 2)

    let internal delay (func : unit -> FIO<'R, 'E>) : FIO<'R, 'E> =
        func ()

    let internal returnFunc () =
        ()

    let internal returnFrom () =
        ()

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
