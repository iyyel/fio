// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Examples

open FSharp.FIO
open System.Threading

module Pingpong =

    let intPing chan =
        let x = 0
        Send(x, chan, fun () ->
            printfn $"intPing sent: %A{x}"
            Receive(chan, fun y ->
                printfn $"intPing received: %A{y}"
                End()))

    let intPong chan =
        Receive(chan, fun x ->
            printfn $"intPong received: %A{x}"
            let y = x + 10
            Send(y, chan, fun () ->
                printfn $"intPong sent: %A{y}"
                End()))

    let rec intPingInf chan =
        let x = 0
        Send(x, chan, fun () ->
            printfn $"intPing sent: %A{x}"
            Receive(chan, fun y ->
                printfn $"intPing received: %A{y}"
                intPingInf chan))

    let rec intPingInfInc chan x =
        Send(x, chan, fun () ->
            printfn $"intPing sent: %A{x}"
            Receive(chan, fun y ->
                printfn $"intPing received: %A{y}"
                intPingInfInc chan y))
    
    let rec intPongInf chan =
        Receive(chan, fun x ->
            printfn $"intPong received: %A{x}"
            let y = x + 10
            Send(y, chan, fun () ->
                printfn $"intPong sent: %A{y}"
                intPongInf chan))

    let strPing chan =
        let x = ""
        Send(x, chan, fun () ->
            printfn $"strPing sent: %A{x}"
            Receive(chan, fun y ->
                printfn $"strPing received: %A{y}"
                End()))
    
    let strPong chan =
        Receive(chan, fun x ->
            printfn $"strPong received: %A{x}"
            let y = x + "a"
            Send(y, chan, fun () ->
                printfn $"strPong sent: %A{y}"
                End()))

    let rec strPingInf chan =
        let x = ""
        Send(x, chan, fun () ->
            printfn $"strPing sent: %A{x}"
            Receive(chan, fun y ->
                printfn $"strPing received: %A{y}"
                strPingInf chan))
    
    let rec strPingInfInc chan x =
        Send(x, chan, fun () ->
            printfn $"strPing sent: %A{x}"
            Receive(chan, fun y ->
                printfn $"strPing received: %A{y}"
                strPingInfInc chan y))
    
    let rec strPongInf chan =
        Receive(chan, fun x ->
            printfn $"strPong received: %A{x}"
            let y = x + "a"
            Send(y, chan, fun () ->
                printfn $"strPong sent: %A{y}"
                Receive(chan, fun z ->
                    printfn $"strPong received: %A{z}"
                    let v = z + "b"
                    Send(v, chan, fun () ->
                        printfn $"strPong sent: %A{v}"
                        strPongInf chan))))

    let intPingpong chan =
        Parallel(intPing chan, intPong chan, fun _ -> End())

    let intPingpongInf chan =
        Parallel(intPingInf chan, intPongInf chan, fun _ -> End())
    
    let intPingpongInfInc chan =
        Parallel(intPingInfInc chan 0, intPongInf chan, fun _ -> End())
    
    let strPingpong chan =
        Parallel(strPing chan, strPong chan, fun _ -> End())

    let strPingpongInf chan =
        Parallel(strPingInf chan, strPongInf chan, fun _ -> End())
    
    let strPingpongInfInc chan =
        Parallel(strPingInfInc chan "", strPongInf chan, fun _ -> End())
    
    let intStrPingpong chanInt chanStr =
        Parallel(intPingpong chanInt, strPingpong chanStr, fun _ -> End())

    let intStrPingpongInf chanInt chanStr =
        Parallel(intPingpongInf chanInt, strPingpongInf chanStr, fun _ -> End())
    
    let intStrPingpongInfInc chanInt chanStr =
        Parallel(intPingpongInfInc chanInt, strPingpongInfInc chanStr, fun _ -> End())

module Ring = 

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }

    let private createProcess chanRecv chanSend name first m =
        let rec create n =
            match n with
            | 1 when first -> Receive(chanRecv, fun x ->
                                  printfn $"%s{name} received: %A{x}"
                                  let y = x + 10
                                  Send(y, chanSend, fun () ->
                                      printfn $"%s{name} sent: %A{y}"
                                      Receive(chanRecv, fun z -> 
                                          printfn $"%s{name} received: %A{z}"
                                          End())))
            | 1            -> Receive(chanRecv, fun v ->
                                  printfn $"%s{name} received: %A{v}"
                                  let value = v + 10
                                  Send(value, chanSend, fun () ->
                                      printfn $"%s{name} sent: %A{value}"
                                      End()))
            | _            -> Receive(chanRecv, fun v ->
                                  printfn $"%s{name} received: %A{v}"
                                  let value = v + 10
                                  Send(value, chanSend, fun () ->
                                      printfn $"%s{name} sent: %A{value}"
                                      create (n - 1)))
        create m

    let processRing processCount roundCount =
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
            | pa::pb::[] -> Parallel(createProcess pa.ChanRecv pa.ChanSend pa.Name first roundCount, createProcess pb.ChanRecv pb.ChanSend pb.Name false roundCount, fun _ -> End())
            | p::ps      -> Parallel(createProcess p.ChanRecv p.ChanSend p.Name first roundCount, createProcessRing ps roundCount false, fun _ -> End())
            | _          -> failwith $"createProcessRing failed! (at least 2 processes should exist) m = %A{roundCount}"

        let injectMessage p startMsg =
            p.ChanRecv.Send startMsg

        let chans = [for _ in 1..processCount -> Channel<int>()]

        let procs = createProcesses chans chans 0 []

        injectMessage (List.item 0 procs) 0

        createProcessRing procs roundCount true

module FSharpRing = 

    type private Process =
        { Name: string
          ChanSend: Channel<int>
          ChanRecv: Channel<int>
        }

    let private createProcess (chanRecv : Channel<int>) (chanSend : Channel<int>) name first m =
        let rec create n =
            match n with
            | 1 when first -> let x = chanRecv.Receive()
                              printfn $"%s{name} received: %A{x}"
                              let y = x + 10
                              chanSend.Send y
                              printfn $"%s{name} sent: %A{y}"
                              let z = chanRecv.Receive()
                              printfn $"%s{name} received: %A{z}"
            | 1            -> let x = chanRecv.Receive()
                              printfn $"%s{name} received: %A{x}"
                              let y = x + 10
                              chanSend.Send y
                              printfn $"%s{name} sent: %A{y}"
            | _            -> let recv = chanRecv.Receive()
                              printfn $"%s{name} received: %A{recv}"
                              let value = recv + 10
                              chanSend.Send value
                              printfn $"%s{name} sent: %A{value}"
                              create (n - 1)
        create m

    let processRing processCount roundCount =
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
            | pa::pb::[] -> let task1 = Tasks.Task.Factory.StartNew(fun () -> createProcess pa.ChanRecv pa.ChanSend pa.Name first m)
                            let task2 = Tasks.Task.Factory.StartNew(fun () -> createProcess pb.ChanRecv pb.ChanSend pb.Name false m)
                            task1.Wait()
                            task2.Wait()
            | p::ps      -> let task = Tasks.Task.Factory.StartNew(fun () -> createProcess p.ChanRecv p.ChanSend p.Name first m)
                            createProcessRing ps m false
                            task.Wait()
            | _          -> failwith $"createProcessRing failed! (at least 2 processes should exist) m = %A{m}"

        let injectMessage p startMsg =
            p.ChanRecv.Send startMsg

        let chans = [for _ in 1..processCount -> Channel<int>()]

        let processes = createProcesses chans chans 0 []

        injectMessage (List.item 0 processes) 0

        createProcessRing processes roundCount true