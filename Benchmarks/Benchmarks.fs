// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Benchmarks

open FSharp.FIO.FIO
open System.Diagnostics

// Pingpong benchmark
// Measures: Message delivery overhead
// Savina benchmark #1 (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)
module Pingpong =

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }

    let rec private createPingProcess proc msg roundCount =
        let template (fioEnd : int -> FIO<'Error, 'Result>) =
            Send(msg, proc.ChanSend) >>= fun _ ->
            printfn $"%s{proc.Name} sent ping: %i{msg}"
            Receive(proc.ChanRecv) >>= fun x ->
            printfn $"%s{proc.Name} received pong: %i{x}"
            fioEnd x
        match roundCount with
        | 1 -> template (fun _ -> End())
        | _ -> template (fun msg -> createPingProcess proc msg (roundCount - 1))

    let rec private createPongProcess proc roundCount =
        let template fioEnd =
            Receive(proc.ChanRecv) >>= fun x ->
            printfn $"%s{proc.Name} received ping: %i{x}"
            let y = x + 10
            Send(y, proc.ChanSend) >>= fun _ ->
            printfn $"%s{proc.Name} sent pong: %i{y}"
            fioEnd
        match roundCount with
        | 1 -> template (End())
        | _ -> template (createPongProcess proc (roundCount - 1))

    let Run roundCount =
        let pingSendChan = Channel<int>()
        let pongSendChan = Channel<int>()
        let pingProc = {Name = "p0"; ChanSend = pingSendChan ; ChanRecv = pongSendChan}
        let pongProc = {Name = "p1"; ChanSend = pongSendChan; ChanRecv = pingSendChan}
        Parallel(createPingProcess pingProc 0 roundCount, createPongProcess pongProc roundCount) >>= fun _ -> End()

// ThreadRing benchmark
// Measures: Message sending; Context switching between actors
// Savina benchmark #5 (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)
module ThreadRing =

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }

    let private createSendProcess proc roundCount =
        let template proc fioEnd =
            let x = 0
            Send(x, proc.ChanSend) >>= fun _ ->
            printfn $"%s{proc.Name} sent: %i{x}"
            Receive(proc.ChanRecv) >>= fun y ->
            printfn $"%s{proc.Name} received: %i{y}"
            fioEnd
        let rec create roundCount =
            match roundCount with
            | 1 -> template proc (End())
            | _ -> template proc (create (roundCount - 1))
        create roundCount

    let private createRecvProcess proc roundCount =
        let template proc fioEnd =
            Receive(proc.ChanRecv) >>= fun x ->
            printfn $"%s{proc.Name} received: %i{x}"
            let y = x + 10
            Send(y, proc.ChanSend) >>= fun _ ->
            printfn $"%s{proc.Name} sent: %i{y}"
            fioEnd
        let rec create roundCount =
            match roundCount with
            | 1 -> template proc (End())
            | _ -> template proc (create (roundCount - 1))
        create roundCount

    let Run processCount roundCount =
        let getRecvChan index (chans : Channel<int> list) =
            match index with
            | index when index - 1 < 0 -> chans.Item (List.length chans - 1)
            | index                    -> chans.Item (index - 1)

        let rec createProcesses chans allChans index acc =
            match chans with
            | []           -> acc
            | chan::chans' -> let proc = {Name = $"p{index}"; ChanSend = chan; ChanRecv = getRecvChan index allChans}
                              createProcesses chans' allChans (index + 1) (proc :: acc)

        let rec createProcessRing procs roundCount =
            match procs with
            | pa::[pb] -> Parallel(createRecvProcess pa roundCount, createSendProcess pb roundCount)
                          >>= fun _ -> End()
            | p::ps    -> Parallel(createRecvProcess p roundCount, createProcessRing ps roundCount)
                          >>= fun _ -> End()
            | _        -> failwith $"createProcessRing failed! (at least 2 processes should exist) processCount = %i{processCount}"

        let chans = [for _ in 1..processCount -> Channel<int>()]
        let procs = createProcesses chans chans 0 []
        createProcessRing procs roundCount

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
                   | Pong msgValue -> printfn $"%s{proc.Name} received pong: %i{msgValue}"
                                      match roundCount with
                                      | count when count <= 1 -> End()
                                      | count                 -> createSendPings proc 0 (count - 1)
                   | _             -> match roundCount with
                                      | count when count <= 1 -> End()
                                      | count                 -> createSendPings proc 0 (count - 1)
            | _ -> Receive(proc.ChanRecvPong) >>= fun msg ->
                   match msg with
                   | Pong msgValue -> printfn $"%s{proc.Name} received pong: %i{msgValue}"
                                      createRecvPongs proc (recvCount - 1) roundCount
                   | _             -> createRecvPongs proc (recvCount - 1) roundCount
        
        and createRecvPings proc recvCount roundCount =
            let template msgValue replyPongChan fioEnd =
                printfn $"%s{proc.Name} received ping: %i{msgValue}"
                let replyValue = msgValue + 1
                let msgReply = Pong replyValue
                Send(msgReply, replyPongChan) >>= fun _ ->
                printfn $"%s{proc.Name} sent pong: %i{replyValue}"
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
                printfn $"%s{proc.Name} sent ping: %i{msgValue}"
                fioEnd
            let rec create (chansSend : Channel<Message> list) =
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

            let rec create recvChanProcs prevRecvChanProcs acc =
                match recvChanProcs with
                | []    -> acc
                | p::ps -> let otherProcs = prevRecvChanProcs @ ps
                           let chansSend = List.map (fun p' -> p'.ChanRecvPing) otherProcs
                           let proc = {Name = p.Name;
                                       ChanRecvPing = p.ChanRecvPing;
                                       ChanRecvPong = p.ChanRecvPong;
                                       ChansSend = chansSend}
                           create ps (prevRecvChanProcs @ [p]) (proc :: acc)

            let recvChanProcesses = createRecvChanProcesses processCount []
            create recvChanProcesses [] []

        let rec createBig procs msgValue =
            match procs with
            | pa::[pb] -> Parallel(createProcess pa msgValue roundCount, createProcess pb (msgValue + 10) roundCount) >>= fun _ -> End()
            | p::ps    -> Parallel(createProcess p msgValue roundCount, createBig ps (msgValue + 10)) >>= fun _ -> End()
            | _        -> failwith $"createBig failed! (at least 2 processes should exist) processCount = %i{processCount}"

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
                       printfn $"%s{proc.Name} sent: %i{msg}"
                       End()
            | count -> Send(msg, proc.Chan) >>= fun _ ->
                       printfn $"%s{proc.Name} sent: %i{msg}"
                       create (count - 1)
        create messageCount

    let private createRecvProcess proc messageCount = 
        let rec create messageCount =
            match messageCount with
            | 1     -> Receive(proc.Chan) >>= fun msg ->
                       printfn $"%s{proc.Name} received: %i{msg}"
                       End()
            | count -> Receive(proc.Chan) >>= fun msg ->
                       printfn $"%s{proc.Name} received: %i{msg}"
                       create (count - 1)
        create messageCount
            
    let Run senderCount messageCount =
        let rec createSendProcesses recvProcChan senderCount =
            List.map (fun count -> {Name = $"p{count}"; Chan = recvProcChan}) [1..senderCount]

        let rec createBang recvProc sendProcs msg =
            match sendProcs with
            | p::[] -> Parallel(createSendProcess p msg messageCount, createRecvProcess recvProc (senderCount * messageCount)) >>= fun _ -> End()
            | p::ps -> Parallel(createSendProcess p msg messageCount, createBang recvProc ps (msg + 10)) >>= fun _ -> End()
            | _     -> failwith $"createBang failed! (at least 1 sending process should exist) senderCount = %i{senderCount}"

        let recvProc = {Name = "p0"; Chan = Channel<int>()}
        let sendProcs = createSendProcesses recvProc.Chan senderCount
        createBang recvProc sendProcs 0

module Benchmark =

    type private TimedOperation<'Result> = {millisecondsTaken : int64; returnedValue : 'Result}

    let private timeOperation<'Result> (func: unit -> 'Result): TimedOperation<'Result> =
        let stopwatch = Stopwatch()
        stopwatch.Start()
        let returnValue = func()
        stopwatch.Stop()
        {millisecondsTaken = stopwatch.ElapsedMilliseconds; returnedValue = returnValue}

    type PingpongConfig = { RoundCount: int }

    type ThreadRingConfig = { ProcessCount: int;
                              RoundCount: int }
    
    type BigConfig = { ProcessCount: int;
                       RoundCount: int }

    type BangConfig = { SenderCount: int;
                        MessageCount: int }

    type BenchmarkConfig =
        | Pingpong of PingpongConfig
        | ThreadRing of ThreadRingConfig
        | Big of BigConfig
        | Bang of BangConfig

    type BenchmarksConfig =
        { Pingpong: PingpongConfig
          Threadring: ThreadRingConfig
          Big: BigConfig
          Bang: BangConfig }

    let private printBenchmarkResults (results : string * BenchmarkConfig * int64 * (int * int64) list) =
        let benchmarkConfigString config =
            match config with
            | Pingpong config   -> $"RoundCount: %i{config.RoundCount}"
            | ThreadRing config -> $"ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount}"
            | Big config        -> $"ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount}"
            | Bang config       -> $"SenderCount: %i{config.SenderCount} MessageCount: %i{config.MessageCount}"

        let rec getTimesStr times average first acc =
            match times with
            | []              -> let str = "|------------------------------------------------------------------|"
                                 (acc + str)
            | (run, time)::ts -> let avg = if first then ((string) average) else ""
                                 let str = $"|  #%-6i{run}     %-23i{time}     %-23s{avg} |\n"
                                 getTimesStr ts average false (acc + str)

        let (name, config, average, times) = results
        let configStr = benchmarkConfigString config
        let timesStr = getTimesStr times average true ""
        let headerStr = $"
|------------------------------------------------------------------|
|     Benchmark                  Configuration                     |
|  ---------------    -----------------------------------          |
|  %-15s{name}    %-35s{configStr}          |
|------------------------------------------------------------------|
|    Run         Execution time (ms)         Average (ms)          |
|  -------     -----------------------     ----------------        |\n"

        let toPrint = headerStr + timesStr
        printfn "%s" toPrint

    let private executeBenchmark config runCount (run : FIO<obj, unit> -> Fiber<obj, unit>) =
        let rec runBenchmark bench runCount acc =
            match runCount with
            | count when count <= 0 -> acc
            | count                 -> let time = (timeOperation (fun () -> (run bench).Await()))
                                       let result = (count, time.millisecondsTaken)
                                       runBenchmark bench (count - 1) (result :: acc)
        
        let (name, bench) = match config with
                            | Pingpong config   -> ("Pingpong", Pingpong.Run config.RoundCount)
                            | ThreadRing config -> ("ThreadRing", ThreadRing.Run config.ProcessCount config.RoundCount)
                            | Big config        -> ("Big", Big.Run config.ProcessCount config.RoundCount)
                            | Bang config       -> ("Bang", Bang.Run config.SenderCount config.MessageCount)

        let times = runBenchmark bench runCount []

        let average = let times = List.map (fun (_, time) -> time) times
                      let sum = List.sum times
                      sum / (int64) times.Length

        let results = (name, config, average, times)
        results

    let Run config runCount (run : FIO<obj, unit> -> Fiber<obj, unit>) =
        let results = executeBenchmark config runCount run
        printBenchmarkResults results

    let RunAll configs runCount (run : FIO<obj, unit> -> Fiber<obj, unit>) =
        let benchConfigs = [Pingpong (configs.Pingpong);
                            ThreadRing (configs.Threadring);
                            Big (configs.Big);
                            Bang (configs.Bang)]

        let results = List.map (fun config -> executeBenchmark config runCount run) benchConfigs

        for result in results do
            printBenchmarkResults result
