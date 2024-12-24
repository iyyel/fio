(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(* -------------------------------------------------------------------------------- *)
(* Fork benchmark                                                                   *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Benchmarks.Fork

open FIO.Core
open System.Diagnostics

let rec private createProcess timerChan =
    Timer.StopwatchTimerMessage.Stop --> timerChan >>= fun _ -> !+ ()

let Create processCount : FIO<int64, obj> =

    let rec createSpawnTime processCount timerChan acc =
        match processCount with
        | 0 -> acc
        | count ->
            let eff = createProcess timerChan <!> acc
            createSpawnTime (count - 1) timerChan eff

    let timerChan = Channel<Timer.StopwatchTimerMessage>()
    let effEnd = createProcess timerChan <!> createProcess timerChan
    let stopwatch = Stopwatch()

    ! (Timer.StopwatchEffect processCount timerChan)
    >>= fun fiber ->
        stopwatch.Start()

        (Timer.StopwatchTimerMessage.Start stopwatch) --> timerChan
        >>= fun _ ->
            createSpawnTime (processCount - 2) timerChan effEnd
            >>= fun _ -> !? fiber >>= fun res -> succeed res