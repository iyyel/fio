(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace Benchmarks

open FIO.Core
open FIO.Runtime
open FIO.Runtime.Naive
open FIO.Runtime.Intermediate
open FIO.Runtime.Advanced
open FIO.Runtime.Deadlocking

open System
open System.IO
open System.Diagnostics

module internal Timer =

    type internal TimerMessage =
        | Start
        | Stop

    type internal StopwatchTimerMessage =
        | Start of Stopwatch
        | Stop

    type internal ChannelTimerMessage<'msg> =
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
                fioZ <| fun _ -> stopwatch.Start()
            | count ->
                !*>chan
                >> fun res ->
                    match res with
                    | TimerMessage.Start -> loopStart (count - 1)
                    | _ -> loopStart count

        let rec loopStop count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer stopped!"
#endif
                fioZ <| fun _ -> stopwatch.Stop()
            | count ->
                !*>chan
                >> fun res ->
                    match res with
                    | TimerMessage.Stop -> loopStop (count - 1)
                    | _ -> loopStop count

        loopStart startCount
        >> fun _ -> loopStop stopCount >> fun _ -> succeed stopwatch.ElapsedMilliseconds

    let StopwatchEffect stopCount (chan: Channel<StopwatchTimerMessage>) =
        let mutable stopwatch = Stopwatch()

        let rec loopStop count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer stopped!"
#endif
                fioZ <| fun _ -> stopwatch.Stop()
            | count ->
                !*>chan
                >> fun res ->
                    match res with
                    | StopwatchTimerMessage.Stop -> loopStop (count - 1)
                    | _ -> loopStop count

        !*>chan
        >> fun msg ->
            match msg with
            | StopwatchTimerMessage.Start sw -> stopwatch <- sw
            | StopwatchTimerMessage.Stop -> failwith "TimerFiber: Received stop as first message!"

            loopStop stopCount >> fun _ -> succeed stopwatch.ElapsedMilliseconds

    let ChannelEffect readyCount goCount stopCount (chan: Channel<ChannelTimerMessage<int>>) =
        let stopwatch = Stopwatch()
        let mutable processChan = Channel<int>()

        let rec loopReady count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer started!"
#endif
                fioZ <| fun _ -> stopwatch.Start()
            | count ->
                !*>chan
                >> fun res ->
                    match res with
                    | ChannelTimerMessage.Ready -> loopReady (count - 1)
                    | _ -> loopReady count

        let rec loopGo count msg =
            match count with
            | 0 -> ! ()
            | count -> msg *> processChan >> fun _ -> loopGo (count - 1) 0

        let rec loopStop count =
            match count with
            | 0 ->
#if DEBUG
                printfn "DEBUG: TimerEffect: Timer stopped!"
#endif
                fioZ <| fun _ -> stopwatch.Stop()
            | count ->
                !*>chan
                >> fun res ->

                    match res with
                    | ChannelTimerMessage.Stop -> loopStop (count - 1)
                    | _ -> loopStop count

        !*>chan
        >> fun msg ->
            match msg with
            | Chan pChan ->
                processChan <- pChan
                ! ()
            | _ -> failwith "ChannelEffect: Did not receive channel as first message!"
            >> fun _ ->
                loopReady readyCount
                >> fun _ ->
                    loopGo goCount 0
                    >> fun _ -> loopStop stopCount >> fun _ -> succeed stopwatch.ElapsedMilliseconds

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

    let private createPingProcess proc roundCount readyChan =
        let stopwatch = Stopwatch()

        let rec create msg roundCount =
            if roundCount = 0 then
                fioZ (fun _ -> stopwatch.Stop())
                >> fun _ -> succeed stopwatch.ElapsedMilliseconds
            else
                msg *> proc.ChanSend
                >> fun _ ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} sent ping: %i{msg}"
#endif
                    !*>proc.ChanRecv
                    >> fun x ->
#if DEBUG
                        printfn $"DEBUG: %s{proc.Name} received pong: %i{x}"
#endif
                        create x (roundCount - 1)

        !*>readyChan
        >> fun _ -> fioZ (fun _ -> stopwatch.Start()) >> fun _ -> create 0 roundCount

    let private createPongProcess proc roundCount readyChan =
        let rec create roundCount =
            if roundCount = 0 then
                ! ()
            else
                !*>proc.ChanRecv
                >> fun x ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} received ping: %i{x}"
#endif
                    let y = x + 10

                    y *> proc.ChanSend
                    >> fun _ ->
#if DEBUG
                        printfn $"DEBUG: %s{proc.Name} sent pong: %i{y}"
#endif
                        create (roundCount - 1)

        0 *> readyChan >> fun _ -> create roundCount

    let Create roundCount : FIO<int64, obj> =
        let pingSendChan = Channel<int>()
        let pongSendChan = Channel<int>()

        let pingProc =
            { Name = "p0"
              ChanSend = pingSendChan
              ChanRecv = pongSendChan }

        let pongProc =
            { Name = "p1"
              ChanSend = pongSendChan
              ChanRecv = pingSendChan }

        let readyChan = Channel<int>()

        createPingProcess pingProc roundCount readyChan
        <*> createPongProcess pongProc roundCount readyChan
        >> fun (res, _) -> succeed res

(**********************************************************************************)
(* Threadring benchmark                                                           *)
(* Measures: Message sending; Context switching between actors                    *)
(* Savina benchmark #5                                                            *)
(* (http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf)       *)
(**********************************************************************************)
module Threadring =

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int> }

    let private createProcess proc roundCount timerChan =
        let rec create roundCount =
            if roundCount = 0 then
                Timer.ChannelTimerMessage.Stop *> timerChan >> fun _ -> ! ()
            else
                !*>proc.ChanRecv
                >> fun x ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} received: %i{x}"
#endif
                    let y = x + 10

                    y *> proc.ChanSend
                    >> fun _ ->
#if DEBUG
                        printfn $"DEBUG: %s{proc.Name} sent: %i{y}"
#endif
                        create (roundCount - 1)

        Timer.ChannelTimerMessage.Ready *> timerChan >> fun _ -> create roundCount

    let Create processCount roundCount : FIO<int64, obj> =
        let getRecvChan index (chans: Channel<int> list) =
            match index with
            | index when index - 1 < 0 -> chans.Item(List.length chans - 1)
            | index -> chans.Item(index - 1)

        let rec createProcesses chans allChans index acc =
            match chans with
            | [] -> acc
            | chan :: chans ->
                let proc =
                    { Name = $"p{index}"
                      ChanSend = chan
                      ChanRecv = getRecvChan index allChans }

                createProcesses chans allChans (index + 1) (acc @ [ proc ])

        let rec createThreadring procs acc timerChan =
            match procs with
            | [] -> acc
            | p :: ps ->
                let eff = createProcess p roundCount timerChan <!> acc
                createThreadring ps eff timerChan

        let chans = [ for _ in 1..processCount -> Channel<int>() ]
        let procs = createProcesses chans chans 0 []

        let pa, pb, ps =
            match procs with
            | pa :: pb :: ps -> (pa, pb, ps)
            | _ -> failwith $"createThreadring failed! (at least 2 processes should exist) processCount = %i{processCount}"

        let timerChan = Channel<Timer.ChannelTimerMessage<int>>()

        let effEnd =
            createProcess pb roundCount timerChan <!> createProcess pa roundCount timerChan

        !>(Timer.ChannelEffect processCount 1 processCount timerChan)
        >> fun fiber ->
            (Timer.ChannelTimerMessage.Chan pa.ChanRecv) *> timerChan
            >> fun _ ->
                createThreadring ps effEnd timerChan
                >> fun _ -> !?>fiber >> fun res -> succeed res

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

    let private createProcess proc msg roundCount timerChan goChan =
        let rec createSendPings chans roundCount =
            if List.length chans = 0 then
                createRecvPings proc.ChansSend.Length roundCount
            else
                let x = msg
                let msg = Ping(x, proc.ChanRecvPong)
                let chan, chans = (List.head chans, List.tail chans)

                msg *> chan
                >> fun _ ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} sent ping: %i{x}"
#endif
                    createSendPings chans roundCount

        and createRecvPings recvCount roundCount =
            if recvCount = 0 then
                createRecvPongs proc.ChansSend.Length roundCount
            else
                !*>proc.ChanRecvPing
                >> fun msg ->
                    match msg with
                    | Ping(x, replyChan) ->
#if DEBUG
                        printfn $"DEBUG: %s{proc.Name} received ping: %i{x}"
#endif
                        let y = x + 1
                        let msgReply = Pong y

                        msgReply *> replyChan
                        >> fun _ ->
#if DEBUG
                            printfn $"DEBUG: %s{proc.Name} sent pong: %i{y}"
#endif
                            createRecvPings (recvCount - 1) roundCount
                    | _ -> failwith "createRecvPings: Received pong when ping should be received!"

        and createRecvPongs recvCount roundCount =
            if recvCount = 0 then
                if roundCount = 0 then
                    Timer.ChannelTimerMessage.Stop *> timerChan
                else
                    createSendPings proc.ChansSend (roundCount - 1)
            else
                !*>proc.ChanRecvPong
                >> fun msg ->
                    match msg with
                    | Pong x ->
#if DEBUG
                        printfn $"DEBUG: %s{proc.Name} received pong: %i{x}"
#endif
                        createRecvPongs (recvCount - 1) roundCount
                    | _ -> failwith "createRecvPongs: Received ping when pong should be received!"

        Timer.ChannelTimerMessage.Ready *> timerChan
        >> fun _ -> !*>goChan >> fun _ -> createSendPings proc.ChansSend (roundCount - 1)

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

        let timerChan = Channel<Timer.ChannelTimerMessage<int>>()
        let goChan = Channel<int>()

        let effEnd =
            createProcess pa (10 * (processCount - 2)) roundCount timerChan goChan
            <!> createProcess pb (10 * (processCount - 1)) roundCount timerChan goChan

        !>(Timer.ChannelEffect processCount processCount processCount timerChan)
        >> fun fiber ->
            (Timer.ChannelTimerMessage.Chan goChan) *> timerChan
            >> fun _ ->
                createBig ps 0 timerChan goChan effEnd
                >> fun _ -> !?>fiber >> fun res -> succeed res

(**********************************************************************************)
(* Bang benchmark                                                                 *)
(* Measures: Many-to-One message passing                                          *)
(* A Scalability Benchmark Suite for Erlang/OTP                                   *)
(* (https://dl.acm.org/doi/10.1145/2364489.2364495I)                              *)
(**********************************************************************************)
module Bang =

    type private Process = { Name: string; Chan: Channel<int> }

    let rec private createSendProcess proc msg roundCount timerChan goChan =
        let rec create proc msg roundCount =
            if roundCount = 0 then
                ! ()
            else
                msg *> proc.Chan
                >> fun _ ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} sent: %i{msg}"
#endif
                    create proc (msg + 10) (roundCount - 1)

        Timer.ChannelTimerMessage.Ready *> timerChan
        >> fun _ -> !*>goChan >> fun _ -> create proc msg roundCount

    let rec private createRecvProcess proc roundCount timerChan goChan =
        let rec create proc roundCount =
            if roundCount = 0 then
                Timer.ChannelTimerMessage.Stop *> timerChan >> fun _ -> ! ()
            else
                !*>proc.Chan
                >> fun x ->
#if DEBUG
                    printfn $"DEBUG: %s{proc.Name} received: %i{x}"
#endif
                    create proc (roundCount - 1)

        Timer.ChannelTimerMessage.Ready *> timerChan
        >> fun _ -> !*>goChan >> fun _ -> create proc roundCount

    let Create processCount roundCount : FIO<int64, obj> =
        let rec createSendProcesses recvProcChan processCount =
            List.map
                (fun count ->
                    { Name = $"p{count}"
                      Chan = recvProcChan })
                [ 1..processCount ]

        let rec createBang recvProc sendProcs msg acc timerChan goChan =
            match sendProcs with
            | [] -> acc
            | p :: ps ->
                let eff = createSendProcess p msg roundCount timerChan goChan <!> acc
                createBang recvProc ps (msg + 10) eff timerChan goChan

        let recvProc = { Name = "p0"; Chan = Channel<int>() }
        let sendProcs = createSendProcesses recvProc.Chan processCount

        let p, ps =
            match List.rev sendProcs with
            | p :: ps -> (p, ps)
            | _ -> failwith $"createBang failed! (at least 1 sending process should exist) processCount = %i{processCount}"

        let timerChan = Channel<Timer.ChannelTimerMessage<int>>()
        let goChan = Channel<int>()

        let effEnd =
            createSendProcess p 0 roundCount timerChan goChan
            <!> createRecvProcess recvProc (processCount * roundCount) timerChan goChan

        !>(Timer.ChannelEffect (processCount + 1) (processCount + 1) 1 timerChan)
        >> fun fiber ->
            (Timer.ChannelTimerMessage.Chan goChan) *> timerChan
            >> fun _ ->
                createBang recvProc ps 10 effEnd timerChan goChan
                >> fun _ -> !?>fiber >> fun res -> succeed res

(**********************************************************************************)
(*                                                                                *)
(* Spawn                                                                          *)
(*                                                                                *)
(**********************************************************************************)
module Spawn =

    let rec private createProcess timerChan =
        Timer.StopwatchTimerMessage.Stop *> timerChan >> fun _ -> ! ()

    let Create processCount : FIO<int64, obj> =

        let rec createSpawnTime processCount timerChan acc =
            match processCount with
            | 0 -> acc
            | count ->
                let eff = createProcess timerChan <!> acc
                createSpawnTime (count - 1) timerChan eff

        let timerChan = Channel<Timer.StopwatchTimerMessage>()
        let effEnd = createProcess timerChan <!> createProcess timerChan
        let stopwatch = Stopwatch()

        !>(Timer.StopwatchEffect processCount timerChan)
        >> fun fiber ->
            stopwatch.Start()

            (Timer.StopwatchTimerMessage.Start stopwatch) *> timerChan
            >> fun _ ->
                createSpawnTime (processCount - 2) timerChan effEnd
                >> fun _ -> !?>fiber >> fun res -> succeed res

(**********************************************************************************)
(*                                                                                *)
(* Benchmark infrastructure functionality                                         *)
(*                                                                                *)
(**********************************************************************************)
[<AutoOpen>]
module Benchmark =

    type PingpongConfig = { RoundCount: int }

    and ThreadringConfig = { ProcessCount: int; RoundCount: int }

    and BigConfig = { ProcessCount: int; RoundCount: int }

    and BangConfig = { ProcessCount: int; RoundCount: int }

    and SpawnConfig = { ProcessCount: int }

    and BenchmarkConfig =
        | Pingpong of PingpongConfig
        | Threadring of ThreadringConfig
        | Big of BigConfig
        | Bang of BangConfig
        | Spawn of SpawnConfig

    type EvalFunc = FIO<int64, obj> -> Fiber<int64, obj>

    type BenchmarkResult = string * BenchmarkConfig * string * string * (int * int64) list

    let private writeResultsToCsv (result: BenchmarkResult) =
        let configStr config =
            match config with
            | Pingpong config -> $"roundcount%i{config.RoundCount}"
            | Threadring config -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Big config -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Bang config -> $"processcount%i{config.ProcessCount}-roundcount%i{config.RoundCount}"
            | Spawn config -> $"processcount%i{config.ProcessCount}"

        let rec fileContentStr times acc =
            match times with
            | [] -> acc
            | (_, time) :: ts -> fileContentStr ts (acc + $"%i{time}\n")

        let headerStr = "Time"

        let homePath =
            if
                (Environment.OSVersion.Platform.Equals(PlatformID.Unix)
                 || Environment.OSVersion.Platform.Equals(PlatformID.MacOSX))
            then
                Environment.GetEnvironmentVariable("HOME")
            else
                Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%")

        let benchName, config, runtimeFileName, _, times = result

        let configStr = configStr config
        let runStr = times.Length.ToString() + "runs"
        let folderName = benchName.ToLower() + "-" + configStr + "-" + runStr

        let fileName =
            folderName
            + "-"
            + runtimeFileName.ToLower()
            + "-"
            + DateTime.Now.ToString("dd-MM-yyyy-HH-mm-ss")
            + ".csv"

        let dirPath =
            homePath + @"\fio\benchmarks\" + folderName + @"\" + runtimeFileName.ToLower()

        let filePath = dirPath + @"\" + fileName

        if (not <| Directory.Exists(dirPath)) then
            Directory.CreateDirectory(dirPath) |> ignore
        else
            ()

        let fileContent = fileContentStr times ""
        printfn $"\nSaving benchmark results to '%s{filePath}'"
        File.WriteAllText(filePath, headerStr + "\n" + fileContent)

    let private benchStr config =
        match config with
        | Pingpong config -> $"Pingpong (RoundCount: %i{config.RoundCount})"
        | Threadring config -> $"Threadring (ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount})"
        | Big config -> $"Big (ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount})"
        | Bang config -> $"Bang (ProcessCount: %i{config.ProcessCount} RoundCount: %i{config.RoundCount})"
        | Spawn config -> $"Spawn (ProcessCount: %i{config.ProcessCount})"

    let private printResult (result: BenchmarkResult) =
        let rec runExecTimesStr runExecTimes acc =
            match runExecTimes with
            | [] ->
                (acc
                 + "└───────────────────────────────────────────────────────────────────────────┘")
            | (run, time) :: ts ->
                let str = $"│  #%-10i{run}                       %-35i{time}    │\n"
                runExecTimesStr ts (acc + str)

        let _, config, _, runtimeName, times = result
        let benchName = benchStr config
        let runExecTimesStr = runExecTimesStr times ""

        let headerStr =
            $"
┌───────────────────────────────────────────────────────────────────────────┐
│  Benchmark:  %-50s{benchName}           │
│  Runtime:    %-50s{runtimeName}           │
├───────────────────────────────────────────────────────────────────────────┤
│  Run                               Time (ms)                              │
│  ────────────────────────────────  ─────────────────────────────────────  │\n"

        let toPrint = headerStr + runExecTimesStr
        printfn $"%s{toPrint}"

    let private runBenchmark config runs (runtime: Runtime) : BenchmarkResult =
        let getRuntimeName (runtime: Runtime) =
            match runtime with
            | :? NaiveRuntime -> ("naive", "Naive")
            | :? IntermediateRuntime as r ->
                let ewc, bwc, esc = r.GetConfiguration()
                ($"intermediate-ewc%i{ewc}-bwc%i{bwc}-esc%i{esc}", $"Intermediate (EWC: %i{ewc} BWC: %i{bwc} ESC: %i{esc})")
            | :? AdvancedRuntime as r ->
                let ewc, bwc, esc = r.GetConfiguration()
                ($"advanced-ewc%i{ewc}-bwc%i{bwc}-esc%i{esc}", $"Advanced (EWC: %i{ewc} BWC: %i{bwc} ESC: %i{esc})")
            | :? DeadlockingRuntime as r ->
                let ewc, bwc, esc = r.GetConfiguration()
                ($"deadlocking-ewc%i{ewc}-bwc%i{bwc}-esc%i{esc}", $"Deadlocking (EWC: %i{ewc} BWC: %i{bwc} ESC: %i{esc})")
            | _ -> failwith "runBenchmark: Invalid runtime!"

        let createBenchmark config =
            match config with
            | Pingpong config -> ("Pingpong", Pingpong.Create config.RoundCount)
            | Threadring config -> ("Threadring", Threadring.Create config.ProcessCount config.RoundCount)
            | Big config -> ("Big", Big.Create config.ProcessCount config.RoundCount)
            | Bang config -> ("Bang", Bang.Create config.ProcessCount config.RoundCount)
            | Spawn config -> ("Spawn", Spawn.Create config.ProcessCount)

        let rec executeBenchmark config curRun acc =
            let bench, eff = createBenchmark config

            match curRun with
            | curRun' when curRun' = runs -> (bench, acc)
            | curRun' ->
                let res: Result<int64, obj> = runtime.Run(eff).Await()

                let time =
                    match res with
                    | Ok time -> time
                    | Error _ -> -1

                let runNum = curRun' + 1
                let result = (runNum, time)
                executeBenchmark config runNum (acc @ [ result ])

        let runtimeFileName, runtimeName = getRuntimeName runtime
        let bench, runExecTimes = executeBenchmark config 0 []
        (bench, config, runtimeFileName, runtimeName, runExecTimes)

    let Run configs runtime runs (processCountInc, incTimes) =
        let newConfig config incTime =
            match config with
            | Pingpong config -> Pingpong config
            | Threadring config ->
                Threadring
                    { RoundCount = config.RoundCount
                      ProcessCount = config.ProcessCount + (processCountInc * incTime) }
            | Big config ->
                Big
                    { RoundCount = config.RoundCount
                      ProcessCount = config.ProcessCount + (processCountInc * incTime) }
            | Bang config ->
                Bang
                    { RoundCount = config.RoundCount
                      ProcessCount = config.ProcessCount + (processCountInc * incTime) }
            | Spawn config -> Spawn { ProcessCount = config.ProcessCount + (processCountInc * incTime) }

        let configs =
            configs
            @ (List.concat
               <| List.map (fun incTime -> List.map (fun config -> newConfig config incTime) configs) [ 1..incTimes ])

        let results = List.map (fun config -> runBenchmark config runs runtime) configs

        for result in results do
            printResult result
            writeResultsToCsv result
