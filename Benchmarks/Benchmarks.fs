// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Benchmarks

open FSharp.FIO.FIO
open System.Threading

// Pingpong benchmark
// Measures: Message delivery overhead
// Savina benchmark #1 (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)
module Pingpong =

    let ping chan =
        let x = 0
        Send(x, chan) >>= fun _ ->
        printfn $"ping sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"ping received: %A{y}"
        End()

    let pong chan =
        Receive(chan) >>= fun x ->
        printfn $"pong received: %A{x}"
        let y = x + 10
        Send(y, chan) >>= fun _ ->
        printfn $"pong sent: %A{y}"
        End()

    let rec pingInf chan =
        let x = 0
        Send(x, chan) >>= fun _ ->
        printfn $"ping sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"ping received: %A{y}"
        pingInf chan
    
    let rec pongInf chan =
        Receive(chan) >>= fun x ->
        printfn $"pong received: %A{x}"
        let y = x + 10
        Send(y, chan) >>= fun _ ->
        printfn $"pong sent: %A{y}"
        pongInf chan

    let Run chan inf =
        if inf then
            Parallel(pingInf chan, pongInf chan) >>= fun _ -> End()
        else
            Parallel(ping chan, pong chan) >>= fun _ -> End()

// ThreadRing benchmark
// Measures: Message sending; Context switching between actors
// Savina benchmark #5 (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)
module ThreadRing = 

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }

    let private createFIOProcess proc first roundCount =
        let rec create roundCount =
            match roundCount with
            | 1 when first -> Receive(proc.ChanRecv) >>= fun x ->
                              printfn $"%s{proc.Name} received: %A{x}"
                              let y = x + 10
                              Send(y, proc.ChanSend) >>= fun _ ->
                              printfn $"%s{proc.Name} sent: %A{y}"
                              Receive(proc.ChanRecv) >>= fun z -> 
                              printfn $"%s{proc.Name} received: %A{z}"
                              End()
            | 1            -> Receive(proc.ChanRecv) >>= fun x ->
                              printfn $"%s{proc.Name} received: %A{x}"
                              let y = x + 10
                              Send(y, proc.ChanSend) >>= fun _ ->
                              printfn $"%s{proc.Name} sent: %A{y}"
                              End()
            | _            -> Receive(proc.ChanRecv) >>= fun x ->
                              printfn $"%s{proc.Name} received: %A{x}"
                              let y = x + 10
                              Send(y, proc.ChanSend) >>= fun _ ->
                              printfn $"%s{proc.Name} sent: %A{y}"
                              create (roundCount - 1)
        create roundCount

    let private createFSProcess proc first roundCount =
        let rec create roundCount =
            match roundCount with
            | 1 when first -> let x = proc.ChanRecv.Receive()
                              printfn $"%s{proc.Name} received: %A{x}"
                              let y = x + 10
                              proc.ChanSend.Send y
                              printfn $"%s{proc.Name} sent: %A{y}"
                              let z = proc.ChanRecv.Receive()
                              printfn $"%s{proc.Name} received: %A{z}"
            | 1            -> let x = proc.ChanRecv.Receive()
                              printfn $"%s{proc.Name} received: %A{x}"
                              let y = x + 10
                              proc.ChanSend.Send y
                              printfn $"%s{proc.Name} sent: %A{y}"
            | _            -> let recv = proc.ChanRecv.Receive()
                              printfn $"%s{proc.Name} received: %A{recv}"
                              let value = recv + 10
                              proc.ChanSend.Send value
                              printfn $"%s{proc.Name} sent: %A{value}"
                              create (roundCount - 1)
        create roundCount

    let private getRecvChan index (chans : Channel<int> list) =
        match index with
        | index when index - 1 < 0 -> chans.Item (List.length chans - 1)
        | index                    -> chans.Item (index - 1)

    let rec private createProcesses chans allChans index acc =
        match chans with
        | []           -> acc
        | chan::chans' -> let proc = {Name = $"p{index}"; ChanSend = chan; ChanRecv = getRecvChan index allChans}
                          createProcesses chans' allChans (index + 1) (acc @ [proc])

    let private injectMessage proc startMsg =
        proc.ChanRecv.Send startMsg

    let RunFIO processCount roundCount =
        let rec createProcessRing procs roundCount first =
            match procs with
            | pa::[pb] -> Parallel(createFIOProcess pa first roundCount, createFIOProcess pb false roundCount)
                          >>= fun _ -> End()
            | p::ps    -> Parallel(createFIOProcess p first roundCount, createProcessRing ps roundCount false)
                          >>= fun _ -> End()
            | _        -> failwith $"createProcessRing failed! (at least 2 processes should exist) m = %A{roundCount}"

        let chans = [for _ in 1..processCount -> Channel<int>()]
        let procs = createProcesses chans chans 0 []
        injectMessage (List.item 0 procs) 0
        createProcessRing procs roundCount true

    let RunFS processCount roundCount =
        let rec createProcessRing procs m first =
            match procs with
            | pa::[pb] -> let task1 = Tasks.Task.Factory.StartNew(fun () -> createFSProcess pa first m)
                          let task2 = Tasks.Task.Factory.StartNew(fun () -> createFSProcess pb false m)
                          task1.Wait()
                          task2.Wait()
            | p::ps    -> let task = Tasks.Task.Factory.StartNew(fun () -> createFSProcess p first m)
                          createProcessRing ps m false
                          task.Wait()
            | _        -> failwith $"createProcessRing failed! (at least 2 processes should exist) m = %A{m}"

        let chans = [for _ in 1..processCount -> Channel<int>()]
        let procs = createProcesses chans chans 0 []
        injectMessage (List.item 0 procs) 0
        createProcessRing procs roundCount true

// Big benchmark
// Measures: Contention on mailbox; Many-to-Many message passing
// Savina benchmark #7 (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)
module Big =

    type private Message =
        | Ping of int * Channel<Message>
        | Pong of int
    
    type private Process =
        { Name: string
          ChanRecvPing: Channel<Message>
          ChanRecvPong: Channel<Message>
          ChansSend: Channel<Message> list
        }

    let private createProcess proc msgValue roundCount =
        let rec createRecvPongs proc recvCount roundCount =
            match recvCount with
            | 1 -> Receive(proc.ChanRecvPong) >>= fun msg ->
                   match msg with
                   | Pong msgValue -> printfn $"%s{proc.Name} received pong: %A{msgValue}"
                                      match roundCount with
                                      | count when count <= 1 -> End()
                                      | count                 -> createSendPings proc 0 (count - 1)
                   | _             -> match roundCount with
                                      | count when count <= 1 -> End()
                                      | count                 -> createSendPings proc 0 (count - 1)
            | _ -> Receive(proc.ChanRecvPong) >>= fun msg ->
                   match msg with
                   | Pong msgValue -> printfn $"%s{proc.Name} received pong: %A{msgValue}"
                                      createRecvPongs proc (recvCount - 1) roundCount
                   | _             -> createRecvPongs proc (recvCount - 1) roundCount
        
        and createRecvPings proc recvCount roundCount =
            let template msgValue replyPongChan fioEnd =
                printfn $"%s{proc.Name} received ping: %A{msgValue}"
                let replyValue = msgValue + 1
                let msgReply = Pong replyValue
                Send(msgReply, replyPongChan) >>= fun _ ->
                printfn $"%s{proc.Name} sent pong: %A{replyValue}"
                fioEnd
            let rec create recvCount = 
                match recvCount with
                | 1 -> Receive(proc.ChanRecvPing) >>= fun msg ->
                       match msg with
                       | Ping (msgValue, replyPongChan) -> template msgValue replyPongChan (createRecvPongs proc proc.ChansSend.Length roundCount)
                       | _                              -> createRecvPongs proc proc.ChansSend.Length roundCount
                | _ -> Receive(proc.ChanRecvPing) >>= fun msg ->
                       match msg with
                       | Ping (msgValue, replyPongChan) -> template msgValue replyPongChan (createRecvPings proc (recvCount - 1) roundCount)                          
                       | _                              -> createRecvPings proc (recvCount - 1) roundCount
            create recvCount
      
        and createSendPings proc msgValue roundCount =
            let template chan fioEnd =
                let msg = Ping (msgValue, proc.ChanRecvPong)
                Send(msg, chan) >>= fun _ ->
                printfn $"%s{proc.Name} sent ping: %A{msgValue}"
                fioEnd
            let rec create chansSend =
                match chansSend with
                | []          -> failwith "createSendPings: Empty list is not supported!"
                | chan::[]    -> template chan (createRecvPings proc proc.ChansSend.Length roundCount)
                | chan::chans -> template chan (create chans)
            create proc.ChansSend

        createSendPings proc msgValue roundCount

    let Run processCount roundCount =
        let rec createProcesses processCount =
            let rec createRecvChanProcesses processCount acc =
                match processCount with
                | 0     -> acc
                | count -> let proc = {Name = $"p{count - 1}";
                                       ChanRecvPing = Channel<Message>();
                                       ChanRecvPong = Channel<Message>();
                                       ChansSend = []}
                           createRecvChanProcesses (count - 1) (acc @ [proc])

            let rec createProcesses' recvChanProcs prevRecvChanProcs acc =
                match recvChanProcs with
                | []    -> acc
                | p::ps -> let otherProcs = prevRecvChanProcs @ ps
                           let chansSend = List.map (fun p' -> p'.ChanRecvPing) otherProcs
                           let proc = {Name = p.Name;
                                       ChanRecvPing = p.ChanRecvPing;
                                       ChanRecvPong = p.ChanRecvPong;
                                       ChansSend = chansSend}
                           createProcesses' ps (prevRecvChanProcs @ [p]) (proc :: acc)

            let recvChanProcesses = createRecvChanProcesses processCount []
            createProcesses' recvChanProcesses [] []

        let rec createBig procs msgValue =
            match procs with
            | pa::[pb] -> Parallel(createProcess pa msgValue roundCount, createProcess pb (msgValue + 10) roundCount) >>= fun _ -> End()
            | p::ps    -> Parallel(createProcess p msgValue roundCount, createBig ps (msgValue + 10)) >>= fun _ -> End()
            | _        -> failwith $"createBig failed! (at least 2 processes should exist) processCount = %A{processCount}"

        let procs = createProcesses processCount
        createBig procs 0
        
// Bang benchmark
// Measures: Contention on mailbox; Many-to-One message passing
// A Scalability Benchmark Suite for Erlang/OTP (https://dl.acm.org/doi/10.1145/2364489.2364495I)
module Bang =

    type private Process =
        { Name: string
          Chan: Channel<int>
        }

    let private createSendProcess proc msg messageCount = 
        let rec create messageCount =
            match messageCount with
            | 1     -> Send(msg, proc.Chan) >>= fun _ ->
                       printfn $"%s{proc.Name} sent: %A{msg}"
                       End()
            | count -> Send(msg, proc.Chan) >>= fun _ ->
                       printfn $"%s{proc.Name} sent: %A{msg}"
                       create (count - 1)
        create messageCount

    let private createRecvProcess proc messageCount = 
        let rec create messageCount =
            match messageCount with
            | 1     -> Receive(proc.Chan) >>= fun msg ->
                       printfn $"%s{proc.Name} received: %A{msg}"
                       End()
            | count -> Receive(proc.Chan) >>= fun msg ->
                       printfn $"%s{proc.Name} received: %A{msg}"
                       create (count - 1)
        create messageCount
            
    let Run senderCount messageCount =
        let rec createSendProcesses recvProcChan senderCount acc =
            match senderCount with
            | 0     -> acc
            | count -> let proc = {Name = $"p{count}"; Chan = recvProcChan}
                       createSendProcesses recvProcChan (count - 1) (proc :: acc)

        let rec createBang recvProc sendProcs msg =
            match sendProcs with
            | p::[] -> Parallel(createSendProcess p msg messageCount, createRecvProcess recvProc (senderCount * messageCount)) >>= fun _ -> End()
            | p::ps -> Parallel(createSendProcess p msg messageCount, createBang recvProc ps (msg + 10)) >>= fun _ -> End()
            | _     -> failwith $"createBang failed! (at least 1 sending process should exist) senderCount = %A{senderCount}"

        let recvProc = {Name = "p0"; Chan = Channel<int>()}
        let sendProcs = createSendProcesses recvProc.Chan senderCount []
        createBang recvProc sendProcs 10
