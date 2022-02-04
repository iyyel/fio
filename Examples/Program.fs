// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open FSharp.FIO
open Examples
open System.Threading

ThreadPool.SetMaxThreads(10000, 10000) |> ignore
ThreadPool.SetMinThreads(10000, 10000) |> ignore

let printThreadPoolInfo =
    let maxWorkerThreads = ref 0;
    let maxCompletePortThreads = ref 0;
    let minWorkerThreads = ref 0;
    let minCompletePortThreads = ref 0;
    let avlWorkerThreads = ref 0;
    let avlIoThreads = ref 0;
    ThreadPool.GetMaxThreads(maxWorkerThreads, maxCompletePortThreads)
    ThreadPool.GetMinThreads(minWorkerThreads, minCompletePortThreads)
    ThreadPool.GetAvailableThreads(avlWorkerThreads, avlIoThreads);
    printfn $"Thread pool information: "
    printfn $"  Maximum worker threads: %A{maxWorkerThreads},\n  Maximum completion port threads %A{maxCompletePortThreads}"
    printfn $"  Minimum worker threads: %A{minWorkerThreads},\n  Minimum completion port threads %A{minCompletePortThreads}"
    printfn $"  Available worker threads: %A{avlWorkerThreads},\n  Available I/O threads: %A{avlIoThreads}"

[<EntryPoint>]
let main _ =
    let result = NaiveEval <| Ring.processRing 5000 5000
    printfn $"Result: %A{result}"

    0