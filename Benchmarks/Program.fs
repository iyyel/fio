// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open Benchmarks.Benchmark
open FSharp.FIO.Runtime
open FSharp.FIO.FIO
open System.Threading

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

let runEffect =
    let fiber = Naive.Run <| Benchmarks.Bang.Run 100 100
    printfn $"Result: %A{fiber.Await()}"

let runAllBenchmarks =
    let configs = {
        Pingpong =   {RoundCount   = 100};
        Threadring = {ProcessCount = 100; RoundCount = 100};
        Big =        {ProcessCount = 100; RoundCount = 100};
        Bang =       {SenderCount  = 100; MessageCount = 100};
    }

    Benchmarks.Benchmark.RunAll configs 100 Naive.Run

[<EntryPoint>]
let main _ =

    runAllBenchmarks
    
    0