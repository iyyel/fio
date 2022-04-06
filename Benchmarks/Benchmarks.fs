(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace Benchmarks

open FSharp.FIO.FIO
open FSharp.FIO.Runtime

open System
open System.IO
open System.Diagnostics

module internal Timer =

    type internal TimerMessage =
        | Start
        | Stop

    let Effect startCount stopCount chan =
        let stopwatch = Stopwatch()

        let rec loopStart count =
            match count with
            | 0 ->
                #if DEBUG
                printfn "DEBUG: TimerEffect: Timer started!"
                #endif
                NonBlocking (fun () -> stopwatch.Start(); Ok ())
            | count ->
                Blocking (chan) >> fun res ->
                match res with
                | Start -> loopStart (count - 1)
                | _ -> loopStart count

        let rec loopStop count =
            match count with
            | 0 ->
                #if DEBUG
                printfn "DEBUG: TimerEffect: Timer stopped!"
                #endif
                NonBlocking (fun () -> stopwatch.Stop(); Ok ())
            | count ->
                Blocking (chan) >> fun res ->
                match res with
                | Stop -> loopStop (count - 1)
                | _ -> loopStop count

        loopStart startCount >> fun _ ->
        loopStop stopCount >> fun _ ->
        Success stopwatch.ElapsedMilliseconds

(**********************************************************************************)
(* Pingpong benchmark                                                             *)
(* Measures: Message delivery overhead                                            *)
(* Savina benchmark #1                                                            *)
(* (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)       *)
(**********************************************************************************)
module Pingpong =
    
    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int> }

    let private createPingProcess proc roundCount timerChan =
        let rec create msg roundCount =
            if roundCount = 0 then
                Send(Timer.Stop, timerChan)
            else
                Send(msg, proc.ChanSend) >> fun _ ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} sent ping: %i{msg}"
                #endif
                Receive proc.ChanRecv >> fun x ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} received pong: %i{x}"
                #endif
                create x (roundCount - 1)
        Send(Timer.Start, timerChan) >> fun _ ->
        create 0 roundCount
        
    let private createPongProcess proc roundCount =
        let rec create roundCount =
            if roundCount = 0 then
                End()
            else
                Receive proc.ChanRecv >> fun x ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} received ping: %i{x}"
                #endif
                let y = x + 10
                Send(y, proc.ChanSend) >> fun _ ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} sent pong: %i{y}"
                #endif
                create (roundCount - 1)
        create roundCount
        
    let Create roundCount : FIO<int64, obj> =
        let pingSendChan = Channel<int>()
        let pongSendChan = Channel<int>()
        let pingProc = { Name = "p0"; ChanSend = pingSendChan; ChanRecv = pongSendChan }
        let pongProc = { Name = "p1"; ChanSend = pongSendChan; ChanRecv = pingSendChan }
        let timerChan = Channel<Timer.TimerMessage>()
        Spawn (Timer.Effect 1 1 timerChan) >> fun fiber ->
        Parallel(createPingProcess pingProc roundCount timerChan, createPongProcess pongProc roundCount) >> fun (_, _) ->
        Await fiber >> fun res ->
        Success res

(**********************************************************************************)
(* ThreadRing benchmark                                                           *)
(* Measures: Message sending; Context switching between actors                    *)
(* Savina benchmark #5                                                            *)
(* (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)       *)
(**********************************************************************************)
module ThreadRing =

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int> }

    let private createSendProcess proc roundCount timerChan =
        let rec create msg roundCount =
            if roundCount = 0 then
                Send(Timer.Stop, timerChan)
            else
                Send(msg, proc.ChanSend) >> fun _ ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} sent: %i{msg}"
                #endif
                Receive proc.ChanRecv >> fun x ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} received: %i{x}"
                #endif
                create x (roundCount - 1)
        Send(Timer.Start, timerChan) >> fun _ ->
        create 0 roundCount

    let private createRecvProcess proc roundCount =
        let rec create roundCount =
            if roundCount = 0 then
                End()
            else
                Receive proc.ChanRecv >> fun x ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} received: %i{x}"
                #endif
                let y = x + 10
                Send(y, proc.ChanSend) >> fun _ ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} sent: %i{y}"
                #endif
                create (roundCount - 1)
        create roundCount
        
    let Create processCount roundCount : FIO<int64, obj> =
        let getRecvChan index (chans: Channel<int> list) =
            match index with
            | index when index - 1 < 0 -> chans.Item(List.length chans - 1)
            | index                    -> chans.Item(index - 1)

        let rec createProcesses chans allChans index acc =
            match chans with
            | [] -> acc
            | chan::chans ->
                let proc = { Name = $"p{index}"; ChanSend = chan; ChanRecv = getRecvChan index allChans }
                createProcesses chans allChans (index + 1) (acc @ [proc])

        let rec createThreadRing procs acc =
            match procs with
            | [] -> acc
            | p::ps ->
                let eff = Parallel(createRecvProcess p roundCount, acc) >> fun (_, _) -> End()
                createThreadRing ps eff

        let chans = [for _ in 1 .. processCount -> Channel<int>()]
        let procs = createProcesses chans chans 0 []
        let pa, pb, ps =
            match procs with
            | pa::pb::ps -> (pa, pb, ps)
            | _ ->
                failwith $"createProcessRing failed! (at least 2 processes should exist) processCount = %i{processCount}"
        let timerChan = Channel<Timer.TimerMessage>()
        let effEnd = Parallel(createRecvProcess pb roundCount, createSendProcess pa roundCount timerChan)
                     >> fun (_, _) -> End()
        Spawn (Timer.Effect 1 1 timerChan) >> fun fiber ->
        createThreadRing ps effEnd >> fun _ ->
        Await fiber >> fun res ->
        Success res

(**********************************************************************************)
(* Big benchmark                                                                  *)
(* Measures: Contention on mailbox; Many-to-Many message passing                  *)
(* Savina benchmark #7                                                            *)
(* (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)       *)
(**********************************************************************************)
module Big =

    type private Message =
        | Ping of int * Channel<Message>
        | Pong of int

    type private Process =
        { Name: string
          ChanRecvPing: Channel<Message>
          ChanRecvPong: Channel<Message>
          ChansSend: Channel<Message> list }

    let private createProcess proc msg roundCount timerChan =
        let rec createSendPings chans roundCount =
            if List.length chans = 0 then
                createRecvPings proc.ChansSend.Length roundCount
            else
                let x = msg
                let msg = Ping(x, proc.ChanRecvPong)
                let chan, chans = (List.head chans, List.tail chans)
                Send(msg, chan) >> fun _ ->
                #if DEBUG
                printfn $"DEBUG: %s{proc.Name} sent ping: %i{x}"
                #endif
                createSendPings chans roundCount

        and createRecvPings recvCount roundCount =
            if recvCount = 0 then
                createRecvPongs proc.ChansSend.Length roundCount
            else
                Receive proc.ChanRecvPing >> fun msg ->
                    match msg with
                    | Ping (x, replyChan) ->
                        #if DEBUG
                        printfn $"DEBUG: %s{proc.Name} received ping: %i{x}"
                        #endif
                        let y = x + 1
                        let msgReply = Pong y
                        Send(msgReply, replyChan) >> fun _ ->
                        #if DEBUG
                        printfn $"DEBUG: %s{proc.Name} sent pong: %i{y}"
                        #endif
                        createRecvPings (recvCount - 1) roundCount
                    | _ -> failwith "createRecvPings: Received pong when ping should be received!"

        and createRecvPongs recvCount roundCount =
            if recvCount = 0 then
                if roundCount = 0 then
                    Send(Timer.Stop, timerChan)
                else
                    createSendPings proc.ChansSend (roundCount - 1)
            else
                Receive proc.ChanRecvPong >> fun msg ->
                    match msg with
                    | Pong x ->
                        #if DEBUG
                        printfn $"DEBUG: %s{proc.Name} received pong: %i{x}"
                        #endif
                        createRecvPongs (recvCount - 1) roundCount
                    | _ -> failwith "createRecvPongs: Received ping when pong should be received!"

        Send(Timer.Start, timerChan) >> fun _ ->
        createSendPings proc.ChansSend (roundCount - 1)

    let Create processCount roundCount : FIO<int64, obj> =
        let rec createProcesses processCount =
            let rec createRecvChanProcesses processCount acc =
                match processCount with
                | 0 -> acc
                | count -> 
                    let proc = { Name = $"p{count - 1}"
                                        ChanRecvPing = Channel<Message>()
                                        ChanRecvPong = Channel<Message>()
                                        ChansSend = [] }
                    createRecvChanProcesses (count - 1) (acc @ [proc])

            let rec create recvChanProcs prevRecvChanProcs acc =
                match recvChanProcs with
                | [] -> acc
                | p::ps -> let otherProcs = prevRecvChanProcs @ ps
                           let chansSend = List.map (fun p -> p.ChanRecvPing) otherProcs
                           let proc = { Name = p.Name
                                        ChanRecvPing = p.ChanRecvPing
                                        ChanRecvPong = p.ChanRecvPong
                                        ChansSend = chansSend }
                           create ps (prevRecvChanProcs @ [p]) (proc::acc)

            let recvChanProcesses = createRecvChanProcesses processCount []
            create recvChanProcesses [] []

        let rec createBig procs msg timerChan acc =
            match procs with
            | [] -> acc
            | p::ps -> let eff = Parallel(createProcess p msg roundCount timerChan, acc)
                                 >> fun (_, _) -> End()
                       createBig ps (msg + 10) timerChan eff

        let procs = createProcesses processCount
        let timerChan = Channel<Timer.TimerMessage>()
        let pa, pb, ps = 
            match procs with
            | pa::pb::ps -> (pa, pb, ps)
            | _ -> 
            failwith $"createBig failed! (at least 2 processes should exist) processCount = %i{processCount}"
        let effEnd = Parallel(createProcess pa (10 * (processCount - 2)) roundCount timerChan,
                              createProcess pb (10 * (processCount - 1)) roundCount timerChan)
                     >> fun (_, _) -> End()
        Spawn (Timer.Effect processCount processCount timerChan) >> fun fiber ->
        createBig ps 0 timerChan effEnd >> fun _ ->
        Await fiber >> fun res ->
        Success res

(**********************************************************************************)
(* Bang benchmark                                                                 *)
(* Measures: Contention on mailbox; Many-to-One message passing                   *)
(* A Scalability Benchmark Suite for Erlang/OTP                                   *)
(* (https://dl.acm.org/doi/10.1145/2364489.2364495I)                              *)
(**********************************************************************************)
module Bang =

    type private Process =
        { Name: string
          Chan: Channel<int> }

    let rec private createSendProcess proc msg roundCount =
        if roundCount = 0 then
            End()
        else
            Send(msg, proc.Chan) >> fun _ ->
            #if DEBUG
            printfn $"DEBUG: %s{proc.Name} sent: %i{msg}"
            #endif
            createSendProcess proc (msg + 10) (roundCount - 1)
            
    let rec private createRecvProcess proc roundCount timerChan =
        if roundCount = 0 then
            Send(Timer.Stop, timerChan)
        else
            Receive proc.Chan >> fun x ->
            #if DEBUG
            printfn $"DEBUG: %s{proc.Name} received: %i{x}"
            #endif
            createRecvProcess proc (roundCount - 1) timerChan

    let Create processCount roundCount : FIO<int64, obj> =
        let rec createSendProcesses recvProcChan processCount =
            List.map (fun count -> { Name = $"p{count}"; Chan = recvProcChan }) [1..processCount]

        let rec createBang recvProc sendProcs msg acc =
            match sendProcs with
            | [] -> acc
            | p::ps -> let eff = Parallel(createSendProcess p msg roundCount, acc) >> 
                                 fun (_, _) -> End()
                       createBang recvProc ps (msg + 10) eff

        let recvProc = { Name = "p0"; Chan = Channel<int>() }
        let sendProcs = createSendProcesses recvProc.Chan processCount
        let p, ps = 
            match List.rev sendProcs with
            | p::ps -> (p, ps)
            | _     -> failwith $"createBang failed! (at least 1 sending process should exist) processCount = %i{processCount}"
        let timerChan = Channel<Timer.TimerMessage>()
        let effEnd = Parallel(createSendProcess p 0 roundCount,
                              createRecvProcess recvProc (processCount * roundCount) timerChan)
                     >> fun (_, _) -> End()
        Spawn (Timer.Effect 1 1 timerChan) >> fun fiber ->
        Send(Timer.Start, timerChan) >> fun _ ->
        createBang recvProc ps 10 effEnd >> fun _ ->
        Await fiber >> fun res ->
        Success res

(**********************************************************************************)
(*                                                                                *)
(* ReverseBang                                                                    *)
(*                                                                                *)
(**********************************************************************************)
module ReverseBang =
    
    type private Process =
        { Name: string
          Chan: Channel<int> }

    let rec private createSendProcess proc msg roundCount =
        if roundCount = 0 then
            End()
        else
            Send(msg, proc.Chan) >> fun _ ->
            #if DEBUG
            printfn $"DEBUG: %s{proc.Name} sent: %i{msg}"
            #endif
            createSendProcess proc (msg + 10) (roundCount - 1)
            
    let rec private createRecvProcess proc roundCount timerChan =
        if roundCount = 0 then
            Send(Timer.Stop, timerChan)
        else
            Receive proc.Chan >> fun x ->
            #if DEBUG
            printfn $"DEBUG: %s{proc.Name} received: %i{x}"
            #endif
            createRecvProcess proc (roundCount - 1) timerChan

    let Create processCount roundCount : FIO<int64, obj> =
        let rec createRecvProcesses sendProcChan processCount =
            List.map (fun count -> { Name = $"p{count}"; Chan = sendProcChan }) [1..processCount]

        let rec createReverseBang sendProc sendProcs timerChan acc =
            match sendProcs with
            | [] -> acc
            | p::ps -> let eff = Parallel(createRecvProcess p roundCount timerChan, acc) >> 
                                 fun (_, _) -> End()
                       createReverseBang sendProc ps timerChan eff

        let sendProc = { Name = "p0"; Chan = Channel<int>() }
        let recvProcs = createRecvProcesses sendProc.Chan processCount

        let p, ps = 
            match List.rev recvProcs with
            | p::ps -> (p, ps)
            | _     -> failwith $"createBang failed! (at least 1 sending process should exist) processCount = %i{processCount}"
        let timerChan = Channel<Timer.TimerMessage>()
        let effEnd = Parallel(createRecvProcess p roundCount timerChan,
                              createSendProcess sendProc 0 (processCount * roundCount))
                     >> fun (_, _) -> End()
        Spawn (Timer.Effect 1 processCount timerChan) >> fun fiber ->
        Send(Timer.Start, timerChan) >> fun _ ->
        createReverseBang sendProc ps timerChan effEnd >> fun _ ->
        Await fiber >> fun res ->
        Success res

(**********************************************************************************)
(*                                                                                *)
(* Benchmark infrastructure functionality                                         *)
(*                                                                                *)
(**********************************************************************************)
module Benchmark =

    type PingpongConfig = 
        { RoundCount: int }

    and ThreadRingConfig = 
        { ProcessCount: int
          RoundCount: int }

    and BigConfig = 
        { ProcessCount: int
          RoundCount: int }

    and BangConfig = 
        { ProcessCount: int
          RoundCount: int }

    and ReverseBangConfig = 
        { ProcessCount: int
          RoundCount: int }

    and BenchmarkConfig =
        | Pingpong of PingpongConfig
        | ThreadRing of ThreadRingConfig
        | Big of BigConfig
        | Bang of BangConfig
        | ReverseBang of ReverseBangConfig

    type EvalFunc = FIO<int64, obj> -> Fiber<int64, obj>

    type BenchmarkResult = string * BenchmarkConfig * string * string * (int * int64) list

    let private writeResultsToCsv (result: BenchmarkResult) =
        let configStr config =
            match config with
            | Pingpong config ->
                $"roundcount%i{config.RoundCount}"
            | ThreadRing config ->
                $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Big config ->
                $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Bang config ->
                $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | ReverseBang config ->
                $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"

        let rec fileContentStr times acc =
            match times with
            | [] -> acc
            | (_, time) :: ts -> fileContentStr ts (acc + $"%i{time}\n")

        let headerStr = "Execution Time (ms)"
        let homePath =
            if (Environment.OSVersion.Platform.Equals(PlatformID.Unix)
                || Environment.OSVersion.Platform.Equals(PlatformID.MacOSX)) then
                Environment.GetEnvironmentVariable("HOME")
            else
                Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%")
        let benchName, config, runtimeFileName, _, times = result

        let configStr = configStr config
        let runStr = times.Length.ToString() + "runs"
        let folderName =
            benchName.ToLower()
            + "-"
            + configStr
            + "-"
            + runStr
        let fileName = folderName + "-" + runtimeFileName.ToLower() + "-" + DateTime.Now.ToString("dd-MM-yyyy-HH-mm-ss") + ".csv"
        let dirPath = homePath + @"\fio\benchmarks\" + folderName + @"\" + runtimeFileName.ToLower()
        let filePath = dirPath + @"\" + fileName
        if (not <| Directory.Exists(dirPath)) then Directory.CreateDirectory(dirPath) |> ignore
        else ()
        let fileContent = fileContentStr times ""
        printfn $"\nSaving benchmark results to '%s{filePath}'"
        File.WriteAllText(filePath, headerStr + "\n" + fileContent)

    let private benchStr config =
        match config with
        | Pingpong config ->
            $"Pingpong (RoundCount: %i{config.RoundCount})"
        | ThreadRing config ->
            $"ThreadRing (ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount})"
        | Big config ->
            $"Big (ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount})"
        | Bang config ->
            $"Bang (ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount})"
        | ReverseBang config ->
            $"ReverseBang (ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount})"

    let private printResult (result: BenchmarkResult) =
        let rec runExecTimesStr runExecTimes acc =
            match runExecTimes with
            | [] -> (acc + "└───────────────────────────────────────────────────────────────────────────┘")
            | (run, time)::ts -> 
                let str = $"│  #%-10i{run}                       %-35i{time}    │\n"
                runExecTimesStr ts (acc + str)

        let _, config, _, runtimeName, times = result
        let benchName = benchStr config
        let runExecTimesStr = runExecTimesStr times ""
        let headerStr = $"
┌───────────────────────────────────────────────────────────────────────────┐
│  Benchmark:  %-50s{benchName}           │
│  Runtime:    %-50s{runtimeName}           │
├───────────────────────────────────────────────────────────────────────────┤
│  Run                               Execution time (ms)                    │
│  ────────────────────────────────  ─────────────────────────────────────  │\n"
        let toPrint = headerStr + runExecTimesStr
        printfn $"%s{toPrint}"

    let private runBenchmark config runs (runtime: Runtime) : BenchmarkResult =
        let getRuntimeName (runtime: Runtime) =
            match runtime with
            | :? Naive -> ("naive", "Naive")
            | :? Advanced as a -> 
                let ewc, bwc, esc = a.GetConfiguration()
                ($"advanced-ewc%i{ewc}-bwc%i{bwc}-esc%i{esc}", $"Advanced (EWC: %i{ewc} BWC: %i{bwc} ESC: %i{esc})")
            | _ -> failwith "runBenchmark: Invalid runtime!"

        let createBenchmark config =
            match config with
            | Pingpong config ->
                ("Pingpong", Pingpong.Create config.RoundCount)
            | ThreadRing config ->
                ("ThreadRing", ThreadRing.Create config.ProcessCount config.RoundCount)
            | Big config ->
                ("Big", Big.Create config.ProcessCount config.RoundCount)
            | Bang config ->
                ("Bang", Bang.Create config.ProcessCount config.RoundCount)
            | ReverseBang config ->
                ("ReverseBang", ReverseBang.Create config.ProcessCount config.RoundCount)

        let rec executeBenchmark config curRun acc =
            let (bench, eff) = createBenchmark config
            match curRun with
            | curRun' when curRun' = runs -> (bench, acc)
            | curRun' ->
                let res: Result<int64, obj> = runtime.Eval(eff).Await()
                let time =
                    match res with
                    | Ok time -> time
                    | Error _ -> -1
                let runNum = curRun' + 1
                let result = (runNum, time)
                printfn $"Completed run #%-5i{runNum} ── Time %-10i{time} (ms) ── %s{benchStr config}"
                executeBenchmark config runNum (acc @ [result])

        let runtimeFileName, runtimeName = getRuntimeName runtime
        let bench, runExecTimes = executeBenchmark config 0 []
        (bench, config, runtimeFileName, runtimeName, runExecTimes)

    let Run configs runtime runs (processCountInc, incTimes) =
        let newConfig config incTime =
            match config with
            | Pingpong config ->
                Pingpong config
            | ThreadRing config ->
                ThreadRing { RoundCount = config.RoundCount; 
                             ProcessCount = config.ProcessCount + (processCountInc * incTime) }
            | Big config ->
                Big { RoundCount = config.RoundCount; 
                      ProcessCount = config.ProcessCount + (processCountInc * incTime) }
            | Bang config ->
                Bang { RoundCount = config.RoundCount; 
                       ProcessCount = config.ProcessCount + (processCountInc * incTime) }
            | ReverseBang config ->
                ReverseBang { RoundCount = config.RoundCount;
                              ProcessCount = config.ProcessCount + (processCountInc * incTime) }

        let configs = configs @ (List.concat <| List.map (fun incTime ->
            List.map (fun config -> newConfig config incTime) configs) [1..incTimes])

        let results = List.map (fun config -> runBenchmark config runs runtime) configs

        for result in results do
            printResult result
            writeResultsToCsv result
