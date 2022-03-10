// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Benchmarks

open FSharp.FIO.FIO

open System
open System.IO
open System.Threading
open System.Diagnostics

module internal Timer =

    type internal TimerMessage =
        | Start
        | Stop

    type internal TimerTask(startCount, stopCount) =
        let chan = Channel<TimerMessage>()
        let task = Tasks.Task.Factory.StartNew(fun () ->
            let stopwatch = Stopwatch()

            let rec loopStart count =
                match count with
                | 0     ->
                           #if DEBUG
                           printfn "TimerTask: Started!"
                           #endif
                           stopwatch.Start()
                | count -> match chan.Receive() with
                           | Start -> loopStart (count - 1)
                           | _     -> loopStart count

            let rec loopStop count =
                match count with
                | 0     ->
                           #if DEBUG
                           printfn "TimerTask: Stopped!"
                           #endif
                           stopwatch.Stop()
                | count -> match chan.Receive() with
                           | Stop -> loopStop (count - 1)
                           | _    -> loopStop count

            loopStart startCount
            loopStop stopCount
            stopwatch.ElapsedMilliseconds)

        member internal _.Chan() = chan

        member internal _.Result() = task.Result

// Pingpong benchmark
// Measures: Message delivery overhead
// Savina benchmark #1 (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)
module Pingpong =

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }
        
    let private createPingProcess proc roundCount (timerTask : Timer.TimerTask) =
        let rec create msg roundCount =
            if roundCount = 0 then
                Send(Timer.Stop, timerTask.Chan()) >>= fun _ ->
                Succeed (timerTask.Result())
            else
                Send(msg, proc.ChanSend) >>= fun _ ->
                #if DEBUG
                printfn $"%s{proc.Name} sent ping: %i{msg}"
                #endif
                Receive(proc.ChanRecv) >>= fun x ->
                #if DEBUG
                printfn $"%s{proc.Name} received pong: %i{x}"
                #endif
                create x (roundCount - 1)
        Send(Timer.Start, timerTask.Chan()) >>= fun _ ->
        create 0 roundCount

    let private createPongProcess proc roundCount =
        let rec create roundCount =
            if roundCount = 0 then
                Succeed 0 >>= fun _ -> End()
            else
                Receive(proc.ChanRecv) >>= fun x ->
                #if DEBUG
                printfn $"%s{proc.Name} received ping: %i{x}"
                #endif
                let y = x + 10
                Send(y, proc.ChanSend) >>= fun _ ->
                #if DEBUG
                printfn $"%s{proc.Name} sent pong: %i{y}"
                #endif
                create (roundCount - 1)
        create roundCount

    let Create roundCount : FIO<obj, int64> =
        let pingSendChan = Channel<int>()
        let pongSendChan = Channel<int>()
        let pingProc = {Name = "p0"; ChanSend = pingSendChan; ChanRecv = pongSendChan}
        let pongProc = {Name = "p1"; ChanSend = pongSendChan; ChanRecv = pingSendChan}
        let timerTask = new Timer.TimerTask(1, 1)
        Parallel(createPingProcess pingProc roundCount timerTask, createPongProcess pongProc roundCount)
        >>= fun (res, _) -> match res with
                            | Success res -> Succeed res
                            | Error error -> Fail error

// ThreadRing benchmark
// Measures: Message sending; Context switching between actors
// Savina benchmark #5 (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)
module ThreadRing =

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }

    let private createSendProcess proc roundCount (timerTask : Timer.TimerTask) =
        let rec create msg roundCount =
            if roundCount = 0 then
                Send(Timer.Stop, timerTask.Chan()) >>= fun _ ->
                Succeed (timerTask.Result())
            else
                Send(msg, proc.ChanSend) >>= fun _ ->
                #if DEBUG
                printfn $"%s{proc.Name} sent: %i{msg}"
                #endif
                Receive(proc.ChanRecv) >>= fun x ->
                #if DEBUG
                printfn $"%s{proc.Name} received: %i{x}"
                #endif
                create x (roundCount - 1)
        Send(Timer.Start, timerTask.Chan()) >>= fun _ ->
        create 0 roundCount

    let private createRecvProcess proc roundCount =
        let rec create roundCount =
            if roundCount = 0 then
                Succeed 0 >>= fun _ -> End()
            else
                Receive(proc.ChanRecv) >>= fun x ->
                #if DEBUG
                printfn $"%s{proc.Name} received: %i{x}"
                #endif
                let y = x + 10
                Send(y, proc.ChanSend) >>= fun _ ->
                #if DEBUG
                printfn $"%s{proc.Name} sent: %i{y}"
                #endif
                create (roundCount - 1)
        create roundCount

    let Create processCount roundCount : FIO<obj, int64> =
        let getRecvChan index (chans : Channel<int> list) =
            match index with
            | index when index - 1 < 0 -> chans.Item (List.length chans - 1)
            | index                    -> chans.Item (index - 1)

        let rec createProcesses chans allChans index acc =
            match chans with
            | []          -> acc
            | chan::chans -> let proc = {Name = $"p{index}"; ChanSend = chan; ChanRecv = getRecvChan index allChans}
                             createProcesses chans allChans (index + 1) (acc @ [proc])

        let rec createThreadRing procs (timerTask : Timer.TimerTask) acc =
            match procs with
            | []    -> acc
            | p::ps -> let fio = Parallel(createRecvProcess p roundCount, acc)
                                 >>= fun (_, res) -> match res with
                                                     | Success res -> Succeed res
                                                     | Error error -> Fail error
                       createThreadRing ps timerTask fio

        let chans = [for _ in 1..processCount -> Channel<int>()]
        let procs = createProcesses chans chans 0 []
        let (pa, pb, ps) = match procs with
                           | pa::pb::ps -> (pa, pb, ps)
                           | _          -> failwith $"createProcessRing failed! (at least 2 processes should exist) processCount = %i{processCount}"
        let timerTask = new Timer.TimerTask(1, 1)
        let fioEnd = Parallel(createRecvProcess pb roundCount, createSendProcess pa roundCount timerTask)
                     >>= fun (_, res) -> match res with
                                         | Success res -> Succeed res
                                         | Error error -> Fail error
        createThreadRing ps timerTask fioEnd

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

    let private createProcess proc msg roundCount (timerTask : Timer.TimerTask) =
        let rec createSendPings chans roundCount =
            if List.length chans = 0 then
                createRecvPings proc.ChansSend.Length roundCount
            else
                let x = msg
                let msg = Ping (x, proc.ChanRecvPong)
                let (chan, chans) = (List.head chans, List.tail chans)
                Succeed msg >>= fun msg -> Send(msg, chan) >>= fun _ ->
                #if DEBUG
                printfn $"%s{proc.Name} sent ping: %i{x}"
                #endif
                createSendPings chans roundCount

        and createRecvPings recvCount roundCount =
            if recvCount = 0 then
                createRecvPongs proc.ChansSend.Length roundCount
            else 
                Receive(proc.ChanRecvPing) >>= fun msg ->
                match msg with
                | Ping (x, replyChan) ->
                                        #if DEBUG
                                        printfn $"%s{proc.Name} received ping: %i{x}"
                                        #endif
                                        let y = x + 1
                                        let msgReply = Pong y
                                        Send(msgReply, replyChan) >>= fun _ ->
                                        #if DEBUG
                                        printfn $"%s{proc.Name} sent pong: %i{y}"
                                        #endif
                                        createRecvPings (recvCount - 1) roundCount
                | _                  -> failwith "createRecvPings: Received pong when ping should be received!"
       
        and createRecvPongs recvCount roundCount =
            if recvCount = 0 then
                if roundCount = 0 then
                    Succeed (Pong 0) >>= fun _ -> 
                    Send(Timer.Stop, timerTask.Chan()) >>= fun _ ->
                    Succeed (timerTask.Result())
                else
                    createSendPings proc.ChansSend (roundCount - 1)
            else 
                Receive(proc.ChanRecvPong) >>= fun msg ->
                match msg with
                | Pong x ->
                            #if DEBUG
                            printfn $"%s{proc.Name} received pong: %i{x}"
                            #endif
                            createRecvPongs (recvCount - 1) roundCount
                | _      -> failwith "createRecvPongs: Received ping when pong should be received!"

        Send(Timer.Start, timerTask.Chan()) >>= fun _ ->
        createSendPings proc.ChansSend (roundCount - 1)
        
    let Create processCount roundCount : FIO<obj, int64> =
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
                           let chansSend = List.map (fun p -> p.ChanRecvPing) otherProcs
                           let proc = {Name = p.Name;
                                       ChanRecvPing = p.ChanRecvPing;
                                       ChanRecvPong = p.ChanRecvPong;
                                       ChansSend = chansSend}
                           create ps (prevRecvChanProcs @ [p]) (proc :: acc)

            let recvChanProcesses = createRecvChanProcesses processCount []
            create recvChanProcesses [] []

        let rec createBig procs msg timerTask acc =
            match procs with
            | []    -> acc
            | p::ps -> let fio = Parallel(createProcess p msg roundCount timerTask, acc)
                                 >>= fun (res, _) -> match res with
                                                     | Success res -> Succeed res
                                                     | Error error -> Fail error
                       createBig ps (msg + 10) timerTask fio
         
        let procs = createProcesses processCount
        let timerTask = new Timer.TimerTask(processCount, processCount)
        let (pa, pb, ps) = match procs with
                           | pa::pb::ps -> (pa, pb, ps)
                           | _          -> failwith $"createBig failed! (at least 2 processes should exist) processCount = %i{processCount}"
        let fioEnd = Parallel(createProcess pa (10 * (processCount - 2)) roundCount timerTask,
                              createProcess pb (10 * (processCount - 1)) roundCount timerTask)
                     >>= fun (res, _) -> match res with
                                         | Success res -> Succeed res
                                         | Error error -> Fail error
        createBig ps 0 timerTask fioEnd
        
// Bang benchmark
// Measures: Contention on mailbox; Many-to-One message passing
// A Scalability Benchmark Suite for Erlang/OTP (https://dl.acm.org/doi/10.1145/2364489.2364495I)
module Bang =

    type private Process =
        { Name: string
          Chan: Channel<int>
        }

    let rec private createSendProcess proc msg roundCount =
        if roundCount = 0 then
            Succeed () >>= fun _ -> End()
        else
            Send(msg, proc.Chan) >>= fun _ ->
            #if DEBUG
            printfn $"%s{proc.Name} sent: %i{msg}"
            #endif
            createSendProcess proc (msg + 10) (roundCount - 1)
  
    let rec private createRecvProcess proc roundCount (timerTask : Timer.TimerTask) =
        if roundCount = 0 then
            Succeed 0 >>= fun _ ->
            Send(Timer.Stop, timerTask.Chan()) >>= fun _ ->
            Succeed (timerTask.Result())
        else
            Receive(proc.Chan) >>= fun msg ->
            #if DEBUG
            printfn $"%s{proc.Name} received: %i{msg}"
            #endif
            createRecvProcess proc (roundCount - 1) timerTask

    let Create processCount roundCount : FIO<obj, int64> =
        let rec createSendProcesses recvProcChan senderCount =
            List.map (fun count -> {Name = $"p{count}"; Chan = recvProcChan}) [1..senderCount]

        let rec createBang recvProc sendProcs acc =
            match sendProcs with
            | []    -> acc
            | p::ps -> let fio = Parallel(createSendProcess p 0 roundCount, acc)
                                 >>= fun (_, res) -> match res with
                                                     | Success res -> Succeed res
                                                     | Error error -> Fail error
                       createBang recvProc ps fio

        let recvProc = {Name = "p0"; Chan = Channel<int>()}
        let sendProcs = createSendProcesses recvProc.Chan processCount
        let (p, ps) = match List.rev sendProcs with
                      | p::ps -> (p, List.rev ps)
                      | _     -> failwith $"createBig failed! (at least 1 sending process should exist) processCount = %i{processCount}"
        let timerTask = new Timer.TimerTask(1, 1)
        let fioEnd = Parallel(createSendProcess p 0 roundCount,
                              createRecvProcess recvProc (processCount * roundCount) timerTask)
                     >>= fun (_, res) -> match res with
                                         | Success res -> Succeed res
                                         | Error error -> Fail error
        Send(Timer.Start, timerTask.Chan()) >>= fun _ ->
        createBang recvProc ps fioEnd

//
// Benchmark assessment functions
//
module Benchmark =

    type PingpongConfig = { RoundCount: int }

    type ThreadRingConfig = { ProcessCount: int;
                              RoundCount: int }
    
    type BigConfig = { ProcessCount: int;
                       RoundCount: int }

    type BangConfig = { ProcessCount: int;
                        RoundCount: int }

    type BenchmarkConfig =
        | Pingpong of PingpongConfig
        | ThreadRing of ThreadRingConfig
        | Big of BigConfig
        | Bang of BangConfig

    type RuntimeRunFunc = FIO<obj, int64> -> Fiber<obj, int64>

    type BenchmarkResult = string * BenchmarkConfig * string * (int * int64) list

    let private writeResultsToCsv (result : BenchmarkResult) = 
        let configStr config =
            match config with
            | Pingpong config   -> $"roundcount%i{config.RoundCount}"
            | ThreadRing config -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Big config        -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Bang config       -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"

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
            | (_, time)::ts -> fileContentStr ts (acc + $"%i{time}\n")

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
            | Bang config       -> $"ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount}"

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
        let createBenchmark config =
            match config with
            | Pingpong config   -> ("Pingpong", Pingpong.Create config.RoundCount)
            | ThreadRing config -> ("ThreadRing", ThreadRing.Create config.ProcessCount config.RoundCount)
            | Big config        -> ("Big", Big.Create config.ProcessCount config.RoundCount)
            | Bang config       -> ("Bang", Bang.Create config.ProcessCount config.RoundCount)

        let rec executeBenchmark (benchName, fioBench) curRun acc =
            match curRun with
            | curRun' when curRun' = runCount -> (benchName, acc)
            | curRun'                         -> let result = (run fioBench).Await()
                                                 let time = match result with
                                                            | Success time -> time
                                                            | Error _      -> -1
                                                 let runNum = curRun' + 1
                                                 let result = (runNum, time)
                                                 executeBenchmark (createBenchmark config) runNum (acc @ [result])
        
        let (benchName, runExecTimes) = executeBenchmark (createBenchmark config) 0 []
        (benchName, config, runtimeName, runExecTimes)

    let Run configs runCount runtimeName (run : RuntimeRunFunc) =
        let results = List.map (fun config -> runBenchmark config runCount runtimeName run) configs
 
        for result in results do
            printResult result
            writeResultsToCsv result
