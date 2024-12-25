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

module internal rec FIO.Benchmark.Suite.Threadring

open FIO.Core

open FIO.Benchmark.Tools.Timing.ChannelTimer

type private Actor =
    { Name: string
      SendingChannel: int channel
      ReceivingChannel: int channel }

[<TailCall>]
let rec private createActorHelper rounds actor timerChannel = fio {
    if rounds = 0 then
        do! timerChannel <!- TimerMessage.Stop
    else
        let! received = !<-- actor.ReceivingChannel
        #if DEBUG
        do! !+ printfn($"DEBUG: %s{actor.Name} received: %i{received}")
        #endif
        let! message = !+ (received + 1)
        do! actor.SendingChannel <!- message
        #if DEBUG
        do! !+ printfn($"DEBUG: %s{actor.Name} sent: %i{message}")
        #endif
        return! createActorHelper (rounds - 1) actor timerChannel
}

let private createActor rounds actor timerChannel = fio {
    do! timerChannel <!- TimerMessage.Start
    return! createActorHelper rounds actor timerChannel
}

[<TailCall>]
let rec private createThreadring rounds procs timerChannel acc =
    match procs with
    | [] ->
        acc
    | p :: ps ->
        let newAcc = createActor rounds p timerChannel <!> acc
        createThreadring rounds ps timerChannel newAcc

let internal Create actorCount (rounds : int) : FIO<BenchmarkResult, obj> =

    let getReceivingChannel index (channels: Channel<int> list) =
        match index with
        | index when index - 1 < 0 ->
            channels.Item(List.length channels - 1)
        | index ->
            channels.Item(index - 1)

    let rec createActors channels allChannels index acc =
        match channels with
        | [] -> acc
        | chan::chans ->
            let actor =
                { Name = $"Actor-{index}"
                  SendingChannel = chan
                  ReceivingChannel = getReceivingChannel index allChannels }
            createActors chans allChannels (index + 1) (acc @ [ actor ])

   

    let chans = [ for _ in 1..actorCount -> Channel<int>() ]
    let procs = createActors chans chans 0 []

    let pa, pb, ps =
        match procs with
        | pa :: pb :: ps -> (pa, pb, ps)
        | _ -> failwith $"createThreadring failed! (at least 2 processes should exist) processCount = %i{actorCount}"

    fio {
        let timerChannel = Channel<TimerMessage<int>>()
        let acc = createActor rounds pb timerChannel 
                     <!> createActor rounds pa timerChannel

        let! fiber = ! TimerEffect(actorCount, 1, actorCount, timerChannel)
        do! timerChannel <!- TimerMessage.MessageChannel (pa.ReceivingChannel)
        do! createThreadring rounds ps timerChannel acc
        let! time = !? fiber
        return time
    }