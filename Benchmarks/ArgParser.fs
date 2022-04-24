(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module ArgParser

open Argu

open FSharp.FIO
open Benchmarks

type Arguments =
    | Naive_Runtime
    | Intermediate_Runtime of evalworkercount: int * blockingworkercount: int * evalstepcount: int
    | Advanced_Runtime of evalworkercount: int * blockingworkercount: int * evalstepcount: int
    | Deadlocking_Runtime of evalworkercount: int * blockingworkercount: int * evalstepcount: int
    | [<Mandatory>] Runs of runs: int
    | Process_Increment of processcountinc: int * inctimes: int
    | Pingpong of roundcount: int
    | ThreadRing of processcount: int * roundcount: int
    | Big of processcount: int * roundcount: int
    | Bang of processcount: int * roundcount: int
    | ReverseBang of processcount: int * roundcount: int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Naive_Runtime _ -> 
                "specify naive runtime. (specify only one runtime)"
            | Intermediate_Runtime _ ->
                "specify eval worker count, blocking worker count and eval step count for intermediate runtime. (specify only one runtime)"
            | Advanced_Runtime _ ->
                "specify eval worker count, blocking worker count and eval step count for advanced runtime. (specify only one runtime)"
            | Deadlocking_Runtime _ ->
                "specify eval worker count, blocking worker count and eval step count for deadlocking runtime. (specify only one runtime)"
            | Runs _ ->
                "specify the number of runs for each benchmark."
            | Process_Increment _ ->
                "specify the value of process count increment and how many times."
            | Pingpong _ ->
                "specify round count for pingpong benchmark."
            | ThreadRing _ ->
                "specify process count and round count for threadring benchmark."
            | Big _ ->
                "specify process count and round count for big benchmark."
            | Bang _ ->
                "specify process count and round count for bang benchmark."
            | ReverseBang _ ->
                "specify process count and round count for reversebang benchmark."

type Parser() =
    let parser = ArgumentParser.Create<Arguments>()

    member _.PrintArgs args =
        let args = List.fold (fun s acc -> s + " " + acc) "" (List.ofArray args)
        printfn $"benchmark arguments:%s{args}"

    member _.PrintUsage() =
        printfn $"%s{parser.PrintUsage()}"

    member _.ParseArgs args =
        let results = parser.Parse args
        let runs = results.GetResult Runs

        let processIncrement =
            match results.TryGetResult Process_Increment with
            | Some (x, y) -> x, y
            | _ -> 0, 0

        let pingpongConfig =
            match results.TryGetResult Pingpong with
            | Some roundCount -> [Benchmark.Pingpong { RoundCount = roundCount }]
            | _ -> []

        let threadRingConfig =
            match results.TryGetResult ThreadRing with
            | Some (processCount, roundCount) -> 
                [Benchmark.ThreadRing { ProcessCount = processCount; RoundCount = roundCount }]
            | _ -> []

        let bigConfig =
            match results.TryGetResult Big with
            | Some (processCount, roundCount) ->
                [Benchmark.Big { ProcessCount = processCount; RoundCount = roundCount }]
            | _ -> []

        let bangConfig =
            match results.TryGetResult Bang with
            | Some (processCount, roundCount) ->
                [Benchmark.Bang { ProcessCount = processCount; RoundCount = roundCount }]
            | _ -> []

        let reverseBangConfig =
            match results.TryGetResult ReverseBang with
            | Some (processCount, roundCount) ->
                [Benchmark.ReverseBang { ProcessCount = processCount; RoundCount = roundCount }]
            | _ -> []

        let configs = pingpongConfig @ threadRingConfig @
                      bigConfig @ bangConfig @ reverseBangConfig

        let runtime : Runner =
            match results.TryGetResult Naive_Runtime with
            | Some _ -> Naive.Runtime()
            | _ ->
                match results.TryGetResult Intermediate_Runtime with
                | Some (ewc, bwc, esc) -> Intermediate.Runtime(ewc, bwc, esc)
                | _ ->
                    match results.TryGetResult Advanced_Runtime with
                    | Some (ewc, bwc, esc) -> Advanced.Runtime(ewc, bwc, esc)
                    | _ -> 
                        match results.TryGetResult Deadlocking_Runtime with
                        | Some (ewc, bwc, esc) -> Deadlocking.Runtime(ewc, bwc, esc)
                        | _ -> failwith "ArgParser: Invalid runtime specified!"
    
        (configs, runtime, runs, processIncrement)
