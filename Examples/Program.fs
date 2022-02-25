// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module Program

open FSharp.FIO
open FSharp.FIO.FIO
open Examples
open System.Threading
open System.Diagnostics

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

type TimedOperation<'T> = {millisecondsTaken:int64; returnedValue:'T}

let timeOperation<'T> (func: unit -> 'T): TimedOperation<'T> =
    let timer = new Stopwatch()
    timer.Start()
    let returnValue = func()
    timer.Stop()
    {millisecondsTaken=timer.ElapsedMilliseconds; returnedValue=returnValue}

[<EntryPoint>]
let main _ =
    let naive = new Runtime.Naive()
    let fiber = naive.Run <| Ring.processRing 10 3
    printfn $"Result: %A{fiber.Await()}"

    0