// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Examples

open FSharp.FIO.FIO
open System.Threading

module Utils =
    
    let createChannels<'Msg> n =
        [for _ in 1..n -> Channel<'Msg>()]

module Pingpong =

    let intPing chan =
        let x = 0
        Send(x, chan) >>= fun _ -> 
        printfn $"intPing sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"intPing received: %A{y}"
        End()

    let intPong chan =
        Receive(chan) >>= fun x ->
        printfn $"intPong received: %A{x}"
        let y = x + 10
        Send(y, chan) >>= fun _ ->
        printfn $"intPong sent: %A{y}"
        End()

    let rec intPingInf chan =
        let x = 0
        Send(x, chan) >>= fun _ ->
        printfn $"intPing sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"intPing received: %A{y}"
        intPingInf chan

    let rec intPingInfInc chan x =
        Send(x, chan) >>= fun _ ->
        printfn $"intPing sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"intPing received: %A{y}"
        intPingInfInc chan y
    
    let rec intPongInf chan =
        Receive(chan) >>= fun x ->
        printfn $"intPong received: %A{x}"
        let y = x + 10
        Send(y, chan) >>= fun _ ->
        printfn $"intPong sent: %A{y}"
        intPongInf chan

    let strPing chan =
        let x = ""
        Send(x, chan) >>= fun _ ->
        printfn $"strPing sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"strPing received: %A{y}"
        End()

    let strPong chan =
        Receive(chan) >>= fun x ->
        printfn $"strPong received: %A{x}"
        let y = x + "a"
        Send(y, chan) >>= fun _ ->
        printfn $"strPong sent: %A{y}"
        End()

    let rec strPingInf chan =
        let x = ""
        Send(x, chan) >>= fun _ ->
        printfn $"strPing sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"strPing received: %A{y}"
        strPingInf chan

    let rec strPingInfInc chan x =
        Send(x, chan) >>= fun _ ->
        printfn $"strPing sent: %A{x}"
        Receive(chan) >>= fun y ->
        printfn $"strPing received: %A{y}"
        strPingInfInc chan y

    let rec strPongInf chan =
        Receive(chan) >>= fun x ->
        printfn $"strPong received: %A{x}"
        let y = x + "a"
        Send(x, chan) >>= fun _ ->
        printfn $"strPong sent: %A{y}"
        Receive(chan) >>= fun z ->
        printfn $"strPong received: %A{z}"
        let v = z + "b"
        Send(v, chan) >>= fun _ ->
        printfn $"strPong sent: %A{v}"
        strPongInf chan

    let intPingpong chan =
        Parallel(intPing chan, intPong chan) >>= fun _ -> End()

    let intPingpongInf chan =
        Parallel(intPingInf chan, intPongInf chan) >>= fun _ -> End()
    
    let intPingpongInfInc chan =
        Parallel(intPingInfInc chan 0, intPongInf chan) >>= fun _ -> End()
    
    let strPingpong chan =
        Parallel(strPing chan, strPong chan) >>= fun _ -> End()

    let strPingpongInf chan =
        Parallel(strPingInf chan, strPongInf chan) >>= fun _ -> End()
    
    let strPingpongInfInc chan =
        Parallel(strPingInfInc chan "", strPongInf chan) >>= fun _ -> End()
    
    let intStrPingpong chanInt chanStr =
        Parallel(intPingpong chanInt, strPingpong chanStr) >>= fun _ -> End()

    let intStrPingpongInf chanInt chanStr =
        Parallel(intPingpongInf chanInt, strPingpongInf chanStr) >>= fun _ -> End()
    
    let intStrPingpongInfInc chanInt chanStr =
        Parallel(intPingpongInfInc chanInt, strPingpongInfInc chanStr) >>= fun _ -> End()

module Ring = 

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }

    let private createFIOProcess p first m =
        let rec create n =
            match n with
            | 1 when first -> Receive(p.ChanRecv) >>= fun x ->
                              printfn $"%s{p.Name} received: %A{x}"
                              let y = x + 10
                              Send(y, p.ChanSend) >>= fun _ ->
                              printfn $"%s{p.Name} sent: %A{y}"
                              Receive(p.ChanRecv) >>= fun z -> 
                              printfn $"%s{p.Name} received: %A{z}"
                              End()
            | 1            -> Receive(p.ChanRecv) >>= fun x ->
                              printfn $"%s{p.Name} received: %A{x}"
                              let y = x + 10
                              Send(y, p.ChanSend) >>= fun _ ->
                              printfn $"%s{p.Name} sent: %A{y}"
                              End()
            | _            -> Receive(p.ChanRecv) >>= fun x ->
                              printfn $"%s{p.Name} received: %A{x}"
                              let y = x + 10
                              Send(y, p.ChanSend) >>= fun _ ->
                              printfn $"%s{p.Name} sent: %A{y}"
                              create (n - 1)
        create m

    let private createFSProcess p first m =
        let rec create n =
            match n with
            | 1 when first -> let x = p.ChanRecv.Receive()
                              printfn $"%s{p.Name} received: %A{x}"
                              let y = x + 10
                              p.ChanSend.Send y
                              printfn $"%s{p.Name} sent: %A{y}"
                              let z = p.ChanRecv.Receive()
                              printfn $"%s{p.Name} received: %A{z}"
            | 1            -> let x = p.ChanRecv.Receive()
                              printfn $"%s{p.Name} received: %A{x}"
                              let y = x + 10
                              p.ChanSend.Send y
                              printfn $"%s{p.Name} sent: %A{y}"
            | _            -> let recv = p.ChanRecv.Receive()
                              printfn $"%s{p.Name} received: %A{recv}"
                              let value = recv + 10
                              p.ChanSend.Send value
                              printfn $"%s{p.Name} sent: %A{value}"
                              create (n - 1)
        create m

    let fioBenchmark processCount roundCount =
        let getRecvChan index (chans : Channel<int> list) =
            match index with
            | i when i - 1 < 0 -> chans.Item (List.length chans - 1)
            | i                -> chans.Item (i - 1)

        let rec createProcesses chans allChans index acc =
            match chans with
            | []    -> acc
            | c::cs -> let proc = {Name = $"p{index}"; ChanSend = c; ChanRecv = getRecvChan index allChans}
                       createProcesses cs allChans (index + 1) (acc @ [proc])

        let rec createProcessRing procs roundCount first =
            match procs with
            | pa::[pb] -> Parallel(createFIOProcess pa first roundCount, createFIOProcess pb false roundCount)
                          >>= fun _ -> End()
            | p::ps    -> Parallel(createFIOProcess p first roundCount, createProcessRing ps roundCount false)
                          >>= fun _ -> End()
            | _        -> failwith $"createProcessRing failed! (at least 2 processes should exist) m = %A{roundCount}"

        let injectMessage p startMsg =
            p.ChanRecv.Send startMsg

        let chans = Utils.createChannels<int> processCount

        let procs = createProcesses chans chans 0 []

        injectMessage (List.item 0 procs) 0

        createProcessRing procs roundCount true

    let fsBenchmark processCount roundCount =
        let getRecvChan index (chans : Channel<int> list) =
            match index with
            | i when i - 1 < 0 -> chans.Item (List.length chans - 1)
            | i                -> chans.Item (i - 1)

        let rec createProcesses chans allChans index acc =
            match chans with
            | []    -> acc
            | c::cs -> let proc = {Name = $"p{index}"; ChanSend = c; ChanRecv = getRecvChan index allChans}
                       createProcesses cs allChans (index + 1) (acc @ [proc])

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

        let injectMessage p startMsg =
            p.ChanRecv.Send startMsg

        let chans = Utils.createChannels<int> processCount

        let processes = createProcesses chans chans 0 []

        injectMessage (List.item 0 processes) 0

        createProcessRing processes roundCount true

// https://dl.acm.org/doi/10.1145/2364489.2364495
// Big benchmark
module Big =
    
    type private Process =
        { Name: string
          Chans: Channel<int> list
        }

    let private createProcess proc =
        let rec createRecvResp proc chans =
            match chans with
            | []    -> createRecvResp proc proc.Chans
            | c::cs -> Receive(c) >>= fun x ->
                       printfn $"%s{proc.Name} received: %A{x}"
                       let y = x + 10
                       Send(y, c) >>= fun _ ->
                       printfn $"%s{proc.Name} sent: %A{y}"
                       createRecvResp proc cs
        and createSend proc chans (x : int) =
            match chans with
            | []    -> failwith "createSend: Empty list not supported!"
            | c::[] -> Send(x, c) >>= fun _ ->
                       printfn $"%s{proc.Name} sent: %A{x}"
                       createRecvResp proc proc.Chans
            | c::cs -> Send(x, c) >>= fun _ ->
                       printfn $"%s{proc.Name} sent: %A{x}"
                       createSend proc cs x
        createSend proc proc.Chans 0

    let benchmark processCount =
        let rec createProcessNames processCount acc =
            match processCount with
            | 0 -> acc
            | n -> createProcessNames (n - 1) ($"p{n - 1}" :: acc)

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
            | index -> let name = $"p{index - 1}"
                       let processChans = getProcessChans name chanMap
                       let proc = {Name = name; Chans = processChans;}
                       createProcesses (index - 1) chanMap (proc :: acc)

        let rec createBig procs =
            match procs with
            | pa::[pb] -> Parallel(createProcess pa, createProcess pb) >>= fun _ -> End()
            | p::ps    -> Parallel(createProcess p, createBig ps) >>= fun _ -> End()
            | _        -> failwith $"createBig failed! (at least 2 processes should exist) processCount = %A{processCount}"
  
        let processNames = createProcessNames processCount []

        let chanMap = createChanMap processNames Map.empty
        
        let processes = createProcesses processCount chanMap []
        
        createBig processes