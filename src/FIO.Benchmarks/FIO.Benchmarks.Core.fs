(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Benchmarks.Core

open FIO.Core

open System.Diagnostics

type BenchmarkResult = int64

[<AutoOpen>]
module Timer =

    type TimerMessage =
        | Start
        | Stop

    type StopwatchTimerMessage =
        | Start of Stopwatch
        | Stop

    type ChannelTimerMessage<'msg> =
        | Chan of Channel<'msg>
        | Ready
        | Stop

    let Effect startCount stopCount chan =
        let stopwatch = Stopwatch()

        let rec loopStart count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer started!"
#endif
                fio {
                    do! succeed <| stopwatch.Start()
                }
            | count ->
                !->? chan
                >>= fun res ->
                    match res with
                    | TimerMessage.Start -> loopStart (count - 1)
                    | _ -> loopStart count

        let rec loopStop count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer stopped!"
#endif
                fio {
                    do! succeed <| stopwatch.Stop()
                }
            | count ->
                !->? chan
                >>=  fun res ->
                    match res with
                    | TimerMessage.Stop -> loopStop (count - 1)
                    | _ -> loopStop count

        loopStart startCount
        >>= fun _ -> loopStop stopCount >>= fun _ -> succeed stopwatch.ElapsedMilliseconds

    let StopwatchEffect stopCount (chan: Channel<StopwatchTimerMessage>) =
        let mutable stopwatch = Stopwatch()

        let rec loopStop count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer stopped!"
#endif
                fio {
                    do! succeed <| stopwatch.Stop()
                }
            | count ->
                !->? chan >>= fun res ->
                match res with
                | StopwatchTimerMessage.Stop -> loopStop (count - 1)
                | _ -> loopStop count

        !->? chan
        >>= fun msg ->
            match msg with
            | StopwatchTimerMessage.Start sw -> stopwatch <- sw
            | StopwatchTimerMessage.Stop -> failwith "TimerFiber: Received stop as first message!"

            loopStop stopCount >>= fun _ -> succeed stopwatch.ElapsedMilliseconds

    let ChannelEffect readyCount goCount stopCount (chan: Channel<ChannelTimerMessage<int>>) =
        let stopwatch = Stopwatch()
        let mutable processChan = Channel<int>()

        let rec loopReady count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer started!"
#endif
                fio {
                    do! succeed <| stopwatch.Start()
                }
            | count ->
                !->? chan
                >>= fun res ->
                    match res with
                    | ChannelTimerMessage.Ready -> loopReady (count - 1)
                    | _ -> loopReady count

        let rec loopGo count msg =
            match count with
            | 0 -> !+ ()
            | count -> msg --> processChan >>= fun _ -> loopGo (count - 1) 0

        let rec loopStop count = fio {
            match count with
            | 0 -> return stopwatch.Stop()
            | count ->
                let! received = !->? chan
                match received with
                | ChannelTimerMessage.Stop -> return! loopStop (count - 1)
                | _ -> return! loopStop count
            }

        fio {
            let! received = !->? chan
            do! match received with
                | Chan pChan ->
                  processChan <- pChan
                  !+ ()
                | _ -> failwith "ChannelEffect: Did not receive channel as first message!"
            do! loopReady readyCount
            do! loopGo goCount 0
            do! loopStop stopCount
            return stopwatch.ElapsedMilliseconds
        }