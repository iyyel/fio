// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open FSharp.FIO.Runtime
open Examples
open System.Threading

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

[<EntryPoint>]
let main _ =
    let fiber = Naive.Run <| Ring.processRing 10000 10000
    printfn $"Result: %A{fiber.Await()}"

    0