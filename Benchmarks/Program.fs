﻿(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
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

    let processIncrement =
        match results.TryGetResult ArgParser.Process_Increment with
        | Some (x, y) -> x, y
        | _ -> 0, 0

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

    let reverseBangConfig =
        match results.TryGetResult ArgParser.ReverseBang with
        | Some (processCount, roundCount) ->
            [ReverseBang { ProcessCount = processCount; RoundCount = roundCount }]
        | _ -> []

    let configs = pingpongConfig @ threadRingConfig @
                  bigConfig @ bangConfig @ reverseBangConfig

    let runtime : Runtime = 
        match results.TryGetResult ArgParser.Naive_Runtime with
        | Some _ -> Naive()
        | _      -> 
            match results.TryGetResult ArgParser.Advanced_Runtime with
            | Some (ewc, bwc, esc) -> Advanced(ewc, bwc, esc)
            | _ -> failwith "ArgParser: Invalid runtime specified!"

    Run configs runtime runs processIncrement

let runSingleProgram() =
    let sendProcess chan =
        Send(10, chan) >> fun _ -> Send(10, chan) >> fun _ ->
        Send(10, chan) >> fun _ -> Send(10, chan) >> fun _ ->
        Send(10, chan) >> fun _ -> Send(10, chan) >> fun _ ->
        Send(10, chan) >> fun _ -> Send(10, chan) >> fun _ ->
        Send(10, chan) >> fun _ -> Send(10, chan) >> fun _ ->
        Send(10, chan) >> fun _ -> End()

    let recvProcess chan =
        Receive chan >> fun _ -> End()

    let chan = Channel<int>()
    let eff = Parallel(recvProcess chan, Parallel(recvProcess chan,
              Parallel(recvProcess chan, Parallel(recvProcess chan,
              Parallel(recvProcess chan, Parallel(recvProcess chan,
              Parallel(recvProcess chan, Parallel(recvProcess chan,
              Parallel(recvProcess chan, Parallel(recvProcess chan,
              Parallel(recvProcess chan, sendProcess chan))))))))))) >> fun (_, _) -> End()

    let result = Advanced(1, 10000).Eval(eff).Await()
    printfn $"Result: %A{result}"

[<EntryPoint>]
let main args =
    printArgs args
    runBenchmarks args
    0