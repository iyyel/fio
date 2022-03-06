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
    let fiber = Naive.Run <| Benchmarks.Bang.Create 100 100
    printfn $"Result: %A{fiber.Await()}"

let runSingleBenchmark() =
    let pingpongConfig   = Pingpong   {RoundCount   = 1000}
    let threadringConfig = ThreadRing {ProcessCount = 1000; RoundCount = 3}
    let bigConfig        = Big        {ProcessCount = 1000; RoundCount = 3}
    let bangConfig       = Bang       {ProcessCount = 1000; RoundCount = 3}
    Benchmarks.Benchmark.Run bangConfig 1 "Naive" Naive.Run

let runAllBenchmarks() =
    let configs = {
        Pingpong   = {RoundCount   = 1000};
        ThreadRing = {ProcessCount = 1000; RoundCount = 3};
        Big        = {ProcessCount = 1000; RoundCount = 3};
        Bang       = {ProcessCount = 1000; RoundCount = 3};
    }
    Benchmarks.Benchmark.RunAll configs 10 "Naive" Naive.Run

[<EntryPoint>]
let main _ =
    runAllBenchmarks()
    0