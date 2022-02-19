// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

open FsCheck
open FSharp.FIO
open FSharp.FIO.FIO

let runtime = new Runtime.Naive()

let succeedIsAlwaysTheSame (result : int) = (runtime.Run <| Succeed result).Await() = Success result

[<EntryPoint>]
let main _ =
    Check.Quick succeedIsAlwaysTheSame
    0