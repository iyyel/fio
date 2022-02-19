﻿// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open FSharp.FIO
open Examples
open System.Threading

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

[<EntryPoint>]
let main _ =
    let naive = new Runtime.Naive()
    let fiber = naive.Run <| Ring.processRing 10 2
    printfn $"Result: %A{fiber.Await()}"

    0