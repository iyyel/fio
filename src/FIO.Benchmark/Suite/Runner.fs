(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

module internal rec FIO.Benchmark.Suite.Runner

open FIO.Core
open FIO.Runtime
open FIO.Runtime.Naive
open FIO.Runtime.Intermediate
open FIO.Runtime.Advanced
open FIO.Runtime.Deadlocking

open System
open System.IO

type PingpongConfig =
    { Rounds: int }

and ThreadringConfig =
    { Actors: int
      Rounds: int }

and BigConfig =
    { Actors: int
      RoundCount: int }

and BangConfig =
    { Actors: int
      Rounds: int }

and ForkConfig = 
    { Actors: int }

and BenchmarkConfig =
    | PingpongC of PingpongConfig
    | ThreadringC of ThreadringConfig
    | BigC of BigConfig
    | BangC of BangConfig
    | ForkC of ForkConfig

and EvalFunc = FIO<int64, obj> -> Fiber<int64, obj>

and BenchmarkResult = string * BenchmarkConfig * string * string * (int * int64) list

let private writeResultsToCsv (result: BenchmarkResult) =
    let configStr config =
        match config with
        | PingpongC config -> $"roundcount%i{config.Rounds}"
        | ThreadringC config -> $"processcount%i{config.Actors}-roundcount%i{config.Rounds}"
        | BigC config -> $"processcount%i{config.Actors}-roundcount%i{config.RoundCount}"
        | BangC config -> $"processcount%i{config.Actors}-roundcount%i{config.Rounds}"
        | ForkC config -> $"processcount%i{config.Actors}"

    let rec fileContentStr times acc =
        match times with
        | [] -> acc
        | (_, time) :: ts -> fileContentStr ts (acc + $"%i{time}\n")

    let headerStr = "Time"

    let homePath =
        if
            (Environment.OSVersion.Platform.Equals(PlatformID.Unix)
                || Environment.OSVersion.Platform.Equals(PlatformID.MacOSX))
        then
            Environment.GetEnvironmentVariable("HOME")
        else
            Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%")

    let benchName, config, runtimeFileName, _, times = result

    let configStr = configStr config
    let runStr = times.Length.ToString() + "runs"
    let folderName = benchName.ToLower() + "-" + configStr + "-" + runStr

    let fileName =
        folderName
        + "-"
        + runtimeFileName.ToLower()
        + "-"
        + DateTime.Now.ToString("dd-MM-yyyy-HH-mm-ss")
        + ".csv"

    let dirPath =
        homePath + @"\fio\benchmarks\" + folderName + @"\" + runtimeFileName.ToLower()

    let filePath = dirPath + @"\" + fileName

    if (not <| Directory.Exists(dirPath)) then
        Directory.CreateDirectory(dirPath) |> ignore
    else
        ()

    let fileContent = fileContentStr times ""
    printfn $"\nSaving benchmark results to '%s{filePath}'"
    File.WriteAllText(filePath, headerStr + "\n" + fileContent)

let private benchStr config =
    match config with
    | PingpongC config -> $"Pingpong (RoundCount: %i{config.Rounds})"
    | ThreadringC config -> $"Threadring (ProcessCount: %i{config.Actors} RoundCount: %i{config.Rounds})"
    | BigC config -> $"Big (ProcessCount: %i{config.Actors} RoundCount: %i{config.RoundCount})"
    | BangC config -> $"Bang (ProcessCount: %i{config.Actors} RoundCount: %i{config.Rounds})"
    | ForkC config -> $"Fork (ProcessCount: %i{config.Actors})"

let private printResult (result: BenchmarkResult) =
    let rec runExecTimesStr runExecTimes acc =
        match runExecTimes with
        | [] ->
            (acc
                + "└───────────────────────────────────────────────────────────────────────────┘")
        | (run, time) :: ts ->
            let str = $"│  #%-10i{run}                       %-35i{time}    │\n"
            runExecTimesStr ts (acc + str)

    let _, config, _, runtimeName, times = result
    let benchName = benchStr config
    let runExecTimesStr = runExecTimesStr times ""

    let headerStr =
        $"
┌───────────────────────────────────────────────────────────────────────────┐
│  Benchmark:  %-50s{benchName}           │
│  Runtime:    %-50s{runtimeName}           │
├───────────────────────────────────────────────────────────────────────────┤
│  Run                               Time (ms)                              │
│  ────────────────────────────────  ─────────────────────────────────────  │\n"

    let toPrint = headerStr + runExecTimesStr
    printfn $"%s{toPrint}"

let private runBenchmark config runs (runtime: Runtime) : BenchmarkResult =
    let getRuntimeName (runtime: Runtime) =
        match runtime with
        | :? NaiveRuntime -> ("naive", "Naive")
        | :? IntermediateRuntime as r ->
            let ewc, bwc, esc = r.GetConfiguration()
            ($"intermediate-ewc%i{ewc}-bwc%i{bwc}-esc%i{esc}", $"Intermediate (EWC: %i{ewc} BWC: %i{bwc} ESC: %i{esc})")
        | :? AdvancedRuntime as r ->
            let ewc, bwc, esc = r.GetConfiguration()
            ($"advanced-ewc%i{ewc}-bwc%i{bwc}-esc%i{esc}", $"Advanced (EWC: %i{ewc} BWC: %i{bwc} ESC: %i{esc})")
        | :? DeadlockingRuntime as r ->
            let ewc, bwc, esc = r.GetConfiguration()
            ($"deadlocking-ewc%i{ewc}-bwc%i{bwc}-esc%i{esc}", $"Deadlocking (EWC: %i{ewc} BWC: %i{bwc} ESC: %i{esc})")
        | _ -> failwith "runBenchmark: Invalid runtime!"

    let createBenchmark config =
        match config with
        | PingpongC config -> ("Pingpong", Pingpong.Create config.Rounds)
        | ThreadringC config -> ("Threadring", Threadring.Create config.Actors config.Rounds)
        | BigC config -> ("Big", Big.Create config.Actors config.RoundCount)
        | BangC config -> ("Bang", Bang.Create config.Actors config.Rounds)
        | ForkC config -> ("Fork", Fork.Create config.Actors)

    let rec executeBenchmark config curRun acc =
        let bench, eff = createBenchmark config

        match curRun with
        | curRun' when curRun' = runs -> (bench, acc)
        | curRun' ->
            let res: Result<int64, obj> = runtime.Run(eff).AwaitResult()

            let time =
                match res with
                | Ok time -> time
                | Error _ -> -1

            let runNum = curRun' + 1
            let result = (runNum, time)
            executeBenchmark config runNum (acc @ [ result ])

    let runtimeFileName, runtimeName = getRuntimeName runtime
    let bench, runExecTimes = executeBenchmark config 0 []
    (bench, config, runtimeFileName, runtimeName, runExecTimes)

let Run configs runtime runs (processCountInc, incTimes) =
    let newConfig config incTime =
        match config with
        | PingpongC config -> PingpongC config
        | ThreadringC config ->
            ThreadringC
                { Rounds = config.Rounds
                  Actors = config.Actors + (processCountInc * incTime) }
        | BigC config ->
            BigC
                { RoundCount = config.RoundCount
                  Actors = config.Actors + (processCountInc * incTime) }
        | BangC config ->
            BangC
                { Rounds = config.Rounds
                  Actors = config.Actors + (processCountInc * incTime) }
        | ForkC config -> ForkC { Actors = config.Actors + (processCountInc * incTime) }

    let configs =
        configs
        @ (List.concat
            <| List.map (fun incTime -> List.map (fun config -> newConfig config incTime) configs) [ 1..incTimes ])

    let results = List.map (fun config -> runBenchmark config runs runtime) configs

    for result in results do
        printResult result
        writeResultsToCsv result
