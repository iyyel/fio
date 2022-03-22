(**********************************************************************************)
(* FIO - Effectful programming library for F#                                     *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

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
    let runs = results.GetResult ArgParser.Runs
    let processIncrement = results.GetResult ArgParser.Process_Count_Increment

    let pingpongConfig =
        match results.TryGetResult ArgParser.Pingpong with
        | Some roundCount -> [Pingpong { RoundCount = roundCount }]
        | _ -> []

    let threadRingConfig =
        match results.TryGetResult ArgParser.ThreadRing with
        | Some (processCount, roundCount) -> 
            [ThreadRing { ProcessCount = processCount; RoundCount = roundCount }]
        | _ -> []

    let bigConfig =
        match results.TryGetResult ArgParser.Big with
        | Some (processCount, roundCount) ->
            [Big { ProcessCount = processCount; RoundCount = roundCount }]
        | _ -> []

    let bangConfig =
        match results.TryGetResult ArgParser.Bang with
        | Some (processCount, roundCount) ->
            [Bang { ProcessCount = processCount; RoundCount = roundCount }]
        | _ -> []

    let configs = pingpongConfig @ threadRingConfig @ bigConfig @ bangConfig

    let runtime : Runtime = 
        match results.TryGetResult ArgParser.Naive_Runtime with
        | Some _ -> Naive()
        | _      -> 
            match results.TryGetResult ArgParser.Advanced_Runtime with
            | Some (x, y, z) -> Advanced(x, y, z)
            | _              -> failwith "ArgParser: Invalid runtime specified!"

    Run configs runtime runs processIncrement

let runSampleEffect () =
    let pinger chan =
        let x = 42
        Send(x, chan) >> fun _ ->
        printfn $"Pinger sent ping: %i{x}"
        Succeed "pinger done"

    let ponger chan =
        Receive chan >> fun x ->
        printfn $"Ponger received ping: %i{x}"
        Succeed "ponger done"

    let pingpong =
        let chan = Channel<int>()
        Parallel(pinger chan, ponger chan)
          
    let runtime = Advanced(2, 2, 10)
    let chan = Channel<int>()
    let fiber = runtime.Eval <| pingpong
    printfn $"Result: %A{fiber.Await()}"

[<EntryPoint>]
let main args =
    //printArgs args
    //runBenchmarks args
    runSampleEffect()
    0
