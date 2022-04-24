(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module Program

open System.Threading

open FSharp.FIO
open Benchmarks.Benchmark

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

let runBenchmarks parsedArgs =
    let configs, runtime, runs, processIncrement = parsedArgs
    Run configs runtime runs processIncrement

[<EntryPoint>]
let main args =

    let res = fio {
        let x = 2
        let y = 4
        do printfn "Hello world!"

        let t = Succeed 42
        //let! z = t

        return x + y
    }

    printfn $"Result: %A{res}"

    let parser = ArgParser.Parser()
    parser.PrintArgs args
    runBenchmarks <| parser.ParseArgs args
    0
