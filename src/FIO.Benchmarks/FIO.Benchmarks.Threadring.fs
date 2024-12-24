(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(* -------------------------------------------------------------------------------- *)
(* Threadring benchmark                                                             *)
(* Measures: Message sending; Context switching between actors                      *)
(* Savina benchmark #5                                                              *)
(* (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)         *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Benchmarks.Threadring

open FIO.Core

type private Actor =
    { Name: string
      SendingChannel: int channel
      ReceivingChannel: int channel }

let private createActor actor rounds timerChannel : FIO<unit, obj> =

    let rec create rounds = fio {
        if rounds = 0 then
            do! Timer.ChannelTimerMessage.Stop -*> timerChannel
        else
            let! received = !->? actor.ReceivingChannel
            #if DEBUG
            do! !+ printfn($"DEBUG: %s{actor.Name} received: %i{received}")
            #endif
            let! message = !+ (received + 1)
            do! message -*> actor.SendingChannel
            #if DEBUG
            do! !+ printfn($"DEBUG: %s{actor.Name} sent: %i{message}")
            #endif
            return! create (rounds - 1)
        }

    fio {
        do! Timer.ChannelTimerMessage.Ready -*> timerChannel
        return! create rounds
    }

let internal Create actorCount rounds : FIO<BenchmarkResult, obj> =

    let getRecvChan index (chans: Channel<int> list) =
        match index with
        | index when index - 1 < 0 -> chans.Item(List.length chans - 1)
        | index -> chans.Item(index - 1)

    let rec createActors chans allChans index acc =
        match chans with
        | [] -> acc
        | chan :: chans ->
            let proc =
                { Name = $"Actor-{index}"
                  SendingChannel = chan
                  ReceivingChannel = getRecvChan index allChans }

            createActors chans allChans (index + 1) (acc @ [ proc ])

    let rec createThreadring procs acc timerChan =
        match procs with
        | [] -> acc
        | p :: ps ->
            let eff = createActor p rounds timerChan <!> acc
            createThreadring ps eff timerChan

    let chans = [ for _ in 1..actorCount -> Channel<int>() ]
    let procs = createActors chans chans 0 []

    let pa, pb, ps =
        match procs with
        | pa :: pb :: ps -> (pa, pb, ps)
        | _ -> failwith $"createThreadring failed! (at least 2 processes should exist) processCount = %i{actorCount}"

    fio {
        let timerChan = Channel<Timer.ChannelTimerMessage<int>>()
        let effEnd = createActor pb rounds timerChan <!> createActor pa rounds timerChan

        let! fiber = ! (Timer.ChannelEffect actorCount 1 actorCount timerChan)
        do! (Timer.ChannelTimerMessage.Chan pa.ReceivingChannel) -*> timerChan
        do! createThreadring ps effEnd timerChan
        let! time = !? fiber
        return time
    }