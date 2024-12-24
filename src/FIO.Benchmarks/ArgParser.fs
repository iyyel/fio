(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

module ArgParser

open Argu

open FIO.Runtime

open FIO.Benchmarks

type Arguments =
    | Naive_Runtime
    | Intermediate_Runtime of evalworkercount: int * blockingworkercount: int * evalstepcount: int
    | Advanced_Runtime of evalworkercount: int * blockingworkercount: int * evalstepcount: int
    | Deadlocking_Runtime of evalworkercount: int * blockingworkercount: int * evalstepcount: int
    | [<Mandatory>] Runs of runs: int
    | Process_Increment of processcountinc: int * inctimes: int
    | Pingpong of roundcount: int
    | Threadring of processcount: int * roundcount: int
    | Big of processcount: int * roundcount: int
    | Bang of processcount: int * roundcount: int
    | Spawn of processcount: int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Naive_Runtime -> "specify naive runtime. (specify only one runtime)"
            | Intermediate_Runtime _ ->
                "specify eval worker count, blocking worker count and eval step count for intermediate runtime. (specify only one runtime)"
            | Advanced_Runtime _ -> "specify eval worker count, blocking worker count and eval step count for advanced runtime. (specify only one runtime)"
            | Deadlocking_Runtime _ ->
                "specify eval worker count, blocking worker count and eval step count for deadlocking runtime. (specify only one runtime)"
            | Runs _ -> "specify the number of runs for each benchmark."
            | Process_Increment _ -> "specify the value of process count increment and how many times."
            | Pingpong _ -> "specify round count for pingpong benchmark."
            | Threadring _ -> "specify process count and round count for threadring benchmark."
            | Big _ -> "specify process count and round count for big benchmark."
            | Bang _ -> "specify process count and round count for bang benchmark."
            | Spawn _ -> "specify process count for spawn benchmark."

type Parser() =
    let parser = ArgumentParser.Create<Arguments>()

    member _.PrintArgs args =
        let args = List.fold (fun s acc -> s + " " + acc) "" (List.ofArray args)
        printfn $"benchmark arguments:%s{args}"

    member _.PrintUsage() = printfn $"%s{parser.PrintUsage()}"

    member _.ParseArgs args =
        let results = parser.Parse args
        let runs = results.GetResult Runs

        let processIncrement =
            match results.TryGetResult Process_Increment with
            | Some(x, y) -> x, y
            | _ -> 0, 0

        let pingpongConfig =
            match results.TryGetResult Pingpong with
            | Some roundCount -> [ Benchmark.PingpongC { RoundCount = roundCount } ]
            | _ -> []

        let threadringConfig =
            match results.TryGetResult Threadring with
            | Some(processCount, roundCount) ->
                [ Benchmark.ThreadringC
                      { ProcessCount = processCount
                        RoundCount = roundCount } ]
            | _ -> []

        let bigConfig =
            match results.TryGetResult Big with
            | Some(processCount, roundCount) ->
                [ Benchmark.BigC
                      { ProcessCount = processCount
                        RoundCount = roundCount } ]
            | _ -> []

        let bangConfig =
            match results.TryGetResult Bang with
            | Some(processCount, roundCount) ->
                [ Benchmark.BangC
                      { ProcessCount = processCount
                        RoundCount = roundCount } ]
            | _ -> []

        let spawnConfig =
            match results.TryGetResult Spawn with
            | Some processcount -> [ Benchmark.SpawnC { ProcessCount = processcount } ]
            | _ -> []

        let configs =
            pingpongConfig @ threadringConfig @ bigConfig @ bangConfig @ spawnConfig

        let runtime: Runtime =
            match results.TryGetResult Naive_Runtime with
            | Some _ -> Naive.NaiveRuntime()
            | _ ->
                match results.TryGetResult Intermediate_Runtime with
                | Some(ewc, bwc, esc) -> Intermediate.IntermediateRuntime(ewc, bwc, esc)
                | _ ->
                    match results.TryGetResult Advanced_Runtime with
                    | Some(ewc, bwc, esc) -> Advanced.AdvancedRuntime(ewc, bwc, esc)
                    | _ ->
                        match results.TryGetResult Deadlocking_Runtime with
                        | Some(ewc, bwc, esc) -> Deadlocking.DeadlockingRuntime(ewc, bwc, esc)
                        | _ -> failwith "ArgParser: Invalid runtime specified!"

        (configs, runtime, runs, processIncrement)
