(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(* -------------------------------------------------------------------------------- *)
(* Big benchmark                                                                    *)
(* Measures: Contention on mailbox; Many-to-Many message passing                    *)
(* Savina benchmark #7                                                              *)
(* (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)         *)
(************************************************************************************)

module internal rec FIO.Benchmark.Suite.Big

open FIO.Core

open FIO.Benchmark.Tools.Timing.ChannelTimer

type private Message =
    | Ping of int * Channel<Message>
    | Pong of int

type private Process =
    { Name: string
      ChanRecvPing: Channel<Message>
      ChanRecvPong: Channel<Message>
      ChansSend: Channel<Message> list }

let private createProcess proc msg roundCount timerChan goChan =
    let rec createSendPings chans roundCount =
        if List.length chans = 0 then
            createRecvPings proc.ChansSend.Length roundCount
        else
            let x = msg
            let msg = Ping(x, proc.ChanRecvPong)
            let chan, chans = (List.head chans, List.tail chans)

            msg --> chan
            >>= fun _ ->
#if DEBUG
                printfn $"DEBUG: %s{proc.Name} sent ping: %i{x}"
#endif
                createSendPings chans roundCount

    and createRecvPings recvCount roundCount =
        if recvCount = 0 then
            createRecvPongs proc.ChansSend.Length roundCount
        else
            !--> proc.ChanRecvPing
            >>= fun msg ->
                match msg with
                | Ping(x, replyChan) ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} received ping: %i{x}"
#endif
                    let y = x + 1
                    let msgReply = Pong y

                    msgReply --> replyChan
                    >>= fun _ ->
#if DEBUG
                        printfn $"DEBUG: %s{proc.Name} sent pong: %i{y}"
#endif
                        createRecvPings (recvCount - 1) roundCount
                | _ -> failwith "createRecvPings: Received pong when ping should be received!"

    and createRecvPongs recvCount roundCount =
        if recvCount = 0 then
            if roundCount = 0 then
                TimerMessage.Stop --> timerChan
            else
                createSendPings proc.ChansSend (roundCount - 1)
        else
            !--> proc.ChanRecvPong
            >>= fun msg ->
                match msg with
                | Pong x ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} received pong: %i{x}"
#endif
                    createRecvPongs (recvCount - 1) roundCount
                | _ -> failwith "createRecvPongs: Received ping when pong should be received!"

    TimerMessage.Start --> timerChan
    >>= fun _ -> !--> goChan >>= fun _ -> createSendPings proc.ChansSend (roundCount - 1)

let Create processCount roundCount : FIO<int64, obj> =
    let rec createProcesses processCount =
        let rec createRecvChanProcesses processCount acc =
            match processCount with
            | 0 -> acc
            | count ->
                let proc =
                    { Name = $"p{count - 1}"
                      ChanRecvPing = Channel<Message>()
                      ChanRecvPong = Channel<Message>()
                      ChansSend = [] }

                createRecvChanProcesses (count - 1) (acc @ [ proc ])

        let rec create recvChanProcs prevRecvChanProcs acc =
            match recvChanProcs with
            | [] -> acc
            | p :: ps ->
                let otherProcs = prevRecvChanProcs @ ps
                let chansSend = List.map (fun p -> p.ChanRecvPing) otherProcs

                let proc =
                    { Name = p.Name
                      ChanRecvPing = p.ChanRecvPing
                      ChanRecvPong = p.ChanRecvPong
                      ChansSend = chansSend }

                create ps (prevRecvChanProcs @ [ p ]) (proc :: acc)

        let recvChanProcesses = createRecvChanProcesses processCount []
        create recvChanProcesses [] []

    let rec createBig procs msg timerChan goChan acc =
        match procs with
        | [] -> acc
        | p :: ps ->
            let eff = createProcess p msg roundCount timerChan goChan <!> acc
            createBig ps (msg + 10) timerChan goChan eff

    let procs = createProcesses processCount

    let pa, pb, ps =
        match procs with
        | pa :: pb :: ps -> (pa, pb, ps)
        | _ -> failwith $"createBig failed! (at least 2 processes should exist) processCount = %i{processCount}"

    let timerChan = Channel<TimerMessage<int>>()
    let goChan = Channel<int>()

    let effEnd =
        createProcess pa (10 * (processCount - 2)) roundCount timerChan goChan
        <!> createProcess pb (10 * (processCount - 1)) roundCount timerChan goChan

    ! TimerEffect(processCount, processCount, processCount, timerChan)
    >>= fun fiber ->
        (TimerMessage.MessageChannel goChan) --> timerChan
        >>= fun _ ->
            createBig ps 0 timerChan goChan effEnd
            >>= fun _ -> !? fiber >>= fun res -> succeed res