(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(* -------------------------------------------------------------------------------- *)
(* Pingpong benchmark                                                               *)
(* Measures: Message delivery overhead                                              *)
(* Savina benchmark #1                                                              *)
(* (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)         *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Benchmarks.Pingpong

open FIO.Core

open System.Diagnostics

type private Actor =
    { Name: string
      SendingChannel: int channel
      ReceivingChannel: int channel }

let private createPingActor actor startSignalChannel rounds : FIO<BenchmarkResult, obj> =
    let stopwatch = Stopwatch()

    // TODO: Make tail-recursive.
    let rec create message rounds = fio {
        if rounds = 0 then
            do! !+ stopwatch.Stop()
            return stopwatch.ElapsedMilliseconds    
        else
            do! message -*> actor.SendingChannel
            #if DEBUG
            do! !+ printfn($"DEBUG: %s{actor.Name} sent ping: %i{message}")
            #endif
            let! received = !->? actor.ReceivingChannel
            #if DEBUG
            do! !+ printfn($"DEBUG: %s{actor.Name} received pong: %i{received}")
            #endif
            return! create (received + 1) (rounds - 1)
    }

    fio {
        do! !*>? startSignalChannel
        do! !+ stopwatch.Start()
        return! create 1 rounds
    }

let private createPongActor actor startSignalChannel rounds : FIO<unit, obj> =

    // TODO: Make tail-recursive.
    let rec create rounds = fio {
        if rounds = 0 then
            return ()
        else
            let! received = !->? actor.ReceivingChannel
            #if DEBUG
            do! !+ printfn($"DEBUG: %s{actor.Name} received ping: %i{received}")
            #endif
            let! message = !+ (received + 1)
            do! message -*> actor.SendingChannel
            #if DEBUG
            do! !+ printfn($"DEBUG: %s{actor.Name} sent pong: %i{message}")
            #endif
            return! create (rounds - 1)
    }

    fio {
        do! 0 -*> startSignalChannel
        return! create rounds
    }

let internal Create rounds : FIO<BenchmarkResult, obj> = fio {
    let startSignalChannel = Channel<int>()
    let pingSendingChannel = Channel<int>()
    let pongSendingChannel = Channel<int>()

    let pingActor =
        { Name = "PingActor"
          SendingChannel = pingSendingChannel
          ReceivingChannel = pongSendingChannel }

    let pongActor =
        { Name = "PongActor"
          SendingChannel = pongSendingChannel
          ReceivingChannel = pingSendingChannel }

    let! (result, _) = createPingActor pingActor startSignalChannel rounds
                       <*> createPongActor pongActor startSignalChannel rounds
    return result
}