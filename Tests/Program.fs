// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

open FsCheck
open FSharp.FIO.Runtime
open FSharp.FIO.FIO

let succeedIsAlwaysTheSame (result : int) = (Default.Run <| Succeed result).Await() = Success result

[<EntryPoint>]
let main _ =
    Check.Quick succeedIsAlwaysTheSame
    0