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

[<EntryPoint>]
let main _ =

    let benchmarkConfig = {
        Pingpong =   {RoundCount   = 100};
        Threadring = {ProcessCount = 100;   RoundCount = 2};
        Big =        {ProcessCount = 100;   RoundCount = 2};
        Bang =       {SenderCount  = 100; MessageCount = 2};
    }

    Benchmarks.Benchmark.RunAll <| benchmarkConfig <| 100 <| Naive.Run
    
    0