(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(* -------------------------------------------------------------------------------- *)
(* Fork benchmark                                                                   *)
(************************************************************************************)

[<AutoOpen>]
module internal rec FIO.Benchmark.Suite.Fork

open FIO.Benchmark.Tools.Timing.StopwatchTimer

open FIO.Core
open System.Diagnostics

let internal Create actorCount : FIO<BenchmarkResult, obj> =

    let rec createActor timerChannel = fio {
        return! timerChannel <!- TimerMessage.Stop
    }

    let rec createForkTime actorCount timerChannel acc = fio {
        match actorCount with
        | 0 -> 
            return! acc
        | count ->
            let newAcc = createActor timerChannel <!> acc
            return! createForkTime (count - 1) timerChannel newAcc
    }

    fio {
        let timerChan = Channel<TimerMessage>()
        let stopwatch = Stopwatch()
    
        let! timerFiber = ! TimerEffect(actorCount, timerChan)
        do! !+ stopwatch.Start()
        do! timerChan <!- TimerMessage.Start stopwatch

        let effEnd = createActor timerChan <!> createActor timerChan
        do! createForkTime (actorCount - 2) timerChan effEnd
        let! result = !? timerFiber
        return result
    }