// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open Benchmarks.Benchmark
open FSharp.FIO.Runtime
open System.Threading

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

let runProgram args =
    let parser = ArgParser.Parser()
    let results = parser.GetResults args
    let runtime = results.GetResult ArgParser.Runtime
    let runCount = results.GetResult ArgParser.Runs
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

    let runtimeName, runtimeFunc = match runtime with
                                     | ArgParser.Default  -> ("Default", Default().Run)
                                     | ArgParser.Naive    -> ("Naive", Naive().Run)
                                     | ArgParser.Advanced -> ("Advanced", Advanced().Run)
                      
    Run configs runCount runtimeName runtimeFunc

[<EntryPoint>]
let main args =
    let argStr = List.fold (fun s acc -> s + " " + acc) "" (List.ofArray args)
    printfn $"benchmark arguments:%s{argStr}"
    //runProgram args

    let runtime = Advanced()

    let workers = runtime.Workers
      

    0