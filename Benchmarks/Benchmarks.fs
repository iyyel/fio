// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Benchmarks

open FSharp.FIO.FIO
open System.Diagnostics
open System.IO
open System

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
            #if DEBUG
            printfn $"%s{proc.Name} sent ping: %i{msg}"
            #endif
            Receive(proc.ChanRecv) >>= fun x ->
            #if DEBUG
            printfn $"%s{proc.Name} received pong: %i{x}"
            #endif
            fioEnd x
        match roundCount with
        | 1 -> template (fun _ -> End())
        | _ -> template (fun msg -> createPingProcess proc msg (roundCount - 1))

    let rec private createPongProcess proc roundCount =
        let template fioEnd =
            Receive(proc.ChanRecv) >>= fun x ->
            #if DEBUG
            printfn $"%s{proc.Name} received ping: %i{x}"
            #endif
            let y = x + 10
            Send(y, proc.ChanSend) >>= fun _ ->
            #if DEBUG
            printfn $"%s{proc.Name} sent pong: %i{y}"
            #endif
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
            #if DEBUG
            printfn $"%s{proc.Name} sent: %i{x}"
            #endif
            Receive(proc.ChanRecv) >>= fun y ->
            #if DEBUG
            printfn $"%s{proc.Name} received: %i{y}"
            #endif
            fioEnd
        let rec create roundCount =
            match roundCount with
            | 1 -> template proc (End())
            | _ -> template proc (create (roundCount - 1))
        create roundCount

    let private createRecvProcess proc roundCount =
        let template proc fioEnd =
            Receive(proc.ChanRecv) >>= fun x ->
            #if DEBUG
            printfn $"%s{proc.Name} received: %i{x}"
            #endif
            let y = x + 10
            Send(y, proc.ChanSend) >>= fun _ ->
            #if DEBUG
            printfn $"%s{proc.Name} sent: %i{y}"
            #endif
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
                   | Pong msgValue ->
                                      #if DEBUG
                                      printfn $"%s{proc.Name} received pong: %i{msgValue}"
                                      #endif
                                      match roundCount with
                                      | count when count <= 1 -> End()
                                      | count                 -> createSendPings proc 0 (count - 1)
                   | _             -> match roundCount with
                                      | count when count <= 1 -> End()
                                      | count                 -> createSendPings proc 0 (count - 1)
            | _ -> Receive(proc.ChanRecvPong) >>= fun msg ->
                   match msg with
                   | Pong msgValue ->
                                      #if DEBUG
                                      printfn $"%s{proc.Name} received pong: %i{msgValue}"
                                      #endif
                                      createRecvPongs proc (recvCount - 1) roundCount
                   | _             -> createRecvPongs proc (recvCount - 1) roundCount
        
        and createRecvPings proc recvCount roundCount =
            let template msgValue replyPongChan fioEnd =
                #if DEBUG
                printfn $"%s{proc.Name} received ping: %i{msgValue}"
                #endif
                let replyValue = msgValue + 1
                let msgReply = Pong replyValue
                Send(msgReply, replyPongChan) >>= fun _ ->
                #if DEBUG
                printfn $"%s{proc.Name} sent pong: %i{replyValue}"
                #endif
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
                #if DEBUG
                printfn $"%s{proc.Name} sent ping: %i{msgValue}"
                #endif
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
                       #if DEBUG
                       printfn $"%s{proc.Name} sent: %i{msg}"
                       #endif
                       End()
            | count -> Send(msg, proc.Chan) >>= fun _ ->
                       #if DEBUG
                       printfn $"%s{proc.Name} sent: %i{msg}"
                       #endif
                       create (count - 1)
        create messageCount

    let private createRecvProcess proc messageCount = 
        let rec create messageCount =
            match messageCount with
            | 1     -> Receive(proc.Chan) >>= fun msg ->
                       #if DEBUG
                       printfn $"%s{proc.Name} received: %i{msg}"
                       #endif
                       End()
            | count -> Receive(proc.Chan) >>= fun msg ->
                       #if DEBUG 
                       printfn $"%s{proc.Name} received: %i{msg}"
                       #endif
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
          ThreadRing: ThreadRingConfig
          Big: BigConfig
          Bang: BangConfig }

    type RuntimeRunFunc = FIO<obj, unit> -> Fiber<obj, unit>

    type BenchmarkResult = string * BenchmarkConfig * string * (int * int64) list

    let private writeResultsToCsv (result : BenchmarkResult) = 
        let configStr config =
            match config with
            | Pingpong config   -> $"roundcount%i{config.RoundCount}"
            | ThreadRing config -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Big config        -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Bang config       -> $"sendercount%i{config.SenderCount}-messagecount%i{config.MessageCount}"

        let headerStr = "Execution Time (ms)"
        let homePath = if (Environment.OSVersion.Platform.Equals(PlatformID.Unix) ||
                           Environment.OSVersion.Platform.Equals(PlatformID.MacOSX))
                       then Environment.GetEnvironmentVariable("HOME")
                       else Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%");
        let (name, config, runtimeName, times) = result
        let dirPath = homePath + @"\fio\benchmarks"
        let configStr = configStr config
        let dateStr = DateTime.Now.ToString("dd-MM-yyyy-HH-mm-ss")
        let fileName = name.ToLower() + "-" + configStr + "-" + runtimeName.ToLower() + "-" + dateStr + ".csv"
        let filePath = dirPath + @"\" + fileName
        let rec fileContentStr times acc =
            match times with
            | []            -> acc
            | (_, time)::ts -> fileContentStr ts (acc + $"%i{time}")

        if (not (Directory.Exists(dirPath))) then
            Directory.CreateDirectory(dirPath) |> ignore
        else ()

        let fileContent = fileContentStr times ""
        printfn $"\nWriting benchmark results to '%s{filePath}'"
        File.WriteAllText(filePath, headerStr + "\n" + fileContent)

    let private printResult (result : BenchmarkResult) =
        let configStr config =
            match config with
            | Pingpong config   -> $"RoundCount: %i{config.RoundCount}"
            | ThreadRing config -> $"ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount}"
            | Big config        -> $"ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount}"
            | Bang config       -> $"SenderCount: %i{config.SenderCount} MessageCount: %i{config.MessageCount}"

        let rec runExecTimesStr runExecTimes acc =
            match runExecTimes with
            | []              -> (acc + "|---------------------------------------------------------------------|")
            | (run, time)::ts -> let str = $"|  #%-10i{run}                 %-35i{time}    |\n"
                                 runExecTimesStr ts (acc + str)

        let (benchName, config, runtimeName, times) = result
        let configStr = configStr config
        let runExecTimesStr = runExecTimesStr times ""
        let benchNameRuntime = benchName + " / " + runtimeName
        let headerStr = $"
|---------------------------------------------------------------------|
|   Benchmark / Runtime                    Configuration              |
|  ---------------------       -------------------------------------  |
|  %-25s{benchNameRuntime}   %-35s{configStr}    |
|---------------------------------------------------------------------|
|           Run                         Execution time (ms)           |
|  ---------------------       -------------------------------------  |\n"
        let toPrint = headerStr + runExecTimesStr
        printfn "%s" toPrint

    let private runBenchmark config runCount runtimeName (run : RuntimeRunFunc) : BenchmarkResult =
        let rec executeBenchmark fioBench curRun acc =
            match curRun with
            | curRun' when curRun' = runCount -> acc
            | curRun'                         -> let time = (timeOperation (fun () -> (run fioBench).Await()))
                                                 let runNum = curRun' + 1
                                                 let result = (runNum, time.millisecondsTaken)
                                                 executeBenchmark fioBench runNum (acc @ [result])
        
        let (benchName, fioBench) = match config with
                                    | Pingpong config   -> ("Pingpong", Pingpong.Run config.RoundCount)
                                    | ThreadRing config -> ("ThreadRing", ThreadRing.Run config.ProcessCount config.RoundCount)
                                    | Big config        -> ("Big", Big.Run config.ProcessCount config.RoundCount)
                                    | Bang config       -> ("Bang", Bang.Run config.SenderCount config.MessageCount)

        let runExecTimes = executeBenchmark fioBench 0 []
        (benchName, config, runtimeName, runExecTimes)

    let Run config runCount runtimeName (run : RuntimeRunFunc) =
        let result = runBenchmark config runCount runtimeName run
        printResult result
        writeResultsToCsv result

    let RunAll configs runCount runtimeName (run : RuntimeRunFunc) =
        let benchConfigs = [Pingpong (configs.Pingpong);
                            ThreadRing (configs.ThreadRing);
                            Big (configs.Big);
                            Bang (configs.Bang)]
        
        let results = List.map (fun config -> runBenchmark config runCount runtimeName run) benchConfigs
 
        for result in results do
            printResult result
            writeResultsToCsv result
