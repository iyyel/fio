﻿// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace Examples

open FSharp.FIO

module Pingpong =

    let intPing chanInt =
        let x = 0
        Send(x, chanInt, fun () ->
            printfn $"intPing sent: %A{x}"
            Receive(chanInt, fun y ->
                printfn $"intPing received: %A{y}"
                Send(y, chanInt, fun () ->
                    printfn $"intPing sent: %A{y}"
                    Receive(chanInt, fun z ->
                        printfn $"intPing received: %A{z}"
                        End()))))

    let intPong chanInt =
        Receive(chanInt, fun x ->
            printfn $"intPong received: %A{x}"
            let y = x + 10
            Send(y, chanInt, fun () ->
                printfn $"intPong sent: %A{y}"
                Receive(chanInt, fun z ->
                    printfn $"intPong received: %A{z}"
                    let v = z + 10
                    Send(v, chanInt, fun () ->
                        printfn $"intPong sent: %A{v}"
                        End()))))

    let rec intPingInf chanInt =
        let x = 0
        Send(x, chanInt, fun () ->
            printfn $"intPing sent: %A{x}"
            Receive(chanInt, fun y ->
                printfn $"intPing received: %A{y}"
                intPingInf chanInt))

    let rec intPingInfInc chanInt value =
        Send(value, chanInt, fun () ->
            printfn $"intPing sent: %A{value}"
            Receive(chanInt, fun y ->
                printfn $"intPing received: %A{y}"
                intPingInfInc chanInt y))
    
    let rec intPongInf chanInt =
        Receive(chanInt, fun x ->
            printfn $"intPong received: %A{x}"
            let y = x + 10
            Send(y, chanInt, fun () ->
                printfn $"intPong sent: %A{y}"
                intPongInf chanInt))

    let strPing chanStr =
        let x = ""
        Send(x, chanStr, fun () ->
            printfn $"strPing sent: %A{x}"
            Receive(chanStr, fun y ->
                printfn $"strPing received: %A{y}"
                Send(y, chanStr, fun () ->
                    printfn $"strPing sent: %A{y}"
                    Receive(chanStr, fun z ->
                        printfn $"strPing received: %A{z}"
                        End()))))
    
    let strPong chanStr =
        Receive(chanStr, fun x ->
            printfn $"strPong received: %A{x}"
            let y = x + "a"
            Send(y, chanStr, fun () ->
                printfn $"strPong sent: %A{y}"
                Receive(chanStr, fun z ->
                    printfn $"strPong received: %A{z}"
                    let v = z + "b"
                    Send(v, chanStr, fun () ->
                        printfn $"strPong sent: %A{v}"
                        End()))))

    let rec strPingInf chanStr =
        let x = ""
        Send(x, chanStr, fun () ->
            printfn $"strPing sent: %A{x}"
            Receive(chanStr, fun y ->
                printfn $"strPing received: %A{y}"
                strPingInf chanStr))
    
    let rec strPingInfInc chanStr value =
        Send(value, chanStr, fun () ->
            printfn $"strPing sent: %A{value}"
            Receive(chanStr, fun y ->
                printfn $"strPing received: %A{y}"
                strPingInfInc chanStr y))
    
    let rec strPongInf chanStr =
        Receive(chanStr, fun x ->
            printfn $"strPong received: %A{x}"
            let y = x + "a"
            Send(y, chanStr, fun () ->
                printfn $"strPong sent: %A{y}"
                Receive(chanStr, fun z ->
                    printfn $"strPong received: %A{z}"
                    let v = z + "b"
                    Send(v, chanStr, fun () ->
                        printfn $"strPong sent: %A{v}"
                        strPongInf chanStr))))

    let intPingpong chanInt =
        Parallel(intPing chanInt, intPong chanInt, fun _ -> End())

    let intPingpongInf chanInt =
        Parallel(intPingInf chanInt, intPongInf chanInt, fun _ -> End())
    
    let intPingpongInfInc chanInt =
        Parallel(intPingInfInc chanInt 0, intPongInf chanInt, fun _ -> End())
    
    let strPingpong chanStr =
        Parallel(strPing chanStr, strPong chanStr, fun _ -> End())

    let strPingpongInf chanStr =
        Parallel(strPingInf chanStr, strPongInf chanStr, fun _ -> End())
    
    let strPingpongInfInc chanStr =
        Parallel(strPingInfInc chanStr "a", strPongInf chanStr, fun _ -> End())
    
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

    let private createSendProcess chanSend chanRecv value name m =
        let rec create n = 
            if n = 1 then
                Send(value, chanSend, fun () ->
                    printfn $"%s{name} sent: %A{value}"
                    Receive(chanRecv, fun v ->
                        printfn $"%s{name} received: %A{v}"
                        End()))
            else 
                Send(value, chanSend, fun () ->
                    printfn $"%s{name} sent: %A{value}"
                    Receive(chanRecv, fun v ->
                        printfn $"%s{name} received: %A{v}"
                        create (n - 1)))
        create m

    let private createRecvProcess chanRecv chanSend name m =
        let rec create n =
            if n = 1 then
                Receive(chanRecv, fun v ->
                    printfn $"%s{name} received: %A{v}"
                    let value = v + 10
                    Send(value, chanSend, fun () ->
                        printfn $"%s{name} sent: %A{value}"
                        End()))
            else
                Receive(chanRecv, fun v ->
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

        let rec createProcessRing procs index m = 
            match procs with
            | pa::pb::[] when index = 0 -> Parallel(createSendProcess pa.ChanSend pa.ChanRecv 0 pa.Name m, createRecvProcess pb.ChanRecv pb.ChanSend pb.Name m, fun _ -> End())
            | pa::pb::[]                -> Parallel(createRecvProcess pa.ChanRecv pa.ChanSend pa.Name m, createRecvProcess pb.ChanRecv pb.ChanSend pb.Name m, fun _ -> End())
            | p::ps when index = 0      -> Parallel(createSendProcess p.ChanSend p.ChanRecv 0 p.Name m, createProcessRing ps (index + 1) m, fun _ -> End())
            | p::ps                     -> Parallel(createRecvProcess p.ChanRecv p.ChanSend p.Name m, createProcessRing ps (index + 1) m, fun _ -> End())
            | _                         -> failwith $"createProcessRing failed! m = %A{m}"

        let chans = [for _ in 1..processCount -> Channel<int>()]

        let processes = createProcesses chans chans 0 []

        createProcessRing processes 0 roundCount
