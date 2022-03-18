// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open System.Threading

open Benchmarks.Benchmark
open FSharp.FIO.Runtime
open FSharp.FIO.FIO

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

let printArgs args =
    let args = List.fold (fun s acc -> s + " " + acc) "" (List.ofArray args)
    printfn $"benchmark arguments:%s{args}"

let runBenchmarks args =
    let parser = ArgParser.Parser()
    let results = parser.GetResults args
    let runtime = results.GetResult ArgParser.Runtime
    let runs = results.GetResult ArgParser.Runs
    let pingpongConfig = match results.TryGetResult ArgParser.Pingpong with
                         | Some roundCount -> [Pingpong {RoundCount = roundCount}]
                         | _               -> []
    let threadRingConfig = match results.TryGetResult ArgParser.ThreadRing with
                           | Some (processCount, roundCount) -> [ThreadRing {ProcessCount = processCount; RoundCount = roundCount}]
                           | _                               -> []
    let bigConfig = match results.TryGetResult ArgParser.Big with
                    | Some (processCount, roundCount) -> [Big {ProcessCount = processCount; RoundCount = roundCount}]
                    | _                               -> []       
    let bangConfig = match results.TryGetResult ArgParser.Bang with
                     | Some (processCount, roundCount) -> [Bang {ProcessCount = processCount; RoundCount = roundCount}]
                     | _                               -> []
    let configs = pingpongConfig @ threadRingConfig @ bigConfig @ bangConfig
    let runtimeName, evalFunc = match runtime with
                                | ArgParser.Naive    -> ("Naive", Naive().Eval)
                                | ArgParser.Advanced -> ("Advanced", Advanced().Eval)
    Run configs runs runtimeName evalFunc

let runSampleEffect() =
    let pinger chan =
        let x = 42
        Send (x, chan) >> fun _ ->
        printfn $"Pinger sent ping: %i{x}"
        Succeed "pinger done"
    let ponger chan =
        Receive chan >> fun x ->
        printfn $"Ponger received ping: %i{x}"
        Succeed "ponger done"
    let pingpong = let chan = Channel<int>()
                   Parallel (pinger chan, ponger chan)

    let runtime = Advanced(2, 2, 10)
    let fiber = runtime.Eval <| Benchmarks.Big.Create 100 1
    printfn $"Result: %A{fiber.Await()}"

[<EntryPoint>]
let main args =
    //printArgs args
    //runBenchmarks args
    runSampleEffect()
    0