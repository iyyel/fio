// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open Benchmarks.Benchmark
open FSharp.FIO.Runtime
open System.Threading

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

let runEffect() =
    let fiber = Naive.Run <| Benchmarks.Bang.Run 100 100
    printfn $"Result: %A{fiber.Await()}"

let runSingleBenchmark() =
    let pongConfig       = Pingpong   {RoundCount   = 100}
    let threadringConfig = ThreadRing {ProcessCount = 100; RoundCount   = 100}
    let bigConfig        = Big        {ProcessCount = 100; RoundCount   = 100}
    let bangConfig       = Bang       {SenderCount  = 100; MessageCount = 100}
    Benchmarks.Benchmark.Run bangConfig 1 Naive.Run

let runAllBenchmarks() =
    let configs = {
        Pingpong =   {RoundCount   = 100};
        ThreadRing = {ProcessCount = 100; RoundCount   = 100};
        Big =        {ProcessCount = 100; RoundCount   = 100};
        Bang =       {SenderCount  = 100; MessageCount = 100};
    }
    Benchmarks.Benchmark.RunAll configs 10 Naive.Run

[<EntryPoint>]
let main _ =
    runAllBenchmarks()
    0