// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Benchmarks

open FSharp.FIO.FIO
open System.Threading

// Simple ping-pong messaging.
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

    let pingpong chan =
        Parallel(ping chan, pong chan) >>= fun _ -> End()

    let pingpongInf chan =
        Parallel(pingInf chan, pongInf chan) >>= fun _ -> End()

// ThreadRing benchmark
// Savina paper [19]: http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf
module FiberRing = 

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

    let benchmarkFIO processCount roundCount =
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

    let benchmarkFS processCount roundCount =
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
// https://dl.acm.org/doi/10.1145/2364489.2364495I (from Savina paper)
module Big =
    
    type private Process =
        { Name: string
          Chans: Channel<int> list
        }

    let private createProcess proc =
        let rec createRecvResp proc chans =
            match chans with
            | []           -> createRecvResp proc proc.Chans
            | chan::chans' -> Receive(chan) >>= fun x ->
                              printfn $"%s{proc.Name} received: %A{x}"
                              let y = x + 10
                              Send(y, chan) >>= fun _ ->
                              printfn $"%s{proc.Name} sent: %A{y}"
                              createRecvResp proc chans'
        and createSend proc chans (msg : int) =
            match chans with
            | []           -> failwith "createSend: Empty channel list not supported!"
            | chan::[]     -> Send(msg, chan) >>= fun _ ->
                              printfn $"%s{proc.Name} sent: %A{msg}"
                              createRecvResp proc proc.Chans
            | chan::chans' -> Send(msg, chan) >>= fun _ ->
                              printfn $"%s{proc.Name} sent: %A{msg}"
                              createSend proc chans' msg
        createSend proc proc.Chans 0

    let benchmark processCount =
        let rec createProcessNames processCount acc =
            match processCount with
            | 0     -> acc
            | count -> createProcessNames (count - 1) ($"p{count - 1}" :: acc)

        let rec createChanMap processNames (chanMap : Map<string, Channel<int>>) =
            match processNames with
            | []    -> chanMap
            | p::ps -> let rec populateMap ps (chanMap : Map<string, Channel<int>>) =
                           match ps with
                           | []      -> chanMap
                           | p'::ps' -> let chanName = p + p'
                                        let newMap = chanMap.Add(chanName, Channel<int>())
                                        populateMap ps' newMap
                       let updatedChanMap = populateMap ps chanMap
                       createChanMap ps updatedChanMap

        let getProcessChans (name : string) (chanMap : Map<string, Channel<int>>) =
            let rec loop (keys : string list) (acc : Channel<int> list) = 
                match keys with
                | []    -> acc
                | k::ks -> if k.Contains(name) then
                               match chanMap.TryFind k with
                               | Some chan -> loop ks (chan :: acc) 
                               | _         -> loop ks acc
                           else 
                               loop ks acc
            loop (chanMap.Keys |> Seq.cast |> List.ofSeq) []

        let rec createProcesses processCount chanMap acc =
            match processCount with
            | 0     -> acc
            | count -> let name = $"p{count - 1}"
                       let processChans = getProcessChans name chanMap
                       let proc = {Name = name; Chans = processChans;}
                       createProcesses (count - 1) chanMap (proc :: acc)

        let rec createBig procs =
            match procs with
            | pa::[pb] -> Parallel(createProcess pa, createProcess pb) >>= fun _ -> End()
            | p::ps    -> Parallel(createProcess p, createBig ps) >>= fun _ -> End()
            | _        -> failwith $"createBig failed! (at least 2 processes should exist) processCount = %A{processCount}"
  
        let processNames = createProcessNames processCount []
        let chanMap = createChanMap processNames Map.empty
        let processes = createProcesses processCount chanMap []
        createBig processes